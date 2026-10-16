# Device snapshot format

This document records the implemented continuation formats for deterministic
snapshot/restore of the Trio/ViRGE and SB16 device models at a stopped
machine execution boundary. It does not add hardware features, change
instruction semantics, or make disk snapshots transactional.

New captures contain DVS1 version 1 records for DMA (kind 2), SB16 including
mixer/OPL (kind 3), and ISA PnP (kind 4), plus the Trio accelerator (kind 1)
on Trio machines. ViRGE engines remain in VID1/VGE1. Every record uses model
0, version 1 and zero flags.

Historical absence, empty trailers and Trio-only trailers survive load/save
byte-for-byte. Restoring those files resets omitted DMA/audio/PnP state to
constructed defaults and rebuilds decode; it cannot reconstruct unsaved audio.
Existing PIC state remains authoritative. Readers predating these records
reject new files instead of silently discarding device state.

### Implemented Trio payload

`T3D1` contains a DWORD magic, WORD version 1 and WORD field count 40, followed
by 40 DWORD slots in the `Field` order declared in
[S3Accelerator.Snapshot.cs](../src/Aurora86.Core/Devices/Video/S3/Trio/S3Accelerator.Snapshot.cs).
These cover register latches, write-mask presence, selectors, partial processor
unit/pixel bytes, transfer presence, geometry, directions, mask and row/column.
A signed DWORD count follows, then sorted `(port:WORD, lowByte:BYTE)` entries.
The payload is 172 bytes plus three bytes per pending port. These entries
preserve the existing PIO low-byte latch behaviour, including retained values
after a high-byte write. Even port addresses are unique and strictly ordered.

Capture clones all state. Validation bounds lengths, fields and cursors before
restore; restore installs raw state without commands, drawing or callbacks.
Host diagnostics (operation counts and trace delegates) remain target-owned.
Machine preflight validates the payload before RAM/CD/device mutation; ViRGE
rejects Trio records. Historical fixtures cover files ending before VID1, at
VID1 and with an empty DVS1 trailer. A historical file cannot recover the old
accelerator settings: reset prevents stale work but does not promise seamless
continuation of a previously unsaved transfer.

Replay tests cover one byte of a word, monochrome expansion, a partially
assembled 24-bit pixel, pending command bytes, 32-bit colour halves, read
selection and an already executed XOR. They compare complete VRAM and engine
state against uninterrupted execution, including a previously used target.
A direct numeric assertion found the existing `PutPixel`/`GetPixel` path
handles the third colour byte only when the pixel size is four. Consequently,
24-bpp continuation is verified against current semantics, but correct 24-bpp
rendering is not claimed; this separate defect is not changed by snapshot work.

### Implemented standalone DMA payload

`DMA1` is exactly 106 bytes: DWORD magic, WORD version 1, WORD channel count 8,
then eight 12-byte channel records in channel order, followed by the two byte
pointer flags (8-bit controller, then 16-bit controller). Each channel stores
base address, base count, current address and current count as little-endian
WORDs, followed by page, mode, masked and terminal-count bytes. Boolean bytes
must be zero or one; every mode byte remains representable under the current
model. Exact length, header and all booleans are validated before mutation.

[Dma8237.Snapshot.cs](../src/Aurora86.Core/Devices/System/Dma8237.Snapshot.cs)
provides capture, validation, raw restore and reset to constructed defaults.
Capture owns its byte array; restore retains the target's channel objects and
memory/bus references and performs no transfer or port access. Callers must
stop device execution. This does not change transfer semantics or introduce
thread safety. `MachineSnapshot` stores this payload in kind 2 and resets omitted historical
DMA state to constructed defaults.

Port-driven tests replay channel 1/5 byte and word transfers with increment,
decrement (mode bit 5), auto-init (mode bit 4), distinct base/current counters,
page bit 0 suppression on word addressing, and terminal-count status at
`08`/`D0` including read-to-clear. Other checks cover both independent byte
pointers during address reads and writes, all eight channel records, reset,
and malformed payload rejection without changing a used target.

### Implemented standalone mixer and OPL payloads

`MIX1` is exactly 278 bytes: DWORD magic, WORD version 1, WORD register count
256, index BYTE, interrupt-status BYTE, then IRQ, DMA8 and DMA16 as signed
DWORDs and the 256 raw register bytes. The resource properties and register
bytes are saved separately: mixer `80`/`81` readbacks come from the owner
properties, and a later stereo write can differ from an earlier Pro alias.
Restore must not replay alias writes or clear a pending IRQ. The current
public properties accept arbitrary integers/status bytes; this serializer
preserves them. Machine preflight checks these against DSP and applied PnP resources.

`OPL1` is exactly 1665 bytes: DWORD magic, WORD version 1, WORD operator count
36, WORD voice count 18, signed DWORD output rate, 512 bank-major register
bytes, two selector bytes, OPL3-mode boolean, timer1/timer2/control/status
bytes, and two IEEE-754 binary64 timer accumulators. The 36 operator records
follow at offset 549, each 27 bytes: ten parameter bytes (multiplier, total
level, key-scale level, attack, decay, sustain level, release, sustain enabled,
key-scale rate, waveform), binary64 oscillator phase, binary64 envelope level,
and envelope-phase byte. The 18 voice records begin at offset 1521, each eight
bytes: F-number WORD, block, key-on, feedback, additive, left and right bytes.
All multibyte values are little-endian; boolean bytes are strictly zero/one.

OPL validation bounds all cached parameters, envelope enums, finite phases and
levels, and timer fractions. A timer fraction can exceed its current period
after a preload rewrite, so its bound is the maximum 256-tick period. Target
preflight also requires the same positive output rate. Cached parameters are
saved raw: waveform and panning interpretations depend on the mode at the time
of a register write, so replaying the banks would lose valid state. Render and
timer algorithms are unchanged; snapshots reject nonfinite/negative timing
state rather than certify invalid caller input to those algorithms.

[SbMixer.Snapshot.cs](../src/Aurora86.Core/Devices/Audio/SbMixer.Snapshot.cs)
and [Opl3.Snapshot.cs](../src/Aurora86.Core/Devices/Audio/Opl3.Snapshot.cs)
provide standalone capture, validation, restore and constructed-default reset.
OPL also exposes target-rate validation used by the enclosing preflight.
Restore performs no register writes, key edges, rendering, timer advancement
or IRQ callbacks. Execution must be stopped; host buffers and audio handles
are outside these payloads. Mixer reset restores resources 5/1/5 and clears
selection/status; OPL reset preserves the target output rate.

Port-driven tests compare nonzero PCM suffixes during attack, decay, sustain
and release, on new and used targets; both banks, all 18 voices and 36
operators, stereo panning and mode-dependent cached values are exercised.
Timers are checked across partial periods, masked/stopped operation, preload
rewrites and latched status. Index/data continuation, mixer aliases/resource
readbacks, malformed atomic rejection and rate mismatch are covered. The
standalone payloads are nested in DSP1 and emitted in the machine kind 3 record.

### Implemented standalone DSP/PCM payload

[SoundBlaster16.Snapshot.cs](../src/Aurora86.Core/Devices/Audio/SoundBlaster16.Snapshot.cs)
provides detached capture, complete validation, target-rate validation, raw
restore and reset to constructed defaults. `DSP1` version 1 has a 108-byte
header, FIFO bytes in read order, queued PCM as little-endian signed shorts,
then complete `MIX1` and `OPL1` payloads. The header stores:

| Offset | Encoding and contents |
| --- | --- |
| 0–7 | DWORD magic, WORD version, WORD header length |
| 8–15 | Signed DWORD output rate and ring capacity in shorts |
| 16–31 | WORD base port, enabled/speaker bytes, signed DWORD IRQ/DMA8/DMA16 |
| 32–37 | Command, reset, last output, identification, test and time-constant bytes |
| 38–43 | Auto-init, 16-bit, stereo, signed, paused and playing boolean bytes |
| 44–55 | Signed DWORD pending/received argument counts and four raw argument bytes |
| 56–67 | Signed DWORD playback rate, block length and remaining transfers |
| 68–91 | Three lossless doubles: sample, resample and FM fractions |
| 92–107 | Signed DWORD FIFO count, PCM short count, MIX1 length and OPL1 length |

Validation checks exact total length using wide arithmetic before count-based
allocation, boolean bytes, parser relationships, rates, nonnegative counters,
finite fractions in `[0,1)`, nested payloads and matching output rates. Block
length and remaining count are independent: command `48` can rewrite the next
block length while a longer current block is still playing. Legacy `1C` can
increase the stored block length above 65536; the serializer preserves it.
Standalone resource integers remain unrestricted; machine preflight requires
agreement with mixer and applied PnP resources and supported decode.

PCM is canonicalized into logical dequeue order. Odd queue counts and odd
capacities are valid because the current host API accepts odd short reads.
Physical ring cursors and unused slots are not guest state. Restore retains
chip objects, DMA/PIC references, bus registrations and the target's diagnostic
`InterruptCount`; it performs no IRQ callback, transfer or render. Callers must
stop both device execution and audio consumption. Restoring resources does not
remap ports: enclosing machine integration rebuilds mappings after preflight.
Reset restores the constructor base/rate, default resources and empty DSP/PCM
and nested state without emitting the guest reset handshake.

Port tests cover partial `41` arguments at `22C`, unread `22A` FIFO/last latch,
reset `226` bit 0, paused and active 8/16-bit auto-init playback around block
completion, signed/unsigned and stereo formats, and IRQ `82` acknowledgement
through `22E`/`22F`. Replay compares queued/future PCM, DMA state, PIC state and
IRQ-count deltas on a used target. Wrapped queues, odd host reads, nonzero
bank-1 FM release, timer fractions, nested selectors, reset and malformed
atomic rejection are also covered. This proves standalone continuation of the
existing audio model. Machine integration is described below; new audio
semantics remain outside this work.

### Implemented PnP payload and machine integration

[IsaPnpBus.Snapshot.cs](../src/Aurora86.Core/Devices/IsaPnp/IsaPnpBus.Snapshot.cs)
stores `PNP1` version 1. Its 21-byte header contains DWORD magic, WORD version,
WORD card count, WORD READ_DATA port, signed DWORD initiation-key cursor,
BYTE address selector, BYTE isolation phase, signed DWORD isolation bit and
BYTE remembered isolation result. Each card stores its nine-byte serial ID,
signed DWORD resource-data length and bytes, BYTE CSN, BYTE state, signed
DWORD serial/resource/selected-LDN cursors, and WORD logical-device count.
Each LDN stores seven ASCII ID bytes, a boolean compatible-ID presence byte
and optional seven ID bytes, then 128 pending register bytes and 128 applied
register bytes. All integers are little-endian.

Applied registers record the last configuration delivered to the chip. Guest
writes while inactive can prepare a new address without changing that applied
configuration. Activation must agree between the two images; while active,
all writable registers must agree. On restore, raw images are copied without
invoking `Configured`, moving a resource cursor, or issuing a port access.
Topology validation matches every card's identity/resource bytes and ordered
logical devices before mutation. The standalone parser bounds cards to 255,
LDNs per card to 256 and resource data per card to 1 MiB, with exact consumption.

[MachineSnapshot.cs](../src/Aurora86.Core/MachineSnapshot.cs)
requires the complete DMA/SB16/PnP set whenever any of those records is present;
Trio additionally requires kind 1. Historical empty and Trio-only sets remain
valid. Before allocating record bodies, the file reader checks exact remaining
length, supported kinds/models/versions/flags, uniqueness, DMA's exact 106 bytes,
Trio's existing maximum, and machine payload ceilings of 64 MiB for DSP and
4 MiB for PnP. Capture rejects payloads beyond those same limits. These are
snapshot-format limits, not new guest FIFO or hardware limits.

Machine preflight validates all new payloads, output rate, the current SB16 and
RTL8019 topology, DSP/mixer/applied-PnP agreement and supported applied resource
choices. Disabled pending register values may be incomplete. It checks proposed
ISA decode together with the saved UHCI BAR4 ranges, fixed PnP ports and OPL
`388..38B`, while allowing current movable mappings on a used target to move
away. Capture additionally requires actual port ownership to match the saved
resources. Unrelated fixed decoders and overlaps are rejected before changing
RAM, CD ownership or devices; invalid active target ranges are also rejected.

Restore removes old enabled ISA ranges first, installs raw DMA/audio/PnP state,
then rebuilds SB16, NE2000 and PnP mappings. Disabled old addresses have no
mapping to remove. It preserves chip instances and does not repeat reset,
key-on, DMA or IRQ callbacks. PIC and existing PCI/USB state are restored by
the enclosing path. NE2000 **configuration** is preserved; packet buffers,
remote DMA progress and backend traffic are not newly captured.

Port-driven tests exercise key and isolation half-pairs, resource/LDN cursors,
inactive pending resources, `60/61`, `70`, `74/75` relocation, audio/network
port swaps, and nonzero 8/16-bit DMA audio with pending IRQs alongside USB PCI
restore. They compare PCM, DMA, PIC, IRQ-count deltas and serialized state on
new/used targets for both video models. Malformed records, mismatched owners,
active/pending disagreement, fixed-port conflicts and invalid target decode
are rejected atomically. Historical files remain byte-identical after resave.

### Caller quiescence audit

The GUI save/restore commands in
[MainWindowViewModel.cs](../src/Aurora86.Gui/ViewModels/MainWindowViewModel.cs)
call `Session.WithMachine` around the entire snapshot operation.
[EmulatorSession.cs](../src/Aurora86.Gui/Emulation/EmulatorSession.cs) uses the
same `_gate` for each emulation batch, stepping and `WithMachine` calls.
[AudioOutput.cs](../src/Aurora86.Gui/Emulation/AudioOutput.cs) obtains core
samples through that same gate. Thus capture/restore excludes both producers
and the core PCM consumer; pausing alone would not establish this boundary.
No new synchronization mechanism is introduced. Already submitted OpenAL
buffers are host state and may finish playing after restore.

SDL restores before creating/running its window. `Virge3DProbe`,
`Win95SetupProbe` and `WindowsDosProbe` use synchronous machine execution and
snapshot calls, with no core audio consumer. External callers retain the same
obligation to exclude emulation and audio reads. Matching CPU frequency and
output rate are required for continuation comparisons; this does not promise
bit-identical audio across runtime architectures.

## Current coverage and gaps

| Owner | Current coverage | Missing continuation state |
| --- | --- | --- |
| [MachineSnapshot](../src/Aurora86.Core/MachineSnapshot.cs) | CPU/RAM, VGA memory/registers, PIC/PIT, keyboard, PCI, USB, DMA, SB16/OPL, ISA PnP, serial mouse and selected firmware state | In-flight IDE and network packet state remain outside this change |
| [S3Accelerator](../src/Aurora86.Core/Devices/Video/S3/Trio/S3Accelerator.cs) | T3D1 plus separately saved VRAM | None identified for the current synchronous engine |
| [ViRGE engines](../src/Aurora86.Core/Devices/Video/S3/Virge/S3VirgeCard.Snapshot.cs) | VGE1 wraps triangle, BitBLT and primary Streams state | No new engine block needed for the implemented synchronous triangle subset |
| [Dma8237](../src/Aurora86.Core/Devices/System/Dma8237.cs) | Standalone DMA1 capture/restore for all eight channels and both byte pointers | None identified for the current synchronous DMA model |
| [SbMixer / Opl3](../src/Aurora86.Core/Devices/Audio/SbMixer.Snapshot.cs) | Standalone MIX1/OPL1 with raw mixer, synthesis and timer state | No new synthesis fidelity claim |
| [SoundBlaster16](../src/Aurora86.Core/Devices/Audio/SoundBlaster16.Snapshot.cs) | Standalone DSP1 with nested MIX1/OPL1 | Existing audio-model limitations described below |
| [IsaPnpBus](../src/Aurora86.Core/Devices/IsaPnp/IsaPnpBus.cs) | PNP1 bus/card/LDN state plus applied configuration | Network packet continuation is excluded |

ViRGE colour, depth and future texture bytes stored in VRAM must not be copied
into engine blocks a second time. Its triangles execute synchronously within
a command write, so there is no supported mid-triangle capture boundary.
Do not invent FIFO or asynchronous progress fields absent from the model.
VGA registers already own scanout state; restore invalidates host rendering
without issuing a guest command or drawing into VRAM.

## Format and migration

Keep the existing positional prefix and VID1 video topology block. Add a
length-prefixed DVS1 trailer after VID1. Integers are little-endian. The
payload is: `magic:u32`, `version:u16=1`, `recordCount:u16`, then records with
`kind:u16`, `model:u16`, `version:u16`, `flags:u16=0`, `length:u32`, payload.
Assign record kinds 1=Trio accelerator, 2=DMA, 3=SB16, 4=ISA PnP;
model 0 denotes the current implementation of each kind. Kinds 1 through 4, model 0, version 1 are implemented.
SB16 owns nested versioned mixer/OPL payloads; DMA remains a system device.

Before this format existed, the loader required VID1's declared length to
equal all remaining bytes. The loader now bounds VID1 to its declared length
and parses the trailer
separately. Old readers reject files containing DVS1: compatibility means
new readers accept old files, not that old readers can load new state.
Reject unknown kinds/models/versions, duplicate records, nonzero flags,
truncated data, trailing garbage and invalid field ranges before mutation.
Require exact payload consumption and bounded allocations. Tests must include
existing files ending before VID1 and files ending exactly after VID1.

New captures require DMA, SB16 and ISA PnP records, plus Trio state
only for Trio topology. Keep ViRGE engines in the existing VID1/VGE1 path.
For historical files without DVS1, retain an explicit absent-state marker:
restore resets omitted devices to the target machine's constructed defaults,
clears pending audio/Trio work and documents lost continuation. Never retain
the target's stale transfer or sound. Existing PIC state remains authoritative;
legacy files cannot reconstruct the omitted source of a pending audio IRQ.
Loading and resaving a historical file must preserve absence, rather than
claiming newly captured state. Do not fabricate a modern trailer after a
legacy prefix that lacks the required intermediate fields.

## Restore boundary and ownership

Capture/restore requires the emulation loop and audio consumer to be stopped.
The caller audit above establishes this for current frontends; serializers do not introduce
unsynchronised access or claim thread safety. Exclude native audio handles,
frontend buffers, trace delegates, static tables and memory/bus references.
Include the core PCM ring in logical dequeue order: it is already generated
output exposed by `IAudioOutput`, not a native host buffer. Preserve its exact
short count, including an odd count allowed by today's `ReadSamples` API.
Validate output sample rate/ring capacity against the target before mutation.

Preflight every new payload, model, PnP identity/resource combination and
mapping operation before changing CD ownership, CPU, RAM, PIC or any device.
Parse into temporary validated state. Suppress callbacks while restoring raw
latches. Restore RAM/VRAM and engines, DMA, SB16/OPL, PnP state and mappings,
then reconcile the captured PIC line/latch state without `RaiseIrq`, DSP reset
commands or key-on writes. Validate cross-owner resource consistency rather
than choosing arbitrarily between mixer, SB16 and PnP values. Derived mappings
must be rebuilt once; reconstruction must not execute transfers or reset a
restored engine. Existing video reconciliation and TLB invalidation remain.

Machine audio time currently derives from each `RunInstructions` batch in
`Machine.Run.cs`; there is no separate audio scheduler remainder there.
Save the device fractions and require matching CPU frequency/output rate for
replay tests. This contract does not promise bit-identical PCM across runtime
architectures with different floating-point behaviour.

## Known limitation: combined PCM/FM mixing

`RenderFmOverPending` calls `RenderFmFrames(0)` during digital playback, so
combined PCM/FM mixing is a separate, unresolved behaviour question. Snapshot
continuation preserves this current semantics and does not by itself
establish that OPL continuation is correct when PCM and FM play together.

## Nominal CPU clock

The sectioned container now captures `CCLK` version 0: one four-byte,
little-endian signed integer containing the nominal CPU frequency in Hz.
Its supported range is 1 MHz through 1 GHz, matching `cpuFrequencyHz` in the
VM configuration. Restore refuses a different target clock before changing
RAM, CPU or CD ownership, because device deadlines and cycle counts cannot
replay at another instruction-to-time ratio. A missing `CCLK` section means
the historical 25 MHz default. Legacy files taken with a manually changed
runtime clock did not record it; their intended ratio cannot be recovered.
The container magic and existing section payloads remain unchanged.
Capture refuses a machine whose runtime clock lies outside that range rather
than write a file that loading would reject. A build older than `CCLK`
ignores the unknown section and restores at its own clock without warning;
nothing in the file can stop it.
