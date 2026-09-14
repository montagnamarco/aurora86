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

These are observed bus values. The current engine ignores several of these
registers and rejects the command; reading its implemented latches alone would
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
