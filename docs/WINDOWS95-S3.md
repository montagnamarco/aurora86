# Windows 95 and the S3 Trio64V+

For the later PCI driver and Device Manager audit, see
[Windows 95 video validation](WINDOWS95-VIDEO-VALIDATION.md). Desktop rendering
alone does not establish that the native PCI driver starts without warnings.

This note records the Windows 95 display behavior verified in disposable
cold-boot copies. It covers measured display regressions and their limits; it
is not a claim of complete Windows 95 compatibility.

## What works

Fresh cold boots reach the Windows login dialog and desktop in both modes:

| Mode | Geometry | Pixel format | Pitch | Observed state |
| --- | --- | --- | ---: | --- |
| `0114` | 800×600 | 16 bpp | 1600 bytes | Desktop, eight icons, Start, cursor, and Display Properties rendered correctly. |
| `0221` | 800×600 | 32 bpp | 3200 bytes | Desktop, eight icons, Start, cursor, and Display Properties rendered correctly; S3 operations were non-zero. |

The 32-bit guest issues `INT 10h` mode request `AX=4F02h`, `BX=8221h`; the
driver does not fall back to `BX=8115h`. The 32-bit state reads `CR50=B0h`
and `CR67=00h`; the 16-bit state reads `CR50=90h` and `CR67=00h`.

The login, desktop, Start menu, and Display Properties checks used the real
guest input path. In the 32-bit mode, dragging Display Properties 100 pixels
right and 60 pixels down preserved the window contents and repainted the
exposed desktop without remnants.

## What the emulator had to get right

- Word-sized `LAR` and `LSL` must preserve the upper half of their
  destination register. Without this, the bitmap address changes from
  `00441094` to `00001094` during icon conversion. Dedicated tests check
  both word-sized instructions.
- Removing the persistent shell icon cache that contained a corrupt large
  bitmap lets Windows regenerate all eight desktop icons with their expected
  colors. The large 128×1088 4-bpp bitmap contained 69,632 zero pixel bytes;
  the smaller bitmap was not entirely zero.
- The VBE ROM must emit guest `OUT` instructions for CRTC registers `13h`,
  `51h`, and `43h`, so the Windows VDD observes the pitch selected by the
  mode. Without them, the S3 driver saw 320 bytes per row where 640 were
  required and rejected the display.
- S3 banking through CRTC `35h` must select the legacy CPU window, while the
  newer bank path remains available, or the cursor upload reaches the wrong
  video-memory page and reads zeroes.
- S3 mix and direction handling, write masking, `BUS_SIZE=11b` doubleword
  transfers, monochrome row padding, and 32-bit pixel assembly must follow
  the observed driver sequences and the documented/reference register
  behavior. Classic-MMIO word pairs with RSF preserve the `00C0C0C0`
  foreground value. Ordinary byte writes apply immediately; command and
  multifunction writes retain their required high-byte timing.

## Known limits

Higher resolutions, 24-bit packed-pixel behavior, and broader application
compatibility remain outside this verification. These checks used cold boots;
the complete accelerator state is not yet serialized, so older snapshots are
not certified. The existing image repair is safe only when performed on a
closed image with a verified backup.

## How to reproduce

Boot a disposable copy of your own Windows 95 image with the S3 Trio64V+
video model, log in through the real input path, and check Device Manager
and Display Properties as described above. The
[Trio guest regression matrix](TRIO-REGRESSION.md) describes the probe and
command sequence used for equivalent guest checks.

## References

The register and instruction behavior was checked against the
[Intel SDM](https://cdrdv2-public.intel.com/774492/325383-sdm-vol-2abcd.pdf),
the [S3 Trio64V+ manual](https://old.vgamuseum.info/images/stories/doc/s3/86c765_trio64vplus.pdf),
and [86Box's S3 implementation](https://github.com/86Box/86Box/blob/master/src/video/vid_s3.c).
