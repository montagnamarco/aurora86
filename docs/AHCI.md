# Intel ICH9 AHCI host bus adapter

`AhciController` models Intel ICH9's AHCI 1.3 SATA host bus adapter: PCI
8086:2922, revision 02, class 01/06/01 (mass storage, SATA, AHCI
programming interface). It is the HBA an AHCI-aware guest drives directly
through a memory-mapped register file -- Windows XP's `msahci`/`storahci`,
Linux's `libahci`, SeaBIOS's own AHCI boot path -- rather than through the
legacy IDE task file `IdeController` models for DOS and Windows 3.1/95.

Primary source: Intel *Serial ATA AHCI 1.3.1 Specification*, October 2012.
Section numbers below are that document's. Slot choice, the card catalog,
`vm.json` wiring, snapshot section registration and any INT 13h path onto
this device are outside this document's scope.

## PCI identity and placement

8086:2922, revision 02h, class 010601h. BAR5 (configuration offset 24h) is
ABAR, a 4 KB (*choice*: covers the generic host control block and six
ports with room to spare) 32-bit non-prefetchable memory window; there is
no BAR0-4 and no I/O space. INTA is wired (offset 3Dh reads 1); Interrupt
Line (3Ch) is plain scratch for the BIOS/OS to fill in. The command
register's memory-space (bit 1) and bus-master (bit 2) bits are writable;
there is no MSI capability (*choice*: INTx is sufficient for every guest
this project targets).

## Generic host control block (ABAR + 00h-2Ch, section 3.1)

| Off | Name | Value |
|---|---|---|
| 00h | CAP | NP=5 (six ports), NCS=31 (32 command slots), SAM=1 (AHCI-only mode, no legacy index/data pair), ISS=3 (Gen3, 6 Gb/s), SCLO=1 (supports PxCMD.CLO, the command-list-override "clear busy status" bit, not COMRESET), S64A=1; every other capability bit (SXS, EMS, CCCS, FBSS, SPM, SNCQ, SSNTF, SMPS, SSS, SALP, SAL) is 0 |
| 04h | GHC | HR (bit 0, self-clearing host reset), IE (bit 1), AE (bit 31, hardwired to read 1 because CAP.SAM=1). A write that sets HR takes priority over anything else in the same dword: it resets every port and forces IE back to 0, exactly as section 10.1.2's initialization sequence expects to observe |
| 08h | IS | not separate storage: bit *n* is the live OR of port *n*'s PxIS & PxIE (section 3.1.2). The usual driver sequence -- clear the pending PxIS bits, then write IS -- still works, because by the time the IS write lands PxIS has already dropped the bits and IS recomputes to 0 |
| 0Ch | PI | 3Fh, fixed: all six ports are implemented (*choice*) |
| 10h | VS | 00010300h: AHCI 1.3.0 |
| 14h/18h/1Ch/20h | CCC_CTL/CCC_PORTS/EM_LOC/EM_CTL | 0, fixed: no command completion coalescing or enclosure management |
| 24h/28h | CAP2/BOHC | 0, fixed |

## Port registers (ABAR + 100h + *n*\*80h, section 3.3)

| Off | Name | Notes |
|---|---|---|
| 00h/04h | CLB/CLBU | command list base, 1 KB aligned. CLBU (and every other *…U* high dword this device reads) is accepted for CAP.S64A but never added to the address: this machine's physical address space fits in 32 bits, and `IDmaMemory` already turns an address past installed RAM into the quiet FFh-reads/dropped-writes a real HBA's own bus fault would produce, never a crash (*choice*, documented at the point each `…U` field is read in `AhciPort.Commands.cs`) |
| 08h/0Ch | FB/FBU | received-FIS base, 256 B aligned |
| 10h | IS | RW1C. Bits implemented: DHRS 0, PSS 1, DSS 2, SDBS 3, UFS 4, DPS 5, PCS 6, DMPS 7, PRCS 22, IPMS 23, OFS 24, INFS 26, IFS 27, HBDS 28, HBFS 29, TFES 30, CPDS 31. Only DHRS, PSS, DPS and TFES are ever actually set by this model; the rest exist so IE can legally enable them and IS can legally clear them. A byte or word write only clears bits within the lanes it actually addressed (see `AhciController.WriteDwordInLanes`'s own summary) -- a single-byte acknowledgement of one pending source must not silently acknowledge an unrelated one sitting in a different lane |
| 14h | IE | same bit layout as IS |
| 18h | CMD | ST (0); SUD (1) and POD (2), read-only, hardwired to 1 because CAP.SSS=0 (section 3.3.7: with staggered spin-up unsupported, software need not manage either bit, and a write to them has no observable effect); CLO (3, self-clearing -- clears TFD's BSY/DRQ bits, which this synchronous engine never leaves set anyway); FRE (4); CCS (12:8, read-only: the last slot processed, or the slot that halted the engine); CR (14, read-only, mirrors ST); FR (15, read-only, mirrors FRE); ICC (31:28, accepted but never stored -- section 3.3.7 has software poll this back down to 0 ("No-Op/Idle") once a requested link-power transition completes, and in this model with no link power state machine that is true the instant after the write). Setting ST clears PxCMD's own TFES halt latch as before; clearing it (a 1→0 transition) additionally clears PxCI and PxSACT to 0 (sections 3.3.13/3.3.14), so a command a driver never saw complete -- including the slot a TFES halt left set -- cannot replay once software restarts the engine |
| 20h | TFD | STS (7:0), ERR (15:8): the packed ATA status/error the last command left |
| 24h | SIG | 00000101h with a disk attached (the ATA signature: sector count 1, LBA 000001h), FFFFFFFFh otherwise (*choice*) |
| 28h | SSTS | DET=3, SPD=3, IPM=1 whenever a disk is attached; all 0 otherwise. This is a direct function of attachment, not of SCTL.DET's own transient state -- see the COMRESET note below |
| 2Ch | SCTL | only DET (3:0) has any effect; the rest is stored, unread by this model |
| 30h | SERR | RW1C, lane-restricted like IS above. Only bit 26 (DIAG.X, "device presence changed") is ever set, by the COMRESET sequence below |
| 34h | SACT | plain storage; NCQ is not implemented (CAP.SNCQ=0), so nothing else reads or writes it. Cleared to 0 whenever ST goes 1→0, alongside CI (see CMD above) |
| 38h | CI | software may only *set* bits (issue a command); the engine clears a bit itself as that command completes, and never on a plain register write. Cleared to 0 as a whole whenever ST goes 1→0 (sections 3.3.13/6.2.2.1) |
| 3Ch | SNTF | RW1C, lane-restricted like IS above; nothing sets it (no hot-plug notification is modelled) |
| 40h | FBS | plain storage; FIS-based switching is unsupported (CAP.FBSS=0) |

### Device detection and COMRESET (sections 3.3.10-3.3.12, 10.1.2)

SSTS/SIG reflect attachment directly: a disk `AttachDisk` put on the port
reports DET=3/SPD=3/IPM=1 and the ATA signature immediately, with no
COMRESET required first (*choice*: this model has no PHY to train, and the
test suite -- and every BIOS/OS that inspects these registers right after
POST -- expects to see the device already there).

Separately, the routine port-init dance a driver performs anyway --
write SCTL.DET=1 (COMRESET asserted), then SCTL.DET=0 (released) -- is
still modelled, because that is also how this HBA *tells the driver* a
device exists: on the 1→0 edge, if a disk is attached, the HBA posts a D2H
Register FIS carrying the ATA signature (LBA low = 1, sector count = 1,
status 50h) into the received-FIS area (gated on PxCMD.FRE, like every FIS
this device posts), sets PxIS.DHRS, and sets PxSERR.DIAG.X, as real HBAs do
on device presence change (taken as given, not independently re-derived
against section 3.3.12's own prose).

## Command engine (sections 4.2, 5.3, 6.2.2.1)

Every command runs to completion synchronously, inside the register write
that issues it (setting CMD.ST with a command already in CI, or writing CI
while ST is already 1) -- there is no PCI arbitration delay and no command
outstanding across two device ticks, exactly as `BusMasterIdeController`
already models for legacy bus-master IDE. FR mirrors FRE and CR mirrors ST
directly (*choice*: no separate start/stop latency).

The engine walks CI from the lowest set slot up. A command that leaves
PxTFD.STS.ERR set (an aborted or unknown command, an out-of-range LBA, a
write to a read-only disk) sets PxIS.TFES, leaves that slot's CI bit *set*,
and stops the walk -- CMD.CCS keeps naming the failing slot -- until
software clears PxCMD.ST (section 6.2.2.1's documented recovery). A
successful command clears its own CI bit and the walk continues to the
next set slot in the same register write.

`PciFunction.BusMasterEnabled` gates the walk itself: with bus mastering
off, CI bits accepted by a write simply stay set, exactly as a real engine
with no bus grant would leave a schedule unwalked.

### Command list, command table, PRDT (section 4.2.2-4.2.3.3)

| Structure | Layout |
|---|---|
| Command header (32 B at CLB + slot·20h) | DW0: CFL (4:0, unchecked -- this model reads the command straight out of the CFIS, not from CFL), A/W/P/R/B/C/PMP (unused), PRDTL (31:16); DW1: PRDBC, written by the HBA with the byte count actually moved; DW2/DW3: CTBA/CTBAU |
| Command FIS (H2D Register FIS, type 27h, at the command table's own offset 0) | byte 2 Command, byte 3 Features, bytes 4-6/8-10 LBA 0-47, byte 7 Device (bit 6 "L": for a 28-bit command, set means bits 3:0 carry LBA 27:24 as before; clear means legacy CHS -- see "Addressing" below), bytes 12-13 Count. The FIS type and the C (command) bit are not validated -- this model trusts a driver-built table, the same simplification `BusMasterIdeController` makes about its own PRD table shape |
| PRDT (16 B entries at the command table's own offset 80h) | DW0/DW1 DBA/DBAU (DBAU ignored, see above); DW3 bits 21:0 byte count minus one, bit 31 the per-entry completion-interrupt flag, which sets PxIS.DPS when that entry finishes |

### Addressing: LBA28, LBA48 and legacy CHS

An `*EXT` command (LBA48) always reads bytes 4-6/8-10 as a 48-bit LBA. A
28-bit command consults the H2D FIS's own Device byte bit 6 ("L"): set,
it is LBA28 exactly as before (bytes 4-6 plus the device byte's own low
nibble). Clear, the same three bytes are legacy CHS -- byte 4 the sector
number, bytes 5-6 the cylinder, the device byte's low nibble the head --
resolved against the attached disk's own physical geometry with
`DiskImage.IsValidChs`/`ToLinearSector`, since AHCI has no INITIALIZE DEVICE
PARAMETERS-style logical geometry to consult the way `IdeController`'s own
CHS branch does. An address CHS cannot resolve reports the same as an
out-of-range LBA: IDNF (ATA error 10h).

### Received FIS area (FB + …, section 4.2.1, 10.3)

Only two of the five documented sub-structures are ever written: the PIO
Setup FIS (type 5Fh) at FB+20h, for a PIO data-in command, and the D2H
Register FIS (type 34h) at FB+40h, for every command's own completion (and
for the COMRESET signature above). DMA Setup, Set Device Bits and Unknown
FIS (offsets 00h/58h/60h) are never posted: this model has no NCQ, no
device-initiated DMA and no FIS it cannot classify. Every FIS write is
gated on PxCMD.FRE -- without it software never told the HBA it may write
there.

PIO Setup is only ever posted ahead of a data-in command's own D2H FIS
(IDENTIFY DEVICE, READ SECTORS/READ SECTORS EXT): PSS, then DHRS. A PIO
*write* (WRITE SECTORS/WRITE SECTORS EXT)
completes like a DMA write, with only the D2H FIS -- *choice*: nothing in
the test list exercises the write-side PIO Setup FIS's own fields, and a
driver's completion handling does not depend on it.

### Commands accepted (ATA/ATAPI-6 codes)

| Command | Behaviour |
|---|---|
| ECh IDENTIFY DEVICE | builds the 512-byte IDENTIFY buffer (below), scatters it through the PRDT, posts PSFIS + D2H |
| 25h/C8h READ DMA EXT/READ DMA, 24h/20h READ SECTORS EXT/READ SECTORS | reads the sector range, scatters through the PRDT (PIO variants also post PSFIS) |
| 35h/CAh WRITE DMA EXT/WRITE DMA, 34h/30h WRITE SECTORS EXT/WRITE SECTORS | gathers the PRDT into a buffer, writes whole sectors to disk |
| 40h/42h READ VERIFY SECTORS/EXT | range check only, no data moved |
| E7h/EAh FLUSH CACHE/EXT | accepted, no-op (every write is already synchronous) |
| EFh SET FEATURES | 03h (transfer mode) accepted for PIO default/flow-control/PIO3-4, multiword DMA 0-2 (20h-22h) and Ultra DMA 0-6 (40h-46h -- wider than `IdeController.SetTransferMode`'s own PIIX4-limited 0-2, matching this HBA's own IDENTIFY words 63/88 below) sub-values, each stored and reported back through IDENTIFY; 02h/82h (write cache) accepted; anything else aborts |
| C6h SET MULTIPLE MODE | accepted, no-op (*choice*: no per-block interrupt pacing to configure) |
| 90h EXECUTE DEVICE DIAGNOSTIC | accepted, ERR register reports 01h ("passed") without STS.ERR being set -- the ATA convention that a diagnostic result is not a failure |
| E5h CHECK POWER MODE | accepted, D2H FIS's own Count field reports FFh (active) |
| E0h/E1h STANDBY IMMEDIATE/IDLE IMMEDIATE | accepted, no-op |
| A1h IDENTIFY PACKET DEVICE, anything else | aborts: this port carries a disk, not an ATAPI device |

### A2: an undersized PRD table is a task-file error, not a silent short transfer

A command whose PRDT (including an empty one, PRDTL=0) describes less room
than the transfer it names (sectorCount·512 bytes, or IDENTIFY's fixed 512)
does not complete successfully. PRDBC still reports how many bytes actually
crossed the PRDT/RAM boundary -- what the HBA genuinely moved off (or onto)
the PCI bus, independent of whether the ATA side of the command went on to
happen -- but the command itself finishes exactly like an unrecognised
command: PxTFD.STS = 51h, ERR = 04h (ABRT), PxIS.DHRS set, and
`ProcessCommandList` promotes that failure into PxIS.TFES and a halted
engine (section 6.2.2.1), exactly as for any other task-file error.

For a **read**, whatever the (undersized) table could receive is still
scattered into guest RAM before the command reports failure -- a real HBA
streaming a DMA in progress has already placed those bytes by the time it
discovers the table is exhausted -- but never a byte past what the PRDT's
own entries described. For a **write**, this model gathers the PRDT into an
internal buffer first and only calls `DiskImage.WriteLinear` once the whole
buffer is genuinely full: an undersized table never reaches the disk image
at all, so a command that mis-describes its own data leaves the guest's
image exactly as it was.

AHCI 1.3.1 also defines PxIS.OFS (bit 24, Overflow Status) for a related
condition (the HBA receiving *more* data from the device than the PRDT can
hold); this model instead reports an undersized PRDT the same way as any
other failed command (PxTFD.STS.ERR/ABRT + PxIS.TFES), since that is the
completion path every driver already has to handle for a command list, and
it lets the same `ProcessCommandList` halt-and-CCS machinery cover it
without a second, parallel failure path (*choice*: both approaches were
open, and this one avoids a second failure path).

### IDENTIFY DEVICE buffer

Shares its non-SATA-specific field choices with `IdeController.IdentifyDisk`
(model/firmware/serial text, CHS geometry, multiple-mode word 47) and adds
what an AHCI/SATA disk needs beyond it:

- Word 49 bit 8 (DMA) alongside bit 9 (LBA).
- Word 63: multiword DMA modes 0-2 supported (bits 2:0 = 7); bits 10:8 name
  the selected mode once SET FEATURES 03h 20h-22h picks one.
- Word 76: SATA capabilities. Bits 3:1 = Gen1/Gen2/Gen3 supported; with
  CAP.ISS=3 this disk advertises all three, 000Eh.
- Word 80 extended through ATA/ATAPI-6 (required to legitimately claim
  LBA48).
- Words 83/86 bit 10 with their own validity bit 14 set (LBA48 supported and
  enabled).
- Words 57-58/60-61: the 32-bit LBA28 total sector count, capped at
  0FFFFFFFh (Lba28MaxSectors) rather than wrapping past it -- the ATA-6
  convention that tells a driver to fall back to LBA48 for a disk's real
  capacity.
- Words 100-103: the 48-bit total sector count, little-endian across the
  four words.
- Word 88: Ultra DMA modes 0-6 supported (bits 6:0 = 7Fh); bits 14:8 name the
  selected mode. Defaults to UDMA5 selected at reset (*choice*: a freshly
  attached SATA disk already has some transfer mode active before any
  driver negotiates one, and UDMA5 is a common real-drive default), then
  tracks whatever SET FEATURES 03h 40h-46h last selected.

## Snapshot

`SnapshotVersion` = 1. `Capture`/`Restore` cover GHC.IE and, per port:
CLB/CLBU/FB/FBU, IS, IE, the CMD bits software actually controls (ST/FRE --
SUD, POD and ICC are hardwired or self-clearing, section 3.3.7, so there is
nothing of theirs to capture) and the derived CCS and TFES-halt latch, TFD
(STS/ERR), SCTL, SERR, SACT, CI, SNTF and FBS, and the SET FEATURES 03h
transfer mode IDENTIFY words 63/88 report. Not captured: the command list,
command tables and received-FIS bytes themselves, which live in guest RAM
and are the machine's own memory snapshot's responsibility; disk contents
and which disk is attached to which port, which are the owner's
machine-composition concern, not this device's.
