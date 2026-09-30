# Windows 95 video validation

## What works

Windows 95 4.00.950 C (Intel486, 16 MB RAM, serial mouse, OSR2.5
installation CD) reaches a working desktop through two configurations,
each verified from a disposable copy of the same disk:

- **Trio, existing legacy `S3` installation.** Desktop at 640×480×8; Device
  Manager reports the legacy device works. The registry binds
  `Root\*PNP0913` to `Display\0003`, using `S3.DRV`/`S3.VXD`.
- **ViRGE, PCI `5333:5631`, with the fixes below.** Desktop at 640×480, 256
  colors through `S3V.DRV` (Microsoft version `4.03.00.2030`, driver date
  8-24-1996). Device Manager reports the device works, with resources
  `A0000-AFFFF`, `B0000-BFFFF`, `3B0-3BB` and following, and no conflicts.
  Display Properties reports 256 Color, 640 by 480 pixels, with no
  invalid-settings warning.

## What the emulator had to get right

Bringing up the ViRGE desktop needed four separate fixes, each with its own
register- or command-level rule and test naming the register or command:

1. **The ROM and VBE service must accept indexed 8-bit mode `0101h`.** The
   Windows 95 ViRGE driver requests this mode (`INT 10h AX=4F02h
   BX=8101h`); rejecting it left the driver unable to switch out of
   standard VGA.
2. **PCI I/O space must be disabled once the driver selects MMIO-only
   access.** After the mode set, the driver writes `SR9=80h` ("MMIO only")
   and reads back `CR40`, `CR59`, `CR5A` and `CR51` through the ports.
   Answering `FFh` there instead of leaving the ports unresponsive made the
   driver conclude the adapter was faulty and fall back to VGA.
3. **The 2D engine must evaluate the three-operand ROP table for 8- and
   16-bit destinations, anchor mono patterns to screen coordinates, and
   consume CPU uploads byte by byte.** The desktop repaint issues BitBLT
   commands for transparent and opaque mono text uploads (`079802E2h`,
   `079800E2h`), mono-pattern PATCOPY and PATINVERT (`07E00920h`,
   `06B40920h`), and an 8-bit rectangle fill (`17E00322h`); rejecting any of
   them leaves the desktop repaint incomplete.
4. **The BIOS must program the CRTC pitch register consistently with the
   doubleword unit the driver assumes.** The driver reads `CR51`, `CR43`
   and `CR13` after the mode set and programs its BitBLT stride as eight
   times the logical width. The doubleword unit is eight bytes (VGA mode
   13h uses `CR13=28h` for a 320-byte pitch under a four-byte unit), so the
   ROM must program `CR13=50h` for mode `0101h` and `CR13=A0h` for mode
   `0110h`; the wrong value drew the desktop on every other scanline with
   doubled icons.

With these fixes, Device Manager (reached by keyboard navigation: Start,
Settings, Control Panel, System, Device Manager) shows the Display adapters
branch with no warning: `S3 ViRGE PCI` reports "This device is working
properly."

The refactor that separated common VGA services (`VgaRegisters`,
`VgaRenderer`) from concrete S3 cards preserved the existing 1136-byte
register snapshot format and the Trio register/mapping behavior; the common
classes no longer contain S3 identification, extension-register handling,
hardware cursor or Streams logic, and the concrete Trio and ViRGE cards
share `VgaCard` ownership.

## Known limits

- The PCI Trio64V+ device (`5333:8811`, `S3MM.DRV`/`S3MM.VXD`) is identified
  correctly and its resource page lists no conflicts, but Device Manager
  reports Code 10 (this device cannot start) for it; the cause is
  unresolved.
- Removing the legacy `S3` device from a Trio installation to make Device
  Manager look clean does not fix this: the sole remaining `S3 Trio64V+`
  entry then reports Code 23 (device failure), and Windows reports the
  adapter is not configured properly. Do not remove the PCI device or
  substitute the generic driver to make the device list appear clean.
- A warm restart of either the Trio or the ViRGE guest did not complete
  within the instruction budgets tried; both remained black in protected
  mode. This is a bounded observation, not a diagnosed cause.
- The same guest configurations also show warning overlays for Standard
  PS/2 Port Mouse (Code 24) and the RTL8019 network adapter (Code 2, device
  failure); the serial mouse remains functional regardless. A clean video
  node must not be read as a clean whole Device Manager.
- This structural refactor validates that the VGA/S3 split preserved
  existing register and snapshot behavior; it does not certify native
  Windows 95 video-driver compatibility beyond the checks above. The
  earlier Trio desktop smoke checks in the
  [Trio guest regression matrix](TRIO-REGRESSION.md) exercised a different
  driver from the PCI Trio64V+ device.

## How to reproduce

Boot a disposable copy of your own Windows 95 image with the Trio64V+ or
ViRGE PCI video model selected, install the matching driver from the
OSR2.5 CD, and open Device Manager (Start, Settings, Control Panel, System,
Device Manager). Check the Display adapters branch for warning icons, and
open Properties on the video device to read its status code and resource
list; open Display Properties to check the configured mode.
