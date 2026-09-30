# Network

The emulated card is an **NE2000** on ISA (port 300h, IRQ 3). The card is
always there; what you choose is where its cable goes.

| Mode | What happens |
|---|---|
| **Unplugged** (default) | transmitted frames come straight back to the card (loopback). That is enough for a driver to find the card and pass its own self-test, and the emulator never touches the host's network. |
| **NAT** | the emulator acts as a router: the guest sees a private network and reaches the world through the host's own ordinary connections. No privileges required. |

## The NAT

The addresses are the ones slirp/QEMU made familiar, so instructions
written for QEMU apply unchanged:

| Who | Address |
|---|---|
| gateway (the emulator) | 10.0.2.2 |
| DNS server | 10.0.2.3 |
| guest (via DHCP) | 10.0.2.15 / 255.255.255.0 |

What the router does, from the bottom up:

- **ARP**: answers for 10.0.2.2 and 10.0.2.3 with its own MAC (52:55:0A:00:02:02). It does not answer for the guest's own address, so a probe made before picking up the address does not find it already taken.
- **DHCP**: a single address, offered to whoever asks for it. A guest configured statically as 10.0.2.15 with gateway 10.0.2.2 works just the same.
- **ICMP echo**: answers immediately for the gateway and the DNS server; towards the outside it pings from the host and, if that returns, rebuilds the reply with the guest's own identifier.
- **DNS**: requests to 10.0.2.3:53 go to the host's first DNS server; the reply comes back as if it came from 10.0.2.3, since otherwise the guest's resolver would discard it.
- **UDP**: one host socket per guest source port, reused for any destination (a "full cone" NAT); closed after 60 s of inactivity.
- **TCP**: the NAT **terminates** the connections. The guest talks TCP to what it believes is the remote server, and the emulator plays that part (sequence numbers, ACKs, windows, retransmission with backoff), while the bytes go over a socket the host connects. A SYN gets no answer until the socket is connected; a refusal becomes an RST. IP fragments are not reassembled and only in-order segments are accepted: on the emulated wire there is neither loss nor reordering, the only possible loss is the card's own full ring buffer, and retransmission covers it.
- **Towards the guest**, at most 8 segments in flight, whatever window it declares: the NE2000's ring holds few frames and a DOS driver drains it slowly.

Everything runs on the emulation thread, inside the card's own calls. The
sockets complete on their own threads and queue what they have to say; the
queue is drained before handling each frame, so the stack never has two
threads inside it and needs no lock.

### Port forwarding

A NAT lets nobody in. To reach a service inside the guest, declare a port
in `vm.json`:

```json
"network": {
  "mode": "Nat",
  "forwards": [
    { "protocol": "tcp", "hostPort": 8080, "guestPort": 80 },
    { "protocol": "udp", "hostPort": 5000, "guestPort": 5000 }
  ]
}
```

From the command line: `--net nat --forward tcp:8080:80`. A connection
that arrives from the host itself (127.0.0.1) presents itself to the guest
as coming from the gateway 10.0.2.2, because to the guest 127.0.0.1 is
"itself".

## In the guest

**DOS** (verified): the Crynwr packet driver `NE2000.COM 0x60 3 0x300`,
then mTCP with `MTCPCFG` pointing at a file with `PACKETINT 0x60`. `DHCP`
picks up the address, `PING 10.0.2.2` tries the router, `PING 1.1.1.1`
really reaches out, `HTGET http://...` resolves the name and downloads
over TCP.

**Windows 3.11 for Workgroups**: Windows' own NE2000 driver, TCP/IP-32
with DHCP. Trumpet Winsock on top of the packet driver works the same way.

## Limits

- No incoming connections without port forwarding.
- No IP fragments, no ICMP beyond echo, no IPv6.
- UDP datagrams larger than 1472 bytes arriving from the host are dropped (no fragmentation).
- Pinging outward depends on how much the host system grants ICMP to an unprivileged process; it works on macOS.
- **Bridged** mode (TAP/pcap) does not exist: it needs a driver and administrator privileges, and the NAT covers ordinary use.
