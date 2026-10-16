# Aurora Display Adapter

A display adapter designed for Aurora86 and the Aurora operating system
rather than copied from a period chip. It is a VGA-compatible PCI card, so
firmware, DOS and a boot loader see an ordinary VGA with VESA modes; an
operating system with a driver additionally gets mode changes without the
BIOS and a 2D engine that executes drawing commands at host speed.

Why a card of our own: in an interpreted emulator every pixel the guest CPU
writes costs tens of emulated instructions. A command such as "fill this
rectangle" or "blend this image" costs one ring entry and runs natively. No
real adapter of the S3 era has the memory for 1920x1080 at 32 bits, and no
modern one can be emulated only in its VGA part: its drivers need the whole
chip.

This document is the contract between the emulator and a guest driver.
Version 1.2 of the interface. All multi-byte values are little-endian.
Version 1.1 appended host-backend discovery; version 1.2 appends a host
preferred display size. The version-1.0 ring and drawing semantics remain
unchanged.

## 1. PCI function

| Field | Value |
|---|---|
| Vendor / device | `1234h` / `A386h` |
| Revision | `01h` |
| Class code | `030000h` (display, VGA compatible) |
| Interrupt pin | INTA (`01h`) |
| BAR0 | video memory, 32 MB, 32-bit prefetchable memory; reset base `E0000000h` |
| BAR1 | registers, 64 KB, 32-bit memory; reset base `E2000000h` |

Command register bits 0 (I/O), 1 (memory) and 2 (bus master) are writable.
BAR0 and BAR1 decode only while bit 1 is set, and the command ring is read
only while bit 2 is set; section 8 gives the exact rule.

The machine firmware places BAR0 and BAR1 at their reset bases and enables
I/O and memory decode during POST.

## 2. Legacy VGA and VESA

The card contains a complete standard VGA (ports 3B0h-3DFh, memory
A0000h-BFFFFh). While the adapter's own scanout is disabled
(`DISPLAY_CONTROL` bit 0 clear), the screen shows the VGA exactly as on any
VGA card.

The BIOS offers these VBE 2.0 modes, all 32 bits per pixel, linear frame
buffer only (mode attributes `00FBh`: no windowed access), memory model 6,
colour layout `X8R8G8B8` (blue in the low byte):

| Mode | Resolution |
|---|---|
| `180h` | 640 x 480 |
| `181h` | 800 x 600 |
| `182h` | 1024 x 768 |
| `183h` | 1280 x 720 |
| `184h` | 1280 x 1024 |
| `185h` | 1366 x 768 |
| `186h` | 1600 x 900 |
| `187h` | 1920 x 1080 |

VBE functions 4F00h, 4F01h, 4F02h, 4F03h, 4F06h and 4F07h are supported
(4F06h and 4F07h only while a valid frame is displayed, and they refuse a
line length or display start that would take the frame out of VRAM);
4F05h, 4F08h and 4F09h answer `014Fh` (not supported: there is no bank
window and no palette in direct colour). Setting a VBE mode programs the
registers of section 4 and enables scanout at VRAM offset 0; the frame
buffer's physical address is BAR0. Setting any VGA mode (INT 10h AH=00h, or
4F02h with a number below 100h) disables the adapter's scanout.

## 3. Pixels

A pixel is a 32-bit little-endian value `AARRGGBB`: byte 0 blue, byte 1
green, byte 2 red, byte 3 alpha. Scanout ignores alpha. Alpha in drawing
commands is **premultiplied**: colour channels are already multiplied by
alpha, so an opaque pixel has `AA = FFh` and a fully transparent one is
`00000000h`. Source-over blending computes, per channel `c`:

    result = source + destination * (255 - sourceAlpha) / 255

rounded to nearest. A driver that keeps its images premultiplied gets
correct antialiased edges and never has to divide.

## 4. Registers (BAR1)

Registers are 32 bits wide and must be accessed with aligned 32-bit
accesses. A narrower read returns the corresponding bytes of the register;
a narrower write is ignored. Unlisted offsets read 0 and ignore writes.

| Offset | Name | Access | Meaning |
|---|---|---|---|
| `000h` | `ID` | R | `55504741h` ("AGPU") |
| `004h` | `VERSION` | R | `00010002h`: major 1, minor 2 |
| `008h` | `VRAM_SIZE` | R | bytes of video memory (`02000000h`) |
| `00Ch` | `CAPABILITIES` | R | bit 0 command ring, bit 1 alpha blending, bit 2 hardware cursor, bit 3 interrupts, bit 4 configured native backend, bit 5 host preferred size |
| `010h` | `DISPLAY_CONTROL` | RW | bit 0: scanout from VRAM enabled |
| `014h` | `DISPLAY_WIDTH` | RW | pixels, 1-4096 |
| `018h` | `DISPLAY_HEIGHT` | RW | lines, 1-4096 |
| `01Ch` | `DISPLAY_PITCH` | RW | bytes per line, multiple of 4, at least width x 4 |
| `020h` | `DISPLAY_OFFSET` | RW | VRAM byte offset of the top-left pixel, multiple of 4 |
| `024h` | `DISPLAY_STATUS` | R | bit 0: the programmed geometry is valid and scanout is active |
| `030h` | `INTERRUPT_STATUS` | R/W1C | bit 0 fence, bit 1 command error; writing 1 clears |
| `034h` | `INTERRUPT_MASK` | RW | same bits; INTA is asserted while `STATUS & MASK` is not zero |
| `040h` | `RING_BASE` | RW | physical address of the command ring, 4 KB aligned |
| `044h` | `RING_SIZE` | RW | ring size in bytes: a power of two from 4 KB to 1 MB |
| `048h` | `RING_HEAD` | R | byte offset of the next command the adapter will read |
| `04Ch` | `RING_TAIL` | RW | byte offset one past the last command written; the doorbell |
| `050h` | `RING_CONTROL` | RW | bit 0 enable; writing bit 1 resets head, tail and error (reads 0) |
| `054h` | `FENCE` | R | value of the last completed `FENCE` command |
| `058h` | `ERROR` | R | error code of section 6, 0 when none |
| `05Ch` | `ERROR_OFFSET` | R | ring offset of the command that failed |
| `060h` | `BACKEND_INFO` | R | configured logical backend: 0 software, 1 OpenGL, 2 Vulkan (reserved for implementations) |
| `064h` | `BACKEND_OPERATIONS` | R | configured native operations: bit 0 opaque fill, bit 1 premultiplied source-over fill |
| `068h` | `HOST_PREFERRED_WIDTH` | R | host-recommended guest display width in pixels, or 0 when unavailable |
| `06Ch` | `HOST_PREFERRED_HEIGHT` | R | host-recommended guest display height in pixels, or 0 when unavailable |
| `070h` | `HOST_PREFERRED_GENERATION` | R | increments on each changed width/height pair, modulo 2^32 |

**Host preferred size (1.2).** A driver checks major version 1, minor >= 2
and capability bit 5 before reading the three appended registers. The host
publishes width and height together: either both are 0 (no preference), or
width is 1-1920 and height is 1-1080. A frontend may filter transient or
unusable window sizes before publishing. Repeating the same pair does not
increment the generation. Guest writes to these registers are ignored.

A guest reads generation, width, height, then generation again with aligned
32-bit accesses, and retries if the generations differ. Each aligned read observes one complete host
publication, so equal generations identify a coherent pair, subject to the
normal 32-bit wrap after 2^32 changes. The pair is advisory: it does not
program display geometry, interrupt the guest, or promise that every host
window size is a supported guest mode. The guest validates its own mode and
may retain its current mode.

The preference comes from the host window, but the guest reads it, so it is
snapshot state: AGPU payload version 2 appends width, height and generation
after video memory, and a restore gives back exactly what the guest saw. The
frontend's next publication then compares the host window with the restored
pair and, when they differ, advances the generation past the recorded one --
so a guest that cached generation N at one size always sees a change, even
when a different frontend in a new process publishes its first size. A card
reset keeps the current preference. A newly constructed card starts with 0,0
and generation 0. A version-1 payload predates the registers and restores
them as 0,0 at generation 0.

**Native backend discovery (1.1).** A driver checks the major version, then
minor >= 1 and capability bit 4 before reading the appended registers. Kind
2 names the contract for a future Vulkan implementation; this build does not
create one. A newly configured software backend reports kind/operations 0
and leaves bit 4 clear. These read-only logical registers are snapshot state:
restoring onto a different host provider preserves what the guest sees. The
actual provider may implement the operation in software after restore; native
execution is proved by the host renderer identity and completed-operation
counter, not by these logical registers alone. Explicit frontend attachment
changes or hardware reset reconfigure the logical state.

Register slots 15 and 16, reserved in the first version-1 payloads, store
logical kind and operation bits; slot 17 remains reserved. Old payloads
containing zero in these slots restore logical software. Validation rejects invalid logical metadata before mutating the
card. The native context, textures and host operation counter are not saved.

Core contains only the versioned `IAuroraGpuBackend` contract. Native graphics
bindings live in `Aurora86.Graphics`, attached and owned by the frontend.
The backend receives temporary, bounded pixel storage, never guest physical
addresses or the authoritative VRAM array. Completed output is copied into
VRAM before the operation returns. Refusal or context failure leaves VRAM
unchanged until the software engine performs the operation. This initial
readback model preserves CPU writes, BAR0 reads, fences and snapshot pixels;
it does not promise that native rendering is faster than host software.

The OpenGL provider uses a thread-owned accelerated offscreen CGL context on
macOS. Other hosts retain software rendering until a native context provider
exists. Frontends opt in with `AURORA_GPU_BACKEND=opengl` (or `auto`, allowing
fallback); absence selects software, and so does an unknown value, reported
in the backend description. The variable is host configuration and is not
saved in `vm.json`. `auto` decides by timing native fills against the CPU at
startup, so two runs on the same host may report different backends; the
default, software, is the deterministic choice. This is an implementation of selected
Aurora operations through OpenGL, not a conformant guest OpenGL API.

**Geometry.** The display registers take effect on every write. When the
geometry is invalid -- a dimension outside 1-4096, a pitch not a multiple
of 4 or below `width x 4`, a misaligned offset, or a frame that would end
past VRAM -- the adapter shows black and `DISPLAY_STATUS` bit 0 reads 0,
until the registers are valid again. Changing resolution is therefore: clear
`DISPLAY_CONTROL`, write width, height, pitch and offset, set
`DISPLAY_CONTROL`, check `DISPLAY_STATUS`.

**Latency.** One doorbell may queue up to a megabyte of commands, each
able to touch the whole of VRAM; the emulator runs them all before the
doorbell write returns, so a driver that wants the machine responsive
submits in batches of a frame's worth of work.

**Doorbell.** Writing `RING_TAIL` while the ring is enabled and bus
mastering is on makes the adapter execute commands from `RING_HEAD` up to
the new tail. Offsets are taken modulo `RING_SIZE` and must be multiples of
4; a command may wrap past the end of the ring. The adapter may complete the
commands at any time after the doorbell; a driver learns which are done
from `FENCE` and must not reuse ring space or source memory before the fence
that follows them has completed. (The emulator executes them before the
doorbell write returns; drivers must not rely on that.)

## 5. Commands

Every command starts with a header dword: bits 15-0 the opcode, bits 31-16
the command's total length in dwords, header included. The adapter reads
exactly that many dwords; a length shorter than the opcode requires is an
error, a longer one is allowed and the extra dwords are ignored (room for
future fields).

Rectangles are `x, y, width, height` in pixels, signed 32-bit origin. Every
destination rectangle is clipped to its surface and to the clip rectangle;
a rectangle that clips to nothing is not an error.

A **surface** is a rectangle of 32-bit pixels in VRAM. Surface 0 is always
the displayed frame (display offset, pitch, width, height). Surfaces 1-15
are defined by `DEFINE_SURFACE`.

A **source space** says where source pixels come from: 0 guest physical
memory (read by bus mastering), 1 VRAM. For space 1, the address is a VRAM
byte offset.

| Opcode | Name | Dwords | Fields after the header |
|---|---|---|---|
| `0000h` | `NOP` | 1 | none |
| `0001h` | `FENCE` | 3 | flags (bit 0: raise the fence interrupt), value |
| `0010h` | `DEFINE_SURFACE` | 6 | id (1-15), VRAM offset, pitch, width, height |
| `0011h` | `SET_CLIP` | 6 | flags (bit 0 enabled), x, y, width, height |
| `0020h` | `FILL_RECT` | 8 | surface, flags (bit 0 blend), x, y, width, height, colour |
| `0021h` | `COPY_RECT` | 10 | flags (bit 0 blend), source surface, source x, source y, destination surface, x, y, width, height |
| `0022h` | `BLIT_IMAGE` | 10 | flags (bit 0 blend), source space, source address, source pitch, destination surface, x, y, width, height |
| `0023h` | `DRAW_MASK` | 11 | flags (reserved, 0), source space, source address, source pitch, destination surface, x, y, width, height, colour |
| `0024h` | `DRAW_LINE` | 8 | surface, flags (bit 0 blend), x0, y0, x1, y1, colour |
| `0030h` | `CURSOR_IMAGE` | 5 | source space, source address, hot spot x, hot spot y |
| `0031h` | `CURSOR_MOVE` | 4 | flags (bit 0 visible), x, y |
| `0040h` | `PRESENT` | 3 | flags (bit 0: set display offset), display offset |

- `FILL_RECT` writes `colour`, or blends it source-over when bit 0 is set.
- `COPY_RECT` copies between surfaces, overlapping areas included (it
  behaves as if the source were read completely first): scrolling a window
  is one command.
- `BLIT_IMAGE` copies 32-bit pixels from the source, a row every `source
  pitch` bytes; with bit 0 it blends them source-over.
- `DRAW_MASK` reads one coverage byte per pixel (0 transparent, 255 full)
  and blends `colour` (premultiplied) scaled by the coverage: this is how
  antialiased text is drawn from a glyph cache.
- `DRAW_LINE` draws a one-pixel line including both end points (Bresenham).
- `CURSOR_IMAGE` loads a 64 x 64 premultiplied image (16 KB); a hot spot
  coordinate above 63 is taken as 63. The cursor is composited on the
  displayed picture and never written to VRAM.
- `PRESENT` with bit 0 changes `DISPLAY_OFFSET`, which is how a driver
  flips between two buffers without tearing a half-drawn frame.

## 6. Errors

When a command is invalid the adapter stops **before** executing it:
`RING_HEAD` stays at the command, `ERROR` and `ERROR_OFFSET` are set, the
error interrupt bit is raised, and further doorbells do nothing until the
driver writes `RING_CONTROL` bit 1. Commands before it have completed.

| Code | Meaning |
|---|---|
| 1 | unknown opcode |
| 2 | length shorter than the opcode requires, or zero, or a command running past the tail |
| 3 | surface id not 0-15, or surface not defined |
| 4 | surface definition outside VRAM, with a width or height outside 1-4096, or with an invalid pitch |
| 5 | source space unknown, or a VRAM source outside VRAM, or a VRAM pixel source whose address or pitch is not a multiple of 4 |
| 6 | ring misconfigured: size not a power of two in range, base misaligned, or tail not a multiple of 4 |
| 7 | a `DRAW_LINE` end point outside -32768 to 32767 |

## 7. A minimal driver

1. Find the function by vendor and device; read BAR0 and BAR1; set command
   bits 1 and 2.
2. Allocate a 64 KB ring in physical memory; write `RING_BASE`, `RING_SIZE`,
   `RING_CONTROL = 1`; set `INTERRUPT_MASK` if interrupts are wanted.
3. Set a mode through section 4, or keep the one the boot loader chose.
4. Write commands at the tail, then write the new tail to `RING_TAIL`.
   End each batch with a `FENCE` and wait for `FENCE` to reach its value.

## 8. Decode rule

VGA ports and the A0000h window always answer: PCI lets the boot display
decode its legacy VGA addresses before it has been configured, and the BIOS
writes text there long before any driver runs. BAR0 and BAR1 answer
while command bit 1 is set and the BAR does not overlap RAM, ROM or another
device. Bus mastering (bit 2) gates only the command ring's reads, including
source reads from guest memory.
