# S3d DOS startup probe

This probe runs the original S3d `CUBE.EXE` through the emulated CPU on a
private DOS disk copy. It records startup and unsupported-command evidence;
exit code zero means observation completed, **not** that 3D rendering worked.
It does not program a video mode or submit triangles on the guest's behalf.

Prepare a copy of a DOS image that stops at `C:\>`, preserving its existing
configuration. Place `CUBE.EXE`, `DOS4GW.EXE`, `BACK.BMP`, `NUMBERS.BMP`,
`MARK.BMP` and `TEXTURE.TEX` in its root using the existing FatTool. Keep a
`hdd.img.prima-di-cube` backup before modifying the prepared image. The probe
itself copies that image again to its new output directory as `run.img`.

The measured external input is [S3d Toolkit 2.6](https://github.com/kytulendu/S3DTK/tree/16ea84eb03b5209718106fd0f51ae420698185b2):

| File | SHA-256 |
| --- | --- |
| `BIN/DOS/CUBE.EXE` | `cef874d7b4a462a6f9ee1a54dcf47a93d2b76fb2781697072c3bb2adf8ffb249` |
| `UTILS/DOS4GW.EXE` | `b8265123ac8a189637448618409ef3ecd2e9f3e1a47062c685a02240f688dec1` |

The archive's WinHelp topic “Driver Redistribution to End Users” permits
binary distribution of the Toolkit library. It does not establish a general
redistribution permission for every sample, asset and bundled runtime.
Those files are external inputs and are not included in this repository.

Build and observe startup:

```sh
dotnet build tools/Virge3DProbe -c Release
dotnet tools/Virge3DProbe/bin/Release/net10.0/Virge3DProbe.dll prepared.img /tmp/cube-first
```

The machine uses an Intel 486, 16 MB RAM and the ViRGE card. It runs a bounded
400-million-instruction boot, requires a root DOS prompt, then saves
`prompt.img`, `prompt.snapshot` and `prompt.json`. The manifest binds both
files by SHA-256 and records the Core module identifier. A different Core
build requires a fresh checkpoint.

Repeat with that exact pair, using a new output directory:

```sh
dotnet tools/Virge3DProbe/bin/Release/net10.0/Virge3DProbe.dll /tmp/cube-first/prompt.img /tmp/cube-repeat /tmp/cube-first/prompt.snapshot
```

Keyboard scan codes launch `cube`; the sample receives another 80 million
instructions after typing. `after.txt` and `after.ppm` preserve the resulting
screen. Esc then requests the sample's normal exit, and `exit.txt` records the
screen after 20 million more instructions. Before Esc, the probe also exports
`vram-000000.ppm` and `vram-096000.ppm`, decoding the two observed 640×480
RGB1555 pages directly from VRAM. `depth-12C000.bin` preserves the corresponding
16-bit depth surface; the log records its hash and count of values other than
FFFFh. These fixed surface addresses come from the CUBE capture. They do not
select a displayed page or establish Streams support. The `first-triangle/`
subdirectory captures the same surfaces on the MMIO write following the first
`B500=80C00006` submission. Since drawing is synchronous, this observes its
completed colour and depth before a subsequent command can clear the Z buffer.
`streams-control-at-mmio` records changes in the observed CR67 latch at MMIO
write boundaries, and `primary-stream` records all seven primary latches at
the final display capture. These observations neither issue guest I/O nor
claim to capture every CRTC write. The log contains the RGB hash,
selected INT calls, faults, unsupported card accesses with their original
widths, unsupported 2D and triangle commands, final registers and source-image hashes.
Counts include repeated events; card-access lines are deduplicated by PC,
reason and 4 KB offset page. Each category has a 256-line limit.

`mmio-writes.log` retains the first 8192 decoded new-MMIO writes at offsets
8000h and above, plus the first 64 writes below 8000h after the prompt
checkpoint. Separate limits prevent image uploads from exhausting the register
budget before triangle setup. Writes to unimplemented registers are included. Each line
records its sequence number, CPU location, register offset within the 64 KB
MMIO window, original transaction width and unmasked value. The final lines
report observed and retained counts, making truncation explicit. The card
event runs before dispatch and does not split or replay the transaction.

No physical-memory write watches are installed: the existing bus watch path
splits DWORD writes into bytes and would change ViRGE command semantics.
This is a bounded write trace; it does not record status reads, PIO writes,
linear pixel writes, or accesses outside the enabled new-MMIO decoder.
The card and CPU events only observe execution.

With the experimental VBE firmware and BitBLT engine, the sample requests
INT 10h `AX=4F02`, `BX=0110` and displays its tiled background and instructions
at 640×480. It submits supported
Gouraud/Z triangle `CMD_SET=80C00006` and primary Streams presentation controls.
The displayed `after.ppm` matches the selected `vram-096000.ppm` page, showing
the cube's front face with the sample's vertex colours. Secondary/composition
and FIFO accesses remain unsupported. Esc returns to the DOS prompt. A new prompt checkpoint
is required after the Core build changes. See [ViRGE status](../../docs/VIRGE.md).
