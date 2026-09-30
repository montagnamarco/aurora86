# VMware backdoor

The I/O port VMware has offered its guests since the late 1990s as a channel
to the host: detection, the absolute mouse, time, screen size hints, and an
RPC channel that carries clipboard and "tools" traffic. Aurora86 implements
a compatible subset so that existing guest software works unchanged -- the
Linux kernel's detection and `vmmouse` driver, open-vm-tools, VMware Tools --
and so that Aurora OS can reach the host through a protocol that already has
a specification.

Sources, cited where each fact is used: open-vm-tools
(`lib/include/backdoor_def.h`, `lib/include/guest_msg_def.h`,
`lib/message/message.c`, `services/plugins/timeSync/timeSync.c`), the Linux
kernel (`drivers/input/mouse/vmmouse.c`, `arch/x86/kernel/cpu/vmware.c`), QEMU
(`hw/i386/vmport.c`, `hw/i386/vmmouse.c`) and the KVM patch series "Add
support for VMware backdoor I/O ports" (Liran Alon, 2017-2018).

## 1. The transaction

- Port `5658h` (low bandwidth). A guest loads `EAX = 564D5868h` ("VMXh"),
  `ECX` = command (low word) and sub-command (high word, MESSAGE only), the
  other registers per command, and executes `IN EAX, DX` with `DX = 5658h`.
  The one instruction reads **and writes** EAX, EBX, ECX, EDX, ESI and EDI.
- Only a 32-bit `IN` with the magic in EAX is a backdoor call. Any other
  access to the port is an ordinary access to a port nothing decodes.
- Port `5659h` (high bandwidth, `REP INSB/OUTSB`) is **not implemented**; the
  RPC channel does not advertise it (`MESSAGE_STATUS_HB` clear), so guests use
  the low-bandwidth path.

## 2. Privilege

VMware lets these ports through even when IOPL and the TSS I/O permission
bitmap would deny them: VMware Tools' Windows installer probes the port from
ring 3 and refuses to install when it faults. KVM reproduces this by
intercepting `#GP` for `IN/OUT/INS/OUTS` on `5658h` and `5659h` only. Aurora86
does the same for the one transaction it implements: a 32-bit `IN` from
`5658h` carrying the magic is handled before the I/O permission check, in any
mode and ring. Every other access keeps the architectural checks.

The backdoor is off unless the machine enables it (a configuration option):
with it off, the port behaves as on a real PC -- a privileged `IN` reads
`FFFFFFFFh` and EBX never echoes the magic, which is what detection code
tests.

## 3. Commands (ECX low word)

| # | Command | In | Out |
|---|---|---|---|
| 1 | GETMHZ | | EAX = processor MHz, EBX = magic |
| 6 | GETSELLENGTH | | EAX = host selection length in bytes (section 8) |
| 7 | GETNEXTPIECE | | EAX = next 4 bytes of the host selection, little-endian (section 8) |
| 8 | SETSELLENGTH | EBX = guest selection length in bytes | (section 8) |
| 9 | SETNEXTPIECE | EBX = next 4 bytes of the guest selection, little-endian | (section 8) |
| 10 | GETVERSION | | EAX = 6, EBX = magic, ECX = 2 (QEMU's defaults: version 6, VMX type "scalable server") |
| 15 | GETSCREENSIZE | | EAX = width << 16 \| height of the displayed picture |
| 23 | GETTIME | | EAX = seconds since 1970, EBX = microseconds, ECX = maximum lag (µs), EDX = 0 |
| 30 | MESSAGE | see section 5 | see section 5 |
| 39 | ABSPOINTER_DATA | EBX = dwords wanted (1 or 4) | EAX, EBX, ECX, EDX = next queued dwords |
| 40 | ABSPOINTER_STATUS | | EAX = queue length in dwords, or `FFFF0000h` when disabled |
| 41 | ABSPOINTER_COMMAND | EBX = sub-command | |
| 46 | GETTIMEFULL | | EAX = magic, ESI:EDX = seconds, EBX = microseconds, ECX = maximum lag |

An unknown command returns `EAX = FFFFFFFFh` and leaves the other registers
unchanged; callers such as open-vm-tools' time sync fall back on that.

## 4. Absolute pointer (`vmmouse`)

- ABSPOINTER_COMMAND sub-commands: `45414552h` enable (queues the version
  `3442554Ah` as one dword), `000000F5h` disable, `53424152h` request absolute,
  `4C455252h` request relative.
- A packet is four dwords: status and buttons (`20h` left, `10h` right, `08h`
  middle; `00010000h` marks a relative packet), X, Y (absolute: 0-FFFFh across
  the screen) and Z (wheel, signed).
- The channel has no interrupt of its own. When a packet is queued the PS/2
  mouse sends one empty packet (header bit 3 set, no motion) on IRQ 12, as
  QEMU does; Linux's `psmouse` hands it to `vmmouse_process_byte`, which then
  drains the real packets through the backdoor.

## 5. RPC channel (MESSAGE, command 30)

Sub-command in ECX's high word (`guest_msg_def.h` enum): 0 OPEN, 1 SENDSIZE,
2 SENDPAYLOAD, 3 RECVSIZE, 4 RECVPAYLOAD, 5 RECVSTATUS, 6 CLOSE. Status bits
returned in ECX's high word: `0001h` SUCCESS, `0002h` DORECV, `0004h` CLOSED,
`0008h` UNSENT, `0010h` CPT, `0080h` HB.

- OPEN: EBX = protocol (`49435052h` "RPCI" guest-to-host requests, `4F4C4354h`
  "TCLO" host-to-guest commands), bit 31 asking for cookies. Returns the
  channel id in EDX's high word and two cookies in ESI and EDI, which every
  later call on the channel repeats.
- SENDSIZE: EBX = request length. SENDPAYLOAD: EBX = the next four bytes.
  When the whole request has arrived the host prepares its reply.
- RECVSIZE: returns EBX = reply length and EDX's high word = 1 (SENDSIZE);
  status SUCCESS, plus DORECV when there is a reply.
- RECVPAYLOAD: EBX in = SUCCESS; returns EBX = the next four reply bytes and
  EDX's high word = 2 (SENDPAYLOAD). RECVSTATUS: EBX = SUCCESS acknowledges.
- CLOSE ends the channel. Eight channels may be open at once.
- RPCI replies start with `"1 "` for success or `"0 "` for failure. Requests
  the host does not act on are answered `"1 "`, so tools do not retry; the
  first real use is the clipboard.

## 6. Snapshots

Section `VMBD`: whether the backdoor was enabled and the absolute pointer's
mode. On restore the pointer mode is taken from the file, while whether the
port answers stays as the receiving machine is configured -- it is a setting,
not guest state. Queued pointer packets and open RPC channels are not saved;
a guest reopens its channels after a failed call, as it does after a real
VMware checkpoint (`MESSAGE_STATUS_CPT`). A file without the section resets
the pointer and closes every channel.

## 7. Host pointer

Front ends deliver relative motion. While a guest driver has enabled the
absolute pointer and asked for absolute packets, the machine adds that
motion to a position on the displayed picture, scales it to 0-FFFFh and
queues a packet for every movement and button change; otherwise the motion
goes to the USB, PS/2 or serial mouse as before.

## 8. Clipboard

Four commands, older than the RPC channel (section 5) and independent of it:
open-vm-tools' compatibility shim (`services/plugins/dndcp/copyPasteCompat.c`)
still drives exactly these when a guest's tools are too old to know about the
RPCI clipboard exchange, and `backdoor_def.h` still declares them alongside
every other command. There is no OPEN/CLOSE step; a transfer is just a
length announcement followed by the payload four bytes at a time.

- GETSELLENGTH: no input; returns the host selection's length in bytes in
  EAX. VMware's own error constants for this call are not published, so
  Aurora86 never returns a negative value: an absent front end or an empty
  host clipboard both answer 0, which every guest already treats as "no
  selection".
- GETNEXTPIECE: no input; returns the next 4 bytes of the host selection in
  EAX, packed little-endian, so a guest reads `ceil(length / 4)` pieces to
  drain it. Reading past the end returns 0.
- SETSELLENGTH: EBX = the guest selection's length in bytes.
- SETNEXTPIECE: EBX = the next 4 bytes of the guest selection, packed
  little-endian.

Everything below this line is Aurora86's own choice, not a fact taken from
open-vm-tools or any other reference implementation, because VMware never
published the numeric limits or the exact error behaviour for this legacy
pair of transfers:

- Text crosses the backdoor as UTF-8 in both directions.
- Both directions are capped at 65436 bytes, (1 << 16) - 100: this is the
  limit recalled from open-vm-tools' copy-paste code, above which a guest
  drops the selection whole. It is not confirmed against the source;
  staying under it costs nothing. A host selection longer than
  that is truncated to the cap at a UTF-8 character boundary -- the
  character that would straddle the cut is dropped whole, never split. A
  guest SETSELLENGTH longer than the cap is ignored outright: every
  SETNEXTPIECE that follows it is discarded until the next SETSELLENGTH
  announces a length Aurora86 is willing to hold.
- GETSELLENGTH takes a snapshot of the host text; GETNEXTPIECE walks that
  snapshot rather than re-reading the host clipboard on every call, so a
  host copy in the middle of a transfer cannot tear what the guest reads.
- The guest's selection is handed to `IHostClipboard.SetText` only once its
  declared length has fully arrived. A zero-length SETSELLENGTH hands
  nothing to the host clipboard at all -- "the guest selection is empty" is
  not the same claim as "replace the host clipboard with an empty string".
- Neither direction's transfer state is part of the snapshot format
  (section 6): a host-selection read in progress and a guest-selection
  write in progress are both dropped on `Reset()` and on `Restore()`, the
  same as a queued pointer packet or an open RPC channel.
