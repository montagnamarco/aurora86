# Windows 95 video validation

## Environment and scope

The 2026-09-15 checks use Windows 95 **4.00.950 C**, Intel486, 16 MB RAM,
serial mouse and the Windows 95 OSR2.5 installation CD. Each adapter runs
against a separate copy of the same disk. Source revision `b03061b` is the
baseline; `a97d94e` separates common VGA services from concrete S3 cards.

The original `images/win95/hdd-500mb.img` SHA-256 at the start is
`4d050e2dd573453c3ba6a8234b14aa2530745758f6ac880dfc1b8705ad3ad97f`.
A backup precedes all experiments. The probe copies its configured disk;
installation changes and registry exports affect disposable copies only.
Local screenshots, register traces and paired disk/snapshot checkpoints are
under `/tmp/video-validation-20260915/`. They are experiment artifacts, not
repository source or a portable guest distribution.

## Baseline findings

| Adapter/path | Observed result |
| --- | --- |
| Trio, existing legacy `S3` installation | Desktop at 640×480×8; Device Manager says this legacy device works. The registry binds `Root\*PNP0913` to `Display\0003`, using `S3.DRV`/`S3.VXD`. |
| Trio, PCI `5333:8811` | Identified as `S3 Trio64V+`, hardware revision 084, using `S3MM.DRV`/`S3MM.VXD`. Device Manager reports Code 10. Its resource page lists `01000000–011FFFFF` and no conflicts. |
| ViRGE, PCI `5333:5631` | Identified as `S3 ViRGE PCI`, hardware revision 000. Installation initially requests `s3v.dll` from `WIN95_05.CAB`: the saved source is `D:\WIN95`, but the attached CD is actually `E:`. Selecting the real CD completes file installation. |
| ViRGE, immediately after installation | No warning overlay in the tree, but Properties reports Code 24. The icon alone is insufficient evidence. |
| ViRGE, subsequent cold boot | Device Manager reports that the device works. Driver files are `S3V.DRV`/`S3V.VXD`, Microsoft version `4.03.00.2030`. Windows still reports invalid display settings and falls back to VGA 640×480 at 16 colors. |
| ViRGE, cold boot at `edc0d0a` | Desktop runs at 640×480, 256 colors through `S3V.DRV`; Device Manager reports the device works, no conflicts, no invalid-settings warning. See "ViRGE desktop at 640x480x8". |

Removing the obsolete legacy `S3` installation on a backed-up Trio experiment
copy does **not** establish a fix: after cold boot the sole remaining
`S3 Trio64V+` entry reports **Code 23** (device failure), and Windows reports that the adapter
is not configured properly. Advanced Display Properties names the correct
`S3MM` files. Do not remove the PCI device or substitute the generic driver
to make the device list appear clean.

The requested warm restarts did not complete within the observed instruction
budgets (950 million additional instructions for Trio, 650 million for
ViRGE); both remained black in protected mode. This is a bounded observation,
not a diagnosed cause. Subsequent cold boots invoked ScanDisk because these
copies had not finished shutting down.

## Measured ViRGE mode rejection

The cold-boot trace contains `INT 10h AX=4F02h BX=8101h`, followed by ordinary
VGA mode `12h`. The driver requests indexed 640×480 mode `0101h`, preserving
video memory. At `a97d94e`, both the VBE service and ROM programming path
only support ViRGE mode `0110h` (RGB1555), so this request is rejected.
This is an emulator firmware limitation independent of successful PCI
detection and driver file installation. The mode extension and its guest
replay are tracked separately from the structural refactor.

## ViRGE desktop at 640x480x8

Four measured defects separated the driver from a working desktop, each fixed
in its own commit with a test naming the register or command:

1. `0ad85c5`: the ROM and VBE service accept mode `0101h` (indexed 8-bit).
2. `b1a3dd2`: after the mode set the driver writes SR9=80h ("MMIO only") and
   reads CR40, CR59, CR5A and CR51 through the ports. Answering FFh made the
   driver conclude the adapter was wrong and fall back to VGA. Only PCI I/O
   space being disabled silences the ports.
3. `592aad7`: the desktop repaint issued 266 BitBLT commands the 2D engine
   rejected: transparent and opaque mono text uploads (`079802E2h`,
   `079800E2h`), mono-pattern PATCOPY and PATINVERT (`07E00920h`, `06B40920h`)
   and an 8-bit rectangle fill (`17E00322h`). The engine now evaluates the
   three-operand ROP table for 8- and 16-bit destinations, anchors mono
   patterns to screen coordinates and consumes CPU uploads byte by byte.
4. `edc0d0a`: the desktop drew on every other scanline with doubled icons.
   The driver reads CR51, CR43 and CR13 after the mode set and programs its
   BitBLT stride as eight times the logical width; the ROM had programmed
   CR13=A0h for a 640-byte pitch under a four-byte unit. The doubleword unit
   is eight bytes (VGA mode 13h uses CR13=28h for 320 bytes), so the ROM now
   programs CR13=50h for `0101h` and A0h for `0110h`.

Cold boot of the driver-installed image at `edc0d0a` reaches the login dialog
and a complete desktop (background, icons, labels, taskbar, tray) at
640x480x8 through S3V.DRV. Device Manager, re-read from that boot by keyboard
navigation (Start, Settings, Control Panel, System, Device Manager): the
Display adapters branch carries no warning, `S3 ViRGE PCI` reports "This
device is working properly", driver date 8-24-1996, resources A0000-AFFFF,
B0000-BFFFF, 3B0-3BB and following with "No conflicts". Display Properties,
Settings: 256 Color, 640 by 480 pixels, no invalid-settings warning. The
remaining warnings in the same guest are the Standard PS/2 Port Mouse and the
RTL8019 adapter, unchanged from before.

## Other warning entries

The same guest configurations also show warning overlays for Standard PS/2
Port Mouse and the RTL8019 network adapter. PS/2 Properties reports Code 24;
the RTL8019 Properties reports Code 2 (device failure). The serial mouse remains
functional. These entries are retained for diagnosis.
A clean video node must not be reported as a clean whole Device Manager.

## Structural verification

The refactor preserves the 1136-byte register snapshot format and existing
S3 register/mapping behavior. Common `VgaRegisters` and `VgaRenderer` no longer
contain S3 identification, extension-register handling, hardware cursor or
Streams implementation. Concrete adapters share `VgaCard` ownership.

- Affected Release video tests: **448/448**.
- Required `Speed!=Slow` Release suite: **1937/1937**, no failures or skips.
- Solution Release build: zero errors; Debug build: zero errors, 22 existing warnings.
- Affected probe Release builds: zero errors.

These checks validate the refactor; they do not certify native Windows 95
video-driver compatibility. In particular, the previous Trio desktop smoke
checks exercised a different driver from the PCI Trio64V+ device in error.
