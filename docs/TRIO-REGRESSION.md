# Trio guest regression after ViRGE Streams

For the later PCI driver and Device Manager audit, see
[Windows 95 video validation](WINDOWS95-VIDEO-VALIDATION.md). Desktop rendering
alone does not establish that the native PCI driver starts without warnings.

## What works

This matrix uses the Trio64V+ video model, an Intel 486 and 16 MiB RAM, each
run on its own writable disk copy, with no emulator source or guest driver
changes:

| Guest | Display | Observed interaction | Incremental/full pixel differences |
| --- | --- | --- | --- |
| MS-DOS 6.22 | 720×400 text | `ver` prints the DOS version and returns to the prompt | 0 |
| Windows 3.11, VGA driver | 640×480 planar | Program Manager File menu opens with F10, Enter and closes with Esc | 0 |
| Windows 3.11, `s3trio.drv` | 800×600, 256 colours | Enter launches Solitaire from Program Manager; cards and board render | 0 |
| Windows 95 | 640×480, 256 colours | Desktop appears; Tab, Enter opens Start; Esc closes it | 0 |

The Windows 3.11 S3 run issued 270 accelerator operations at Program Manager
and 431 after launching Solitaire. Windows 95 issued 402 at the desktop and
543 with Start open. VGA Windows 3.11 issued none. Screenshots were inspected
visually as well as compared against a full redraw by the probe.

## What the emulator had to get right

This matrix exists to confirm that adding ViRGE Streams support elsewhere in
the video stack does not change Trio64V+ behavior: the Trio renderer receives
no Streams state and must retain its existing rendering path (see
[VIRGE.md](VIRGE.md)). This is the guest-level check of that invariant, run
after Streams integration landed.

## Known limits

- Windows 95 requests ScanDisk after an improper shutdown; Enter allows it to
  proceed, then Esc dismisses the login dialog. Random text glyphs have been
  observed during startup, before ScanDisk runs, on the Trio video model
  (`WIN95-BOOT-LOGO`); its cause is unresolved. The later desktop and Start
  menu render correctly regardless.
- This matrix does not establish general ViRGE Windows-driver compatibility,
  texture support, secondary Streams, or long-running guest stability.
- Checkpoints saved during a run are diagnostic captures; they were not
  restored or replayed as part of this check.

## How to reproduce

Build and invoke the interactive probe with a VM configuration folder and a
new output folder; it copies the configured disk before executing it:

```sh
dotnet build tools/Win95SetupProbe -c Release --no-restore --disable-build-servers -m:1
dotnet tools/Win95SetupProbe/bin/Release/net10.0/Win95SetupProbe.dll --vm <configuration-folder> <output-folder>
```

The DOS and Windows 3.11 runs used serial mouse input, no network, no floppy
and no USB. The Windows 95 run used its own VM configuration, including an
OSR2.5 ISO and serial mouse/NAT settings.

Boot each guest to an idle point, deliver the input from the table above
with `type <text>` and `key <code>`, then use `shot <name>` for the
incremental/full-redraw pixel comparison and RGB PPM output, and `save
<name>` for a paired disk/snapshot checkpoint. These smoke checks complete
the Trio guest matrix for the first period S3d frame milestone described in
[VIRGE-CUBE.md](VIRGE-CUBE.md).
