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
