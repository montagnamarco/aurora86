# Rete

La scheda emulata è una **NE2000** su ISA (porta 300h, IRQ 3). La scheda c'è
sempre; quello che si sceglie è dove va il suo cavo.

| Modalità | Cosa succede |
|---|---|
| **Unplugged** (default) | i frame trasmessi tornano indietro alla scheda (loopback). Basta a un driver per trovare la scheda e superare il proprio autotest, e l'emulatore non tocca la rete del computer. |
| **NAT** | l'emulatore fa da router: il guest vede una rete privata e raggiunge il mondo attraverso le connessioni normali del computer. Nessun privilegio richiesto. |

## Il NAT

Gli indirizzi sono quelli resi consueti da slirp/QEMU, così le istruzioni
scritte per QEMU valgono tali e quali:

| Chi | Indirizzo |
|---|---|
| gateway (l'emulatore) | 10.0.2.2 |
| server DNS | 10.0.2.3 |
| guest (via DHCP) | 10.0.2.15 / 255.255.255.0 |

Cosa fa il router, in ordine dal basso:

- **ARP**: risponde per 10.0.2.2 e 10.0.2.3 con il proprio MAC (52:55:0A:00:02:02). Non risponde per l'indirizzo del guest, così un probe prima di prendere l'indirizzo non lo trova occupato.
- **DHCP**: un solo indirizzo, offerto a chi lo chiede. Un guest configurato staticamente come 10.0.2.15 con gateway 10.0.2.2 funziona ugualmente.
- **ICMP echo**: al gateway e al DNS risponde subito; verso l'esterno fa un ping dal computer e, se torna, ricostruisce la risposta con l'identificatore del guest.
- **DNS**: le richieste a 10.0.2.3:53 vanno al primo server DNS del computer; la risposta torna come se venisse da 10.0.2.3, altrimenti il resolver del guest la scarterebbe.
- **UDP**: un socket dell'host per ogni porta sorgente del guest, riusato per qualunque destinazione (NAT "full cone"); chiuso dopo 60 s di inattività.
- **TCP**: il NAT **termina** le connessioni. Il guest parla TCP con quello che crede il server remoto e l'emulatore fa quella parte (numeri di sequenza, ACK, finestre, ritrasmissione con backoff), mentre i byte passano su un socket connesso dal computer. Un SYN non riceve risposta finché il socket non è connesso; un rifiuto diventa un RST. Non si riassemblano frammenti IP e si accettano solo segmenti in ordine: sul filo emulato non c'è né perdita né riordino, l'unica perdita possibile è il ring buffer pieno della scheda, e la ritrasmissione la copre.
- **Verso il guest** al massimo 8 segmenti in volo, qualunque finestra dichiari: il ring della NE2000 tiene pochi frame e un driver DOS li svuota lentamente.

Tutto gira sul thread di emulazione, dentro le chiamate della scheda. I
socket completano su thread propri e mettono in coda quello che hanno da
dire; la coda viene svuotata prima di gestire ogni frame, così lo stack non
ha mai due thread dentro e non ha lock.

### Port forwarding

Un NAT non lascia entrare nessuno. Per raggiungere un servizio nel guest si
dichiara una porta in `vm.json`:

```json
"network": {
  "mode": "Nat",
  "forwards": [
    { "protocol": "tcp", "hostPort": 8080, "guestPort": 80 },
    { "protocol": "udp", "hostPort": 5000, "guestPort": 5000 }
  ]
}
```

Da riga di comando: `--net nat --forward tcp:8080:80`. Una connessione che
arriva dal computer stesso (127.0.0.1) si presenta al guest come proveniente
dal gateway 10.0.2.2, perché per il guest 127.0.0.1 è "me stesso".

## Nel guest

**DOS** (verificato): packet driver Crynwr `NE2000.COM 0x60 3 0x300`, poi
mTCP con `MTCPCFG` che punta a un file con `PACKETINT 0x60`. `DHCP` prende
l'indirizzo, `PING 10.0.2.2` prova il router, `PING 1.1.1.1` esce davvero,
`HTGET http://...` risolve il nome e scarica via TCP.

**Windows 3.11 for Workgroups**: driver NE2000 di Windows, TCP/IP-32 con DHCP.
Trumpet Winsock sopra il packet driver funziona allo stesso modo.

## Limiti

- Niente connessioni in ingresso senza port forwarding.
- Niente frammenti IP, niente ICMP oltre l'echo, niente IPv6.
- I datagrammi UDP più grandi di 1472 byte in arrivo dall'host vengono scartati (non si frammenta).
- Il ping verso l'esterno dipende da quanto il sistema ospite concede l'ICMP a un processo senza privilegi; su macOS funziona.
- La modalità **bridged** (TAP/pcap) non c'è: serve un driver e privilegi di amministratore, e il NAT copre l'uso normale.
