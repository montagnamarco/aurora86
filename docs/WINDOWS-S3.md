# Windows 3.1 with the S3 Trio64V+ driver

For the corresponding Windows 95 measurements, see
[Windows 95 and the S3 Trio64V+](WINDOWS95-S3.md).

## What works

Windows 3.11 Enhanced mode runs at 800x600, 256 colours through the S3
Trio64V+ driver (`S3TRIO.DRV`), including window drag/repaint through the
accelerator.

## What the emulator had to get right

**The installed S3 driver needs its matching enhanced-mode virtual display
driver.** Selecting only `S3TRIO.DRV` while retaining the generic VGA VDD
does not establish a working S3 display: that combination switches to
800x600 but leaves the display black.

The files present in `C:\WINDOWS\SYSTEM` are `S3TRIO.DRV`,
`VDDS3764.386`, and `VGA_ENG.3GR`. The relevant entries in
`C:\WINDOWS\SYSTEM.INI` are:

```ini
[boot]
display.drv=s3trio.drv
386grabber=vga_eng.3gr

[386Enh]
display=vdds3764.386

[DISPLAY]
screen-size=800
color-format=8
dpi=96
```

Keep the other entries and CRLF line endings. Start with `win` (enhanced
mode). This S3 driver explicitly rejects `win /s`; the standard-mode
regression uses `vga.drv` on a separate image copy.

**BitBLT direction must advance independently per axis, from whichever
corner the driver names first.** The driver may supply the bottom-right
corner as the first pixel to transfer rather than the top-left; adding a
positive offset to that corner unconditionally copies the wrong rectangle
and corrupts the window being dragged. `S3Accelerator.CopyRectangle`
advances from the programmed coordinates by +1 or -1 independently on each
axis. The unit regression checks all four directions with overlapping
source and destination, comparing against an untouched copy of the
original pixels. This agrees with the direction handling in
[DOSBox-X's S3 engine](https://github.com/joncampbell123/dosbox-x/blob/master/src/hardware/vga_xga.cpp)
(`XGA_BlitRect`).

Before the fix, dragging Program Manager corrupted the window:

![Program Manager corrupted after moving](images/s3-window-before.png)

After the fix, the same mouse input moves it correctly:

![Program Manager correctly moved](images/s3-window-after.png)

## Known limits

This regression verifies the configured 800x600, 256-colour mode. Other
colour depths and the remaining line/polygon operations are separate
coverage work; it does not establish their completeness.

## Reproducing the guest check

```sh
dotnet run --project tools/S3WindowsProbe -c Release -- <path-to-your-image> <output-folder>
```

The probe boots a temporary copy, types `win`, captures the desktop, moves
its title bar, and saves `before.ppm`, `moved.ppm`, and `commands.log`.
It renders incrementally throughout execution and compares each capture
against a full redraw. It uses COM1 mouse input and reads the S3 hardware
cursor position to guide the movement. The default title-bar point is
`(300,60)`; supply two extra coordinates for another desktop layout.
