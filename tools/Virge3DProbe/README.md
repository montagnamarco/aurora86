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
screen after 20 million more instructions. The log contains the RGB hash,
selected INT calls, faults, unsupported card accesses with their original
widths, unsupported triangle commands, final registers and source-image hashes.
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

With the experimental VBE firmware, the sample requests INT 10h `AX=4F02`,
`BX=0110` and reaches a black 640×480 graphics frame. It still accesses
unimplemented 2D registers and submits unsupported `CMD_SET=80C00006`.
Esc returns to the DOS prompt. This is a reproducible missing-capability
result, not a rendered cube. A new prompt checkpoint is required after the
Core build changes. See [ViRGE status](../../docs/VIRGE.md).
