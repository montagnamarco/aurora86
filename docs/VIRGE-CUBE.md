# Observed S3d CUBE drawing requests

This records guest-submitted values from the Toolkit 2.6 DOS CUBE sample,
using the input hashes and procedure in the [probe README](../tools/Virge3DProbe/README.md).
It is a contract for further implementation, not evidence of a rendered cube.
The baseline is VBE mode 0110h on the experimental ViRGE 86C325.

## Capture boundary

The card observer records each decoded new-MMIO write before dispatch, retaining
its original width and value. It does not use physical-memory write watches.
The probe retains the first 8192 writes at offsets 8000h and above and the first
64 below 8000h. Sequence numbers count all writes, including omitted ones.
All offsets below are relative to the 64 KB new-MMIO window at BAR0 + 01000000h.

The measured run observed 4,883,214 writes: 43,879 at offsets 8000h and above,
and 4,839,335 below. The bounded register trace includes all five command values
listed below; it does not establish that later commands use no other values.
The graphics frame and DOS exit screen match the prior uninstrumented probe
byte for byte. Both runs report 18 selected interrupts, 84 fault events and
4,852,563 unsupported notifications. The frame remains black at 640x480.

## First command parameters

Every transaction in these examples is a DWORD write. Values are hexadecimal.
Each row gives the first occurrence of that command in the retained trace.

| Command register/value | Sequence | Setup observed before submission |
| --- | ---: | --- |
| A500=17E00124 | 18 | A4D8=00000000, A4F4=00000000, A4E4=05000000, A504=027F01E0, A50C=00000000 |
| A500=07980822 | 25 | A4D4=A4D8=00000000, A4E4=02800280, A504=00000001, A508=A50C=00000001 |
| A500=079808A6 | 32 | A4DC=0000027F, A4E0=000001DF, A4E4=05000000, A504=027F01E0, A50C=00000000 |
| A500=07980224 | 337938 | A4D4=001EE000, A4D8=00096000, A4E4=05000020, A4F4=0000FFFF, A4FC=00000000, A504=000F0010, A508=00000000, A50C=000C0041 |
| B500=80C00006 | 337965 | B4D4=0012C000, B4D8=00096000, B4DC=0000027F, B4E0=000001DF, B4E4=05000000, B4E8=00000500 |

Before these commands, write 12 sets 8208=00002727. After the third 2D
command, data writes begin at offset 0000, then 0004, 0008 and successive
DWORD addresses. The first values are 108B108B, 108B108B and 108C108B.

The first triangle also supplies:

```text
B53C=FD58FF78 B540=00000330 B544=0088FF78 B548=00000000
B54C=00007F80 B550=00000000
B554=00028000 B558=00000000 B55C=00C80000
B560=FFF00000 B564=0F000000 B568=00033333 B56C=0C800000
B570=00000000 B574=0C800000 B578=00000168 B57C=80C80029
```

These are observed bus values. At the capture revision, the engine ignored several
of these registers and rejected the command; reading its implemented latches alone would
lose the missing parameters. PIO and status reads are outside this capture,
so display-start changes and polling protocol still need separate observation.

## Databook interpretation and next subsets

The reference is the [August 1996 ViRGE databook](https://vgamuseum.info/images/doc/s3/virge.pdf),
sections 15.4, 19.3, 19.4 and 21. This interpretation separates fields documented
by the chip manual from values observed in the bounded guest trace.

MM8208 is the streams FIFO timeout register (21-3/4). Value 2727h sets both
stream timeouts to 39 MCLK and leaves the priority bit clear. It is not a
pixel-format or drawing command. A future latch can mask bits 31:17 and use
the documented reset value 0808h; timing effects remain outside the current
synchronous model.

| Observed A500 command | Required operation |
| --- | --- |
| 17E00124 | 16-bit rectangle fill, PATCOPY (ROP F0), mono pattern, drawing enabled, no clipping. Later instances clear the Z buffer with FFFFh. |
| 07980822 | 8-bit screen copy, SRCCOPY (ROP CC), positive X/Y, inclusive clipping. The first instance copies a 1×1 region onto itself. |
| 079808A6 | 16-bit CPU image transfer, SRCCOPY, inclusive clipping, DWORD alignment and zero first-DWORD offset. Pixel words arrive low word first at offsets 0000h–7FFFh. |
| 07980224 | 16-bit screen copy, SRCCOPY, positive X/Y, no clipping. Transparency is ignored here because the documented image-source prerequisite is clear. |

Each has autoexecute clear, so A500 itself submits the command. The first
implementation can reject other ROPs and image-transfer formats explicitly.
It needs state for an in-progress CPU upload, including snapshot and restore
coverage, before accepting the transfer command. Width in A504 is encoded
minus one; height is the actual count. Bases are aligned to eight bytes,
strides to eight bytes, and coordinate/clip fields are eleven bits. Use the
detailed clipping descriptions in 19-5: high half is left/top, low half is
right/bottom; the appendix summary reverses the horizontal labels.

B500=80C00006 selects Gouraud triangles, 16-bit ZRGB1555 output, hardware
clipping, normal Z buffering, strict source-Z less-than-buffer comparison and
Z updates. Alpha blending, fog and texture processing are not requested.
All 319 triangle submissions retained by the register budget use this value.
The first destination is 096000h; Z starts at 12C000h, both with 1280-byte
strides. Nonzero colour gradients confirm that flat-colour output is insufficient.

The 3D subset needs the missing Z-base/stride and Z-start/delta latches
(B4D4, B4E8, B554, B558, B55C), with compatible snapshot evolution. It must
interpolate signed S8.7 RGB attributes, produce two-byte ZRGB1555 pixels,
and compare/update 16-bit Z before writing colour. Z attributes use S16.15;
the first starting value 00C80000h represents 400, and the observed clear is
FFFFh. These examples support explicit integer-depth tests, but do not establish
all subpixel rounding, colour saturation or fractional-Z conversion behaviour.
Those boundaries need a further reference or hardware measurement before
claiming general bit-exact rendering.

The implementation order is 2D clear/copy/upload with regression vectors,
then Gouraud and Z with explicit coverage tests. Page presentation remains a
separate integration check because this capture does not record PIO writes.
The full demo must render and return to DOS before its compatibility task closes.

## Result with the first BitBLT subset

The 2D implementation now accepts the four measured A500 variants. CUBE's
background and instructions appear at 640x480, with RGB frame hash
`0E1908F147D05A35A0D3DBC5A392D58F921F4E26444C6AFD28E7C3FB97F2CD5D`.
Esc returns to the DOS prompt. The observed fault and selected-interrupt counts
remain 84 and 18; unsupported notifications fall from 4,852,563 to 2,094.
No unsupported 2D command is reported. The 1,728 unsupported triangle
submissions remain, and streams accesses account for the other notifications.

The retained write trace also shows primary streams registers 8180=03000000,
81C8=00000500, 81F0=00010001 and 81F4=027F01E0. Writes to 81C0 and 81D0
alternate between base addresses 00096000 and 00000000. These are evidence for
a later presentation task, not implemented streams functionality. The 2D
background alone must not be treated as proof that page flipping or the
offscreen triangle destination is visible.

The BitBLT engine snapshots all fifteen latches and a frozen upload descriptor
with its row/DWORD position in a 112-byte V2D1 block. VGE1 bundles that block
with the existing 84-byte VRG1 triangle payload. Machine tests save in the middle
of an odd-width RGB1555 row, restore from disk and complete the upload with the
same VRAM as uninterrupted execution. Legacy triangle-only snapshots remain
readable; malformed nested state is rejected before RAM or VRAM changes.


## Gouraud/Z implementation contract

The triangle engine accepts the measured `B500=80C00006` command with signed
S8.7 RGB gradients and S16.15 depth. Register latches now include B4D4, B4E8,
B554, B558 and B55C. The programmed attributes are anchored at point A on the
long edge, as in databook 15-23/24. Each upward scanline advances the Y
accumulators; X deltas advance from that row's integer long-edge origin in
the rendering direction selected by B57C.31. This follows the observed Toolkit
triangle whose white starting vertex must interpolate to green and magenta;
using signed screen-X distance instead saturates its right-to-left colours.
Clipping preserves that origin and the number of elapsed rows. A source depth
must be strictly smaller than the stored word before depth and colour change.
Destination and depth addresses are checked independently against VRAM bounds.

Fractional-edge attribute correction has not been measured on hardware. The
current conversion discards fractional/low RGB bits, saturates the result to
five bits per component, and truncates then saturates depth to a 16-bit word.
These are explicit approximation boundaries, not established silicon rounding
rules. Texture, dithering, blending and other Z comparison commands remain
outside this subset. The legacy constant-colour RGB888 path remains supported.

New captures use a 104-byte VRG1 version 2 payload with 24 latches. The original
19 latches retain their positions; five depth latches are appended. Version 1
restores with zero depth latches. Both bare legacy triangle snapshots and VGE1
bundles containing a version 1 triangle remain loadable and resave byte-identically
before restoration. Restoring either version never submits a command.


## Result with Gouraud and Z

A fresh DOS checkpoint with the new Core builds the first triangle in page
096000h and updates 4,880 Z pixels before the following MMIO write. Its depth
surface SHA-256 is
`103BEA0787506B513A914F17371548AE0453486490557D7E1D0420B2882CED4F`.
The final raw page shows the cube's front face: green at top left, magenta at
top right, blue at bottom left and white at bottom right. Its RGB SHA-256 is
`3FE2C6EC5A71F48B08E13CDA61EA515E53A0565CF8D82967C5161AC512370076`.
At that stage the visible page's hash differed because Streams presentation
was still absent.
At that final boundary the sample has already cleared its depth surface for
the next frame; an all-FFFF dump there does not mean the triangles skipped Z.

The right-to-left regression uses the captured third triangle and the vertex
colours in the Toolkit's [EXAMPLE.C](https://github.com/kytulendu/S3DTK/blob/16ea84eb03b5209718106fd0f51ae420698185b2/SAMPLES/EXAMPLE.C).
Its source SHA-256 is
`1DEA0F4CBEA7FCB14D9CC3C2AA534DE86770FC0646CAADDEE816DDA94D73004A`.
An earlier implementation multiplied the deltas by signed screen-X distance,
which inverted their effect in right-to-left triangles and saturated colours.
The captured vertices establish that these deltas advance in drawing order.
Tests retain that reproducer along with signed X/Y gradients, clipping, both
halves, independent colour/Z bounds, strict depth ordering and CPU snapshots.

The recreated input uses an existing DOS disk backup with its configuration
preserved; the Toolkit binary hashes match the original capture. This run
reports 18 selected interrupts, 102 fault events and 366 unsupported card
notifications, with no rejected BitBLT or triangle commands. These fault counts
are from a different DOS input and do not establish a CPU regression. Esc
returns to `C:\>`, and the probe's source-image hash is unchanged.


## Result with primary Streams presentation

The primary display now follows CR67 bits 3:2 and the seven primary Streams
registers. The final CUBE capture contains `CR67=3C`, `8180=03000000`,
`81C0=00096000`, `81C4=0`, `81CC=0`, `81C8=00000500`, `81F0=00010001`
and `81F4=027F01E0`. These select an unscaled KRGB1555 page at 096000h,
1280 bytes per row, covering the full 640x480 display. The resulting display
RGB hash is `3FE2C6EC5A71F48B08E13CDA61EA515E53A0565CF8D82967C5161AC512370076`,
identical to the raw selected page. The unselected page is partially drawn at
this boundary and has a different hash. The probe neither modifies a display
register nor substitutes the raw image for the renderer's output.

CR67 observations at MMIO boundaries show 00h initially and 3Ch after the mode
setup. It returns to 00h when Esc exits to the DOS prompt. Counts remain 18
selected interrupts and 102 fault events on the same DOS input; unsupported
notifications decrease from 366 to 203. No BitBLT or triangle command is
rejected. The input-image SHA-256 remains unchanged.

Only primary presentation is implemented here. The capture selects primary
opaque composition with `81A0=01000000` and places the secondary window outside
the screen; secondary composition, scaling, chroma/color key and FIFO timing
are still unsupported. The seven primary fields do not have a documented
VSYNC-delayed shadow, so they update immediately. Beam-accurate effects of
mid-scan writes remain unmeasured. `81CC.0` selection of address 1 is covered by
CPU tests, although CUBE instead alternates address 0 in software.

New VGE1 version 2 snapshots use a 20-byte header and include the 36-byte VST1
primary latch block after VRG1 and V2D1. Old VGE1 version 1 and bare VRG1 files
remain readable and resave unchanged; their absent Streams latches reset to
zero on restore. An old snapshot captured while Streams was already enabled
cannot recover presentation registers that were never saved. Tests verify
both old triangle versions, nested-state preflight before RAM/VRAM changes,
page/stride restoration through real CPU MMIO, page-only redraw, window
clipping, zero/odd stride, out-of-range pixels and the unchanged Trio path.
