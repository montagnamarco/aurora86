# Parallel port and printer

The IBM PC's printer port, as the 1981 Technical Reference defined it and
every PC kept: LPT1 at I/O ports 378h-37Ah, interrupt 7. DOS prints through
BIOS INT 17h (`COPY FILE PRN`, `TYPE FILE > PRN`, a program's print menu);
Windows 3.x and 95 drive the ports directly through their own port driver.
Aurora386 fits a printer on the other end that collects each print job and
hands it to the host as a file -- the raw bytes always, and a PDF when the
job is plain text.

Sources: IBM Personal Computer AT Technical Reference (1984), "Parallel
Printer Adapter" and BIOS listing (INT 17h); the IEEE 1284 description of
the Standard Parallel Port for the signal names. Values marked *choice* are
Aurora386's where period hardware varied.

## 1. Registers

| Port | Name | Access | Meaning |
|---|---|---|---|
| 378h | DATA | R/W | the byte on pins 2-9; reads return the last byte written |
| 379h | STATUS | R | bit 7 /BUSY (1 = ready), bit 6 /ACK (0 during the acknowledge pulse), bit 5 PE (1 = paper out), bit 4 SELECT (1 = on line), bit 3 /ERROR (1 = no error); bits 2-0 read 1 (*choice*) |
| 37Ah | CONTROL | R/W | bit 0 STROBE, bit 1 AUTOFEED, bit 2 /INIT (0 = initialize the printer), bit 3 SELECT IN, bit 4 IRQ enable; bits 5-7 read 1 (*choice*: no bidirectional mode) |

An idle, ready printer reads STATUS = `DFh`.

**Handshake.** Software writes the byte to DATA, sets STROBE, then clears
it. The printer takes the byte when STROBE is cleared (the end of the pulse,
as QEMU's model does), then acknowledges: /ACK falls and rises again, and if
CONTROL bit 4 is set that edge raises IRQ 7. The emulated printer is never
busy, so /ACK is not visibly held low; the interrupt is raised at once.

**Initialize.** Clearing /INIT (CONTROL bit 2) resets the printer: the job
in progress is closed.

## 2. Firmware

- BIOS data area `0040:0008` holds `0378h` (LPT1's base); `0040:000A` and
  `0040:000C` hold 0. The equipment word (`0040:0010`) bits 15-14 say one
  printer is fitted.
- INT 17h, DX = printer number. Only printer 0 is fitted; any other
  number answers AH = 01h, a time-out (*choice*: the IBM BIOS returns
  without touching AH when the BIOS data area holds no port for it):
  - AH = 00h: print the character in AL through the handshake above;
  - AH = 01h: initialize (pulse /INIT);
  - AH = 02h: read status.

  Every function returns the status in AH: the STATUS register with bits
  2-0 cleared and bits 6 (/ACK) and 3 (/ERROR) inverted, bit 0 set on a
  time-out. A ready printer answers `90h` (not busy, selected).
- The Plug and Play BIOS lists the port as device `PNP0400` with I/O
  378h-37Ah and IRQ 7, so Windows 95 finds LPT1 without asking the user.
  When a configured ISA card uses IRQ 7 (a Sound Blaster there was common),
  the node lists no interrupt, as many BIOSes of the period did; a card
  configured over 378h-37Ah is refused at machine construction.

## 3. Print jobs

A job is the bytes received between two quiet periods: it closes when no
byte has arrived for two seconds of emulated time, when the printer is
initialized, or when the machine is shut down. Each job is written to the
configured output folder as `print-<date>-<time>-<n>.prn` (the bytes
exactly as sent). When the job is plain text -- printable characters, CR,
LF, FF, TAB and BS only, no escape sequences -- a PDF of it is written too:
A4 pages, monospaced 10-point text, 66 lines per page, FF starting a new
page, CR returning to the start of the line, TAB stops every 8 columns, and
code page 437 characters mapped to their nearest PDF (WinAnsi) equivalent.
Printer languages (ESC/P, PCL, PostScript) are kept as `.prn` only.

## 4. Snapshots

Section `LPT1`: the DATA and CONTROL registers. A job in progress is not
saved; restoring closes nothing and starts the next byte on a new job.
