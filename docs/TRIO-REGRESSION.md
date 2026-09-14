# Trio guest regression after ViRGE Streams

On 2026-09-14, source revision `cee5f52ee6b178f8d10d91222e9eceee7d5808a6`
passed the following guest smoke checks using `Win95SetupProbe` in Release.
Each run used its own writable disk copy, an Intel 486, 16 MiB RAM and the
Trio64V+ video model. No emulator source or guest drivers were changed.

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

Windows 95 initially requested ScanDisk after a previous improper shutdown.
Enter allowed it to proceed on the private copy, then Esc dismissed the login
dialog. Random text glyphs appeared during startup before ScanDisk; this
reproduces the separately tracked `WIN95-BOOT-LOGO` symptom on Trio. Its cause
is unresolved. The later desktop and Start menu rendered correctly.

## Inputs and reproduction

The original image SHA-256 values below were checked unchanged after all runs:

| Image | SHA-256 |
| --- | --- |
| `images/hdd.img.prima-di-s3-256` (DOS/VGA) | `de48f3d9d23508b62fc9d2f2d13e8d3be6457d94b09f6b6ecb74b0acfeb4f152` |
| `images/hdd-setup-completato.img` (Windows 3.11 S3) | `0781407aaec547b4934e6a283542eef7586b762a5c83242f9238309a82626c4c` |
| `images/win95/hdd-500mb.img` | `88a478d5b666d7dccb16fc4890391af04082effbc802a2d563dfdeeb385bfda8` |

Build and invoke the existing interactive probe with a VM configuration folder
and a new output folder; it copies the configured disk before executing it:

```sh
dotnet build tools/Win95SetupProbe -c Release --no-restore --disable-build-servers -m:1
dotnet tools/Win95SetupProbe/bin/Release/net10.0/Win95SetupProbe.dll --vm <configuration-folder> <new-output-folder>
```

The DOS/Windows 3.11 runs used serial mouse, no network, no floppy and no USB.
Windows 95 used `images/win95/vm.json`, including its existing OSR2.5 ISO and
serial mouse/NAT settings. The installed Windows build was not independently
identified. No network application was exercised.

DOS was sampled after 400 million instructions. After `type win`, Windows 3.11
was sampled after another 500 million. Windows 95 was sampled after 600 million,
then `key 1c`, 300 million, `key 01`, and another 100 million instructions.
The probe's input commands also execute instructions to deliver input.
Use `shot <name>` for the incremental/full comparison and RGB PPM output,
and `save <name>` for a paired disk/snapshot checkpoint. Saved checkpoints
were not restored in this smoke check.

## Local artifacts

Artifacts are local, uncommitted files under `/tmp/trio-matrix-win311`,
`/tmp/trio-matrix-win311-s3` and `/tmp/trio-matrix-win95`. Each directory holds
screenshots, paired checkpoints and a `control.log` containing retained console
output. The Windows 95 startup capture is `boot.ppm`.

| PPM artifact | SHA-256 of the complete PPM file |
| --- | --- |
| `win311/dos-ver.ppm` | `84f0c7252f8c72a22189953b49a2fed429cebd2ba58f3d79ccca390754b7c2f6` |
| `win311/menu.ppm` | `3b7f35b2adac83dbe29ea867032745cfe91434b3761a0315c7722faaadeb40ba` |
| `win311-s3/interaction.ppm` | `1c9d0902fab12e88fc47aae66900ddbdd95d46ca8bb3bbc52b011f0d56bb6f69` |
| `win95/interaction.ppm` | `75744f73f9f1fbca746ccf1578bf001f239c6b8817908d65cdaad4f985263847` |

The probe build passed with zero warnings/errors. The unchanged source revision
already passed 1,787 tests (`Speed!=Slow`) and the solution build during Streams
integration; those checks were not repeated for this documentation-only result.
These smoke checks complete the Trio guest matrix for the first period S3d
frame milestone described in [VIRGE-CUBE.md](VIRGE-CUBE.md). They do not establish
general ViRGE Windows-driver compatibility, texture support, secondary Streams,
or long-running guest stability.
