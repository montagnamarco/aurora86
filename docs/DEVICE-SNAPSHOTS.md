# Device snapshot implementation contract

This is the P3-01 design, based on source `fd3b728` (2026-09-14). It describes
future implementation units, not capabilities of the current snapshot format.
The scope is deterministic continuation of the existing Trio/ViRGE and SB16
models at a stopped machine execution boundary. It does not add hardware
features, change instruction semantics, or make disk snapshots transactional.

## Current coverage and gaps

| Owner | Current coverage | Missing continuation state |
| --- | --- | --- |
| [MachineSnapshot](../src/Aurora386.Core/MachineSnapshot.cs) | CPU/RAM, VGA memory/registers, PIC/PIT, keyboard, PCI, USB, serial mouse and selected firmware state | DMA, SB16, OPL, ISA PnP and Trio accelerator |
| [S3Accelerator](../src/Aurora386.Core/Devices/Video/S3Accelerator.cs) | VRAM is saved separately | Register latches, byte/word sequencing and pending processor transfer |
| [ViRGE engines](../src/Aurora386.Core/Devices/Video/S3VirgeCard.Snapshot.cs) | VGE1 wraps triangle, BitBLT and primary Streams state | No new engine block needed for the implemented synchronous triangle subset |
| [Dma8237](../src/Aurora386.Core/Devices/System/Dma8237.cs) | Not captured | All eight channels and both byte-pointer flip-flops |
| [SoundBlaster16](../src/Aurora386.Core/Devices/Audio/SoundBlaster16.cs) | Not captured | DSP parser/FIFO, playback, timing fractions, resources, mixer, PCM queue and OPL |
| [IsaPnpBus](../src/Aurora386.Core/Devices/IsaPnp/IsaPnpBus.cs) | Not captured | Bus selection/isolation and card/logical-device configuration |

ViRGE colour, depth and future texture bytes stored in VRAM must not be copied
into engine blocks a second time. Its triangles execute synchronously within
a command write, so there is no supported mid-triangle capture boundary.
Do not invent FIFO or asynchronous progress fields absent from the model.
VGA registers already own scanout state; restore invalidates host rendering
without issuing a guest command or drawing into VRAM.

## Format and migration

Keep the existing positional prefix and VID1 video topology block. Add a
length-prefixed DVS1 trailer after VID1. Integers are little-endian. Proposed
payload: `magic:u32`, `version:u16=1`, `recordCount:u16`, then records with
`kind:u16`, `model:u16`, `version:u16`, `flags:u16=0`, `length:u32`, payload.
Assign record kinds 1=Trio accelerator, 2=DMA, 3=SB16, 4=ISA PnP;
model 0 denotes the current implementation of each kind. These identifiers
are reserved by this design and become a wire contract only with implementation.
SB16 owns nested versioned mixer/OPL payloads; DMA remains a system device.

The current loader requires VID1's declared length to equal all remaining
bytes. The new loader must instead bound VID1 to its declared length and parse
the trailer separately. Old readers will reject new files: compatibility means
new readers accept old files, not that old readers can load new state.
Reject unknown kinds/models/versions, duplicate records, nonzero flags,
truncated data, trailing garbage and invalid field ranges before mutation.
Require exact payload consumption and bounded allocations. Tests must include
existing files ending before VID1 and files ending exactly after VID1.

For a current capture, require DMA, SB16 and ISA PnP records, plus Trio state
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
Audit frontend callers before integration; device serializers do not introduce
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

## Ordered implementation units

Each unit uses a direct owner and affected tests. Machine integration runs the
full `Speed!=Slow` suite and solution build. Obtain independent review for any
new concurrency guarantee or destructive transition; do not add these implicitly.

1. **SNAP-01 — Trailer and compatibility.** Files: `MachineSnapshot.cs`,
   snapshot tests. Implement bounded DVS1 parsing and presence tracking,
   initially with no newly claimed device continuation. Test VID1 length versus
   trailer length, legacy round-trip, duplicate/unknown records and truncation.
   Reject malformed state with CPU/RAM/VRAM/PIC/CD ownership unchanged.
   Done when the extension infrastructure is tested without rewriting old bytes
   or accepting unsupported records as successfully restored.
2. **SNAP-02 — Trio accelerator.** Files: `S3Accelerator.cs`, a focused snapshot
   partial/helper, machine integration and accelerator tests. Capture all
   register latches, `_pendingLow`, multifunction/read/colour selectors,
   write-mask-written flag, transfer geometry/directions/mask/row/column,
   `_unit`/`_unitBytes` and `_processorPixel`/`_processorPixelBytes`.
   Exclude diagnostic counters and callbacks. Preserve byte-write ordering;
   restore through raw state, never `Execute`. Tests drive command `9AE8`,
   data `E2E8`, multifunction `BEE8` and colour `A6E8`: capture after one byte
   of a word, midway through monochrome expansion, and during a split 24-bit
   pixel; replay the suffix and compare entire VRAM and subsequent readbacks.
   Verify immediate commands are not repeated. Done when fresh and previously
   used target machines continue identically and legacy pending work resets.
3. **SNAP-03 — DMA.** Files: `Dma8237.cs`, DMA tests. Capture base/current
   address/count, page, mode, mask, terminal-count flag for eight channels and
   `_highByte8`/`_highByte16`. Test channel 1 and 5 port programming, capture
   between low/high bytes, decrement, auto-init mode bit 4, word addressing,
   and terminal-count status read-to-clear. Replay remaining transfers using
   explicit RAM data and compare bytes, address/count and status. Done when
   payload validation and continuation pass without transferring during restore.
4. **SNAP-04 — Mixer and OPL.** Files: `SbMixer.cs`, `Opl3.cs`, audio tests.
   Save mixer index/registers/resources/IRQ status; OPL banks and address
   selectors, mode, operator phase/envelope state, voice parameters, timer
   registers/status and both timer accumulators. Encode doubles losslessly;
   reject invalid enums/nonfinite values. Restore without register writes that
   trigger key-on. Test bank 1 register `05`, key-on `B0`, panning `C0`, timer
   control `04`, index/data sequencing and nonzero PCM after mid-envelope
   capture. Compare the complete PCM suffix and timer status to uninterrupted
   execution. Derived fields may be recomputed only without side effects.
5. **SNAP-05 — DSP playback and PCM.** Files: `SoundBlaster16.cs`, audio tests.
   Save output FIFO order/last byte, pending command/arguments/counts, reset
   latch, identification/test registers, speaker, playback format/block state,
   sample/resample/FM fractions and core ring. Include resources/enabled state
   and nested mixer/OPL. Test partial `41` command arguments through base+`0C`,
   unread DSP FIFO at base+`0A`, paused playback, 8/16-bit auto-init and a
   snapshot immediately before/after block completion. Compare PCM, DMA
   counters and mixer `82` IRQ bits; acknowledge through base+`0E`/`0F`.
   Use a port-programmed DMA buffer, never a live DOS allocation. Done when
   replay preserves queued output and future samples without an extra IRQ.
6. **SNAP-06 — PnP and machine integration.** Files: ISA PnP bus/card classes,
   `MachineSnapshot.cs`, frontend snapshot callers and machine tests. Capture
   read-data port, initiation-key cursor, selected address, isolation state,
   each card's identity/CSN/state/resource cursor/selected logical device and
   logical registers. Include all attached card configuration, not only SB16,
   to preserve bus protocol; this does not claim network packet continuation.
   Test partial isolation, activation register `30`, relocated base `60/61`,
   IRQ `70` and DMA `74/75`, then replay audio on the relocated ports. Validate
   topology and supported resource conflicts before callbacks. Compare captures
   on fresh/dirty targets; assert PIC pending state and absence of duplicate
   IRQ edges. Test old files' explicit reset policy. Done when machine replay,
   malformed/mismatched atomic rejection, caller quiescence audit and full
   integration checks pass. Guest benches are needed only for gaps not proved
   by these constructed port/CPU tests.

The existing `RenderFmOverPending` currently calls `RenderFmFrames(0)` during
digital playback. Combined PCM/FM mixing therefore needs a separate behaviour
investigation; snapshot work must preserve current semantics and must not use
that path alone as evidence that OPL continuation works.
