# Intel High Definition Audio controller

`HdaController` (`src/Aurora386.Core/Devices/Audio/HdaController.cs`, plus its
`.Corb.cs` and `.Streams.cs` partials) and `HdaCodec`
(`src/Aurora386.Core/Devices/Audio/HdaCodec.cs`) implement an ICH9-identity
HD Audio controller with one output-only codec, feeding the host mix through
`IAudioSource`, exactly as `Es1371` does. This document is the register map
and node graph as implemented, with every point the Intel HDA specification
1.0a left open marked **choice**, and its source.

Primary sources: Intel *High Definition Audio Specification*, revision 1.0a;
QEMU `hw/audio/intel-hda.c`, `hw/audio/intel-hda-defs.h` and
`hw/audio/hda-codec-common.h` (the `hda-output` codec's own identity and node
layout); Linux `sound/pci/hda/hda_intel.c` (the driver's own expectations for
CORB/RIRB timing and the interrupt status bits).

## PCI identity

| Field | Value | Note |
| --- | --- | --- |
| Vendor/Device | 8086:293Eh | Intel, ICH9 HD Audio controller |
| Revision | 03h | |
| Class | 04/03/00h | Multimedia, HD Audio programming interface |
| Command | bits 1 (memory), 2 (bus master) writable | no I/O BAR; no MSI |
| BAR0 | 16 KB, 32-bit, non-prefetchable memory | **choice**: the spec only requires 16 KB be reachable; a 64-bit-capable BAR adds nothing this project's guests read |
| Interrupt pin | INTA | Interrupt Line (3Ch) writable, bookkeeping only |
| 44h (TCSEL) | bits 2:0 writable, no effect | **choice**, per the coordinator's own instruction |

BAR0 decodes 0h-1FFh as the registers below; every other offset up to 4000h
reads 0 and discards writes (an empty region of the real chip's decode).

## Global registers (00h-3Fh)

| Offset | Width | Register | Notes |
| --- | --- | --- | --- |
| 00h | 16 | GCAP | 4401h: 64OK=1, BSS=0, ISS=4 (SD0-SD3, input), OSS=4 (SD4-SD7, output) |
| 02h | 8 | VMIN | 00h |
| 03h | 8 | VMAJ | 01h |
| 04h | 16 | OUTPAY | 3Ch |
| 06h | 16 | INPAY | 1Dh |
| 08h | 32 | GCTL | bit0 CRST, bit1 FCNTRL (scratch), bit8 UNSOL (scratch) |
| 0Ch | 16 | WAKEEN | plain RW, bits 14:0 |
| 0Eh | 16 | STATESTS | RW1C, bit0 = codec 0 present |
| 10h | 16 | GSTS | bit1 FSTS RW1C, rest 0 |
| 20h | 32 | INTCTL | bit31 GIE, bit30 CIE, bits 7:0 SIE |
| 24h | 32 | INTSTS | RO, computed live (see "Interrupt gating" below) |
| 30h | 32 | WALCLK | RO, 24 MHz, derived from `Advance`'s own microseconds |
| 34h | 32 | SSYNC (legacy alias) | **choice**: some drivers still probe the pre-1.0a offset; aliases 38h |
| 38h | 32 | SSYNC | plain RW, no stream-sync behaviour modelled |
| 40h-6Fh | -- | command interface | see below |
| 70h | 32 | DPLBASE | bit0 enable, bits 31:7 base (128-byte aligned) |
| 74h | 32 | DPUBASE | **choice**: accepted and read back, but only the low base addresses memory (see "32-bit DMA" below) |
| 80h+n·20h | -- | stream descriptors (n = 0..7) | see below |
| 2084h+n·20h | 32 | LPIB alias (n = 0..7) | **choice**, optional, read-only |

### GCTL and CRST

While GCTL bit 0 (CRST) is clear -- the power-on state -- every register but
GCTL itself reads and stays at its reset value, and every write to them is
dropped (`HdaController.WriteByte`'s own gate). Writing GCTL byte 0 with bit 0
clear resets the whole register file, including GCTL's own other bits, to
power-on defaults (**choice**: a driver forcing CRST clear intends a full
reset regardless of what else that same byte asked for). Writing it with bit
0 set for the first time announces the codec at address 0 in STATESTS bit 0.

### Interrupt gating

Each per-stream INTSTS bit (0-7) mirrors that stream's own **raw** pending
condition (`SDnSTS & (BCIS|FIFOE|DESE)`), *unqualified* by SDnCTL's own
IOCE/FEIE/DEIE: HDA spec 3.3.14 (Interrupt Status Register) does not describe
these bits as gated by SDnCTL's own enables, and QEMU's own `intel-hda.c`
does not gate BCIS by IOCE for them either -- an earlier revision of this
document claimed the opposite; that claim was wrong and has been corrected
here (`HdaController.IsStreamStatusPending`).

SDnCTL's own IOCE/FEIE/DEIE (spec 3.3.13) instead gate whether that raw
condition is allowed to *assert the interrupt line*, alongside INTCTL's own
SIE and GIE (`HdaController.IsStreamInterruptQualified`, folded into bit 31
below). The command interface's own bit 30 (traditionally CIS) is a
different pair of registers spec 3.3.13/3.3.14 does not cover, so it keeps
its own local qualification (**choice**): CORBSTS.CMEI gated by
CORBCTL.CMEIE, RIRBSTS.RINTFL gated by RIRBCTL.RINTCTL, RIRBSTS.RIRBOIS gated
by RIRBCTL.RIRBOIC.

Bit 31 (GIS), the bit that actually drives the PCI interrupt line, is true
only once INTCTL.GIE is set and at least one IOCE/FEIE/DEIE-qualified,
SIE-gated stream bit or the CIE-gated bit 30 is asserted.

No CORB memory-error condition (CORBSTS.CMEI) or RIRB overrun
(RIRBSTS.RIRBOIS) is ever raised by hardware in this model (**choice**, see
"Synchronous CORB/RIRB" below) -- both bits exist purely for a driver to
write RW1C acknowledgements to without effect.

### Recomputing the interrupt line

`HdaController.UpdateInterruptLine` -- which reads INTSTS bit 31 and tells
the PCI interrupt router whether this function's INTA is asserted -- runs
after every write that can move that bit, not only after the events that
first raise it: SDnCTL byte 0 (SRST, RUN, IOCE, FEIE, DEIE), the RW1C writes
to SDnSTS, CORBSTS and RIRBSTS, CORBCTL and RIRBCTL (whose own enables
requalify whatever is already pending), INTCTL itself, GCTL leaving reset,
and a BDL entry's own IOC completion during `Advance`. Missing any of these
would leave the PCI line stuck asserted after the driver acknowledges the
condition that raised it, or stuck clear after enabling an interrupt for a
condition already pending -- both wrong, and both covered by
`HdaControllerTests`' own interrupt-line tests (which read the router/PIC
state directly, the same way `Es1371Tests` and `Rtl8139Tests` do, not just
INTSTS).

### 32-bit DMA (**choice**)

CORBUBASE, RIRBUBASE, DPUBASE and each Buffer Descriptor List entry's own
high address dword are all accepted and read back, but only ever the *low*
base or address is used to reach memory: `IDmaMemory` addresses installed RAM
with a 32-bit address, exactly like every other bus master this machine has
(the IDE bus-master engine, the RTL8139, the ES1371). A driver that programs
a non-zero upper half is therefore never actually reachable here, the same
simplification every other DMA engine on this machine already makes.

## Command interface (40h-6Fh)

| Offset | Width | Register | Notes |
| --- | --- | --- | --- |
| 40h/44h | 32 | CORBLBASE/UBASE | 128-byte aligned |
| 48h | 16 | CORBWP | low byte 0-255; a write re-runs `ProcessCorb` |
| 4Ah | 16 | CORBRP | bits 7:0 read pointer (RO); bit 15 reset handshake (write 1 -> reads 1, RP := 0; write 0 -> reads 0) |
| 4Ch | 8 | CORBCTL | bit0 CMEIE, bit1 CORBRUN |
| 4Dh | 8 | CORBSTS | bit0 CMEI, RW1C, never set by hardware |
| 4Eh | 8 | CORBSIZE | 42h, fixed: 256 entries only |
| 50h/54h | 32 | RIRBLBASE/UBASE | 128-byte aligned |
| 58h | 16 | RIRBWP | low byte 0-255 (RO); bit 15 is a reset *pulse* (RIRBWP := 0), not a latch -- it always reads 0 |
| 5Ah | 16 | RINTCNT | low byte; 0 means 256, the HDA spec's own convention |
| 5Ch | 8 | RIRBCTL | bit0 RINTCTL, bit1 RIRBDMAEN, bit2 RIRBOIC |
| 5Dh | 8 | RIRBSTS | bit0 RINTFL, bit2 RIRBOIS, RW1C; RIRBOIS never set by hardware |
| 5Eh | 8 | RIRBSIZE | 42h, fixed: 256 entries only |
| 60h | 32 | IC | immediate command out |
| 64h | 32 | IR | immediate response in, hardware-owned |
| 68h | 16 | ICS/IRS | bit0 ICB (write 1: execute now, self-clears), bit1 IRV (RW1C, set with the response) -- both bits live in the *low* byte; 69h is reserved |

### Synchronous CORB/RIRB (**choice**)

A real controller walks the CORB in the background, independently of the
guest instruction stream. This model drains every entry from `CORBRP+1` up
to the current write pointer the instant that write pointer changes, or the
instant CORBCTL/RIRBCTL turn either engine on (`HdaController.ProcessCorb`):
a driver that writes CORBWP and immediately polls RIRBSTS never has to wait.
Like every other bus master on this machine, this only ever moves bytes
while `PciFunction.BusMasterEnabled` is set: with it clear, `ProcessCorb`
fetches no CORB entry and writes no RIRB response, exactly as bus-master
DMA elsewhere on this controller (the stream descriptors' own BDL walk) and
on this machine (the IDE engine, the RTL8139, the ES1371) already stops.
Because of this, RIRB overrun (more responses generated than the driver has
drained) cannot happen in any trace this project cares about, so
`RIRBSTS.RIRBOIS` is never set; likewise no CORB memory-error condition is
detected, so `CORBSTS.CMEI` is never set.

RINTFL is set either after `RINTCNT` responses (0 meaning 256) or once the
ring empties with at least one response still short of that count -- both of
the contract's own trigger conditions, checked inside the drain loop and
again once it ends.

The very first command a driver ever sends lands at CORB index 1, not 0: both
CORBWP and CORBRP reset to 0, and the drain condition is `RP != WP`, so the
first verb must be written at `WP = RP + 1 = 1`. This is not an
implementation quirk -- it is how real hardware's own protocol works too
(see Linux's `azx_corb_send_cmd`), and `HdaControllerTests` exercises it
exactly that way.

## Stream descriptors (80h+n·20h, n = 0..7)

SD0-SD3 are input, SD4-SD7 are output, per GCAP's own ISS/OSS split. This
codec is output-only, so SD0-SD3 exist and answer their registers (a driver
enumerates all eight) but only ever write silence into whatever ring they are
pointed at (**choice**, no host capture is modelled -- the same choice
`Es1371`'s own ADC makes).

| Relative offset | Width | Register | Notes |
| --- | --- | --- | --- |
| 00h | 8 | CTL byte 0 | bit0 SRST (handshake, held not pulsed), bit1 RUN, bit2 IOCE, bit3 FEIE, bit4 DEIE |
| 01h | 8 | CTL byte 1 | reserved, no bit defined |
| 02h | 8 | CTL byte 2 | bits 7:4 stream tag; bit 3 (DIR) accepted, ignored -- direction is fixed by the stream's own index |
| 03h | 8 | STS | bit2 BCIS, bit3 FIFOE, bit4 DESE (all RW1C), bit5 FIFORDY (RO, computed: 1 whenever the stream is out of SRST, 0 while held in it) |
| 04h | 32 | LPIB | RO |
| 08h | 32 | CBL | |
| 0Ch | 16 | LVI | 8-bit field (bits 7:0); high byte reserved |
| 0Eh | 16 | FIFOW | fixed 04h, **choice**, not functionally used |
| 10h | 16 | FIFOS | fixed: FFh output, 3Fh input, **choice**, the contract's own suggested constants |
| 12h | 16 | FMT | base rate (bit14), multiplier (bits13:11), divisor (bits10:8), bits/sample (bits6:4), channels-1 (bits3:0) |
| 18h/1Ch | 32 | BDPL/BDPU | 128-byte aligned |

### CTL/STS as one dword

A driver's own 32-bit access to offset 00h reaches CTL (bytes 0-2) and STS
(byte 3) together, exactly as `SDnCTL`+`SDnSTS` form one register on real
hardware. This implementation dispatches every access byte by byte
regardless of the width the guest used (byte, the default word/dword
composition `IMemoryDevice` gives for free), so a driver's own careless
32-bit read-modify-write that echoes back a previously-read STS byte clears
whatever RW1C bit was set in it -- the same pitfall real hardware has, which
is why real drivers mask STS out of such a read-modify-write themselves.

### SRST and RUN

Entering SRST (bit 0 going 0->1) clears the DMA engine's own moving parts --
LPIB, the RW1C status bits and the cached Buffer Descriptor List cursor --
but not what the driver programmed (CBL/LVI/BDPL/BDPU/FMT), which persist
across the handshake. FIFORDY is not one of the bits SRST clears here: it is
computed at read time from the stream's own current SRST bit (see the
register table above), so it reads 0 only while SRST is actually held and
comes back the instant the stream is set up again -- SRST must not be able
to wipe it permanently, matching QEMU's own model. RUN (bit 1) going 0->1
alone does not reset LPIB; it only forces a fresh reload of the cached BDL
entries from guest RAM, since CBL/LVI/BDPL/BDPU may have changed since the
stream last ran.

### Buffer Descriptor List

Each entry is 16 bytes: a 64-bit address (low dword only reachable, see "32-bit
DMA" above), a 32-bit length, and a 32-bit flags word whose bit 0 is IOC.
`LVI+1` entries are cached the first time a running stream needs them after
any register describing the list changes. BCIS is set unconditionally when
an IOC-flagged entry finishes (regardless of IOCE) -- INTSTS bit N mirrors
that raw bit directly (see "Interrupt gating" above); IOCE instead gates
whether the condition may assert the interrupt line. LPIB advances one
frame's worth of bytes at a time and wraps at CBL. An input stream's own
silence write (see "Host mix path" below) never writes past the current
entry's own buffer, even when a frame's byte count does not evenly divide
the entry's length: the last, short write is clamped to what remains.

### Sample decode (**choice**)

8- and 16-bit samples decode to their exact PCM value (unsigned-to-signed for
8-bit, matching `Es1371`'s own `ConvertUnsigned8`). The wider containers
(20/24/32-bit, all carried in a 32-bit slot) are consumed at the correct byte
rate -- the contract's own requirement -- but their *value* is approximated
from the container's own most significant 16 bits; no test exercises their
exact sample values, and no guest this project boots plays audio at those
widths.

### DMA position buffer

When DPLBASE bit 0 is set, every stream's own LPIB is additionally written to
`DPLBASE & ~7Fh + n·8` after each frame it consumes -- the shadow copy a
driver reads to avoid an MMIO round trip.

### Host mix path

Only the output stream (SD4-SD7) whose own CTL byte 2 stream tag matches
`HdaCodec.DacStreamTag` (last programmed by verb 706h) is mixed to the host,
through the same fixed 48 kHz `MixSampleRate` and repeat-based resampling
`Es1371` uses for its own two DACs. The DAC's own stereo output amplifier
(gain and mute, independently per channel) is applied to every frame before
it reaches the mix ring. `IAudioSource.ReadSamples` drains that ring; nothing
bound, or nothing rendered yet, writes nothing and returns 0 -- silence, not
an error, exactly as the interface's own contract calls for.

## HdaCodec node graph (**choice**)

Four nodes, following QEMU's `hda-output` codec:

| NID | Node | Notes |
| --- | --- | --- |
| 0 | Root | Vendor/Device ID 1AF40022h, Revision ID 00100101h -- **choice**: 1AF4h is Red Hat's PCI vendor ID, used purely as an identity token QEMU's own `hda-output` codec also reports; Linux's generic HD Audio parser and Windows' inbox class driver both accept it without a vendor-specific driver |
| 1 | Audio Function Group | Type 01h; AFG caps 0; supported PCM rates 16-bit + 44.1/48 kHz (20060h); supported stream formats PCM (1h); supported power states D0\|D3 (9h) |
| 2 | DAC (Audio Output) | Widget caps 1Dh (stereo, output amp present, amp param override, format override); output amp caps 80034A4Ah (mute capable, step size 03h = 1.0 dB/step, 75 steps, offset 4Ah = the 0 dB point at the top step) |
| 3 | Pin Complex | Widget caps 400101h (stereo, one-entry connection list, type 4); pin caps 10h (output capable, no presence detect); connection list length 1, entry 0 = NID 2; default configuration 01014010h (**choice**: jack present, rear panel, line out, green, association 1 sequence 0) |

Subordinate Node Count (Get Parameter 04h) response format, HDA spec
7.3.4.3: bits 23:16 the starting node number, bits 7:0 the total node count
-- root reports `(1u<<16)|1` (AFG starts at NID 1, one node); the AFG
reports `(2u<<16)|2` (the DAC and pin start at NID 2, two nodes). An earlier
revision of this codec had these two fields swapped; that was a bug, now
fixed.

### Verbs implemented

Narrow (12-bit ID, 8-bit payload): F00h Get Parameter; F01h/701h connection
select; F02h connection list entry; F05h/705h power state (Get always
answers D0 set and actual, per the contract; Set is accepted and ignored --
this codec never suspends); F06h/706h stream tag/channel; F07h/707h pin
widget control; F0Ch/70Ch EAPD; F1Ch/71Ch-71Fh configuration default (Get
returns the whole 32 bits; each Set writes one byte); F20h subsystem ID
(**choice**: always 0, not programmed, no driver behaviour this project
boots depends on it); 7FFh function reset (resets the whole codec).

Wide (4-bit ID, 16-bit payload): 2h/Ah converter format (Set stores the
codec's own copy for read-back; DMA framing is entirely controller-side, per
`SDnFMT` -- **choice**, matching how the codec and controller are separate
chips on a real link that do not read each other's registers in this model);
3h/Bh amplifier gain/mute.

Unknown verbs, or a verb sent to a node that does not implement it, answer 0.

### Amplifier gain/mute payload, HDA spec 7.3.3.7

- **Set** (verb 3h): bit15 selects the output amp, bit14 the input amp,
  bit13/12 left/right, bits11:8 the amplifier index, bit7 Mute, bits6:0 Gain
  (0..74). Only the DAC's (NID 2) output amp at index 0 exists here: a Set
  naming the input amp (bit15 clear), or any index but 0, changes nothing.
- **Get** (verb Bh): bit15 selects the output amp, bit13 left (1) or right
  (0), bits3:0 the amplifier index; response bit7 Mute, bits6:0 Gain. A Get
  for the nonexistent amp (bit15 clear) or any index but 0 answers 0.
  `HdaControllerTests` uses Linux's own Get-Left (A000h) and Get-Right
  (8000h) payloads.

An earlier revision of this codec used a different, *choice*-encoded payload
(no output/input select bit, no index); that was wrong against the real spec
and has been replaced with the layout above, which now matches what an
actual HD Audio driver sends.

Gain step 74 (`MaxAmpGainSteps`), the amplifier's own Offset field (see the
node graph table above), is 0 dB; each step below it attenuates and each
step above it boosts, by (StepSize + 1) x 0.25 dB = 1.0 dB per step
(`AmpStepDecibels`, HDA spec 7.3.4.10's own step-size encoding, with this
codec's StepSize field at 03h). The reset default is **unmuted at gain 74**
(0 dB) -- **choice**: an audible amplifier at reset, matching QEMU's own
`hda-output` codec, which likewise powers up unmuted rather than at minimum
gain; an earlier revision of this codec defaulted to muted at minimum gain,
which left a driver that never touches the amplifier with silence.

## Snapshot

`HdaController.SnapshotVersion` = 1. `Capture`/`Restore` cover every
guest-visible register: the global registers, the command interface's own
state (including CORB/RIRB pointers and the response-since-interrupt
counter), every stream descriptor's registers and DMA cursor (LPIB, the BDL
entry index and byte offset within it, the sample-rate accumulator), and the
embedded codec's node registers. PCI configuration (including the BAR) is
captured separately by `PciBus`. The host mix ring and each stream's cached
BDL entries are host-side presentation/derived state: not captured. On
restore, every stream's `BdlLoaded` flag is left false so the next `Advance`
rebuilds its cached entries from the restored CBL/LVI/BDPL/BDPU and guest RAM
(itself restored separately by a full machine snapshot) -- the same pattern
`Es1371`'s own DMA channels use for their own runtime cursor.
