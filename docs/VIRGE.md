# S3 ViRGE 86C325

Aurora86 implements an experimental S3 ViRGE 86C325 PCI card.
It owns the VGA register bank, 2 MB of VRAM, the display renderer, a limited
BitBLT engine and a triangle engine. Internal callers can select it with
`new Machine(videoModel: VmVideoModel.S3Virge325)`. POST enables PCI I/O and
memory decoding before initializing standard VGA. The real CPU INT 10h path
has produced and rendered text at 720x400. Windows 95 driver compatibility
remains unverified. To select the experimental card in `vm.json`, set
`"video": "S3Virge325"`. Omitting `video` selects `S3Trio64VPlus`, preserving
existing machines. Both frontends apply this setting when opening a VM folder;
a graphical GPU selector is not exposed yet. Unknown models are rejected
before the machine is built or its disks are opened.

The generated firmware supplies standard VGA and experimental VBE modes `0101h`
(640x480 indexed 8-bit) and `0110h` (640x480 RGB1555). Real CPU OUT instructions
set VGA timing, the pixel format, enhanced mapping and either a 64 KB banked
window or a 2 MB linear aperture. Functions `4F00/01/02/03/05` report these modes,
set/query them, return to text mode `03h`, and select/query window A.
Other requests fail with `AX=014F`
before changing video state or caller buffers. Mode bit 14 selects linear
access; bit 15 preserves VRAM. The default Trio BIOS remains byte-identical.

Mode `0101h` uses `CR13=50h`, zero CR51/CR43 pitch extensions and `CR31.3=1`:
the forced doubleword address unit is eight bytes, as in VGA mode 13h, so the
pitch is 640 bytes independently of Chain-4. `CR67=00h` selects one 8-bit
palette pixel per clock with Streams disabled. `GR05.6=1` and `CR3A.4=1` select
256-colour graphics shifts and enhanced attribute shifts. Returning to VGA
clears CR3A.4 and restores its graphics register table. Mode `0110h` uses
`CR13=A0h`, zero pitch extensions, `CR31.3=1` and `CR67=30h`, yielding a
1280-byte RGB1555 pitch. The Windows 95 driver S3V.DRV reads CR51, CR43 and
CR13 after the mode set and programs the BitBLT stride as eight times the
logical width, which is how the earlier `CR13=A0h` for mode 0101h produced a
1280-byte driver stride over a 640-byte display and a desktop drawn on every
other scanline.
Nonzero CR51 pitch extensions take precedence over CR43.2. CPU tests verify
banked and relocated linear pixel writes against the rendered scanlines,
snapshot restoration, and the return to B8000 text memory.

These settings follow the August 1996 S3 databook, sections 8-2, 16-20,
16-30, 18-3, 18-10, 18-16 and 18-25/26. The Windows 95 native ViRGE driver
requests `INT 10h AX=4F02h BX=8101h`, the standard-mode form of mode 0101h;
the firmware accepts this request and programs the adapter accordingly.
Supporting that request does not establish complete Windows 95 driver
compatibility.
[S3 ViRGE Integrated 3D Accelerator databook](https://vgamuseum.info/images/doc/s3/virge.pdf).

Machine snapshots include the selected video model and ViRGE drawing-engine
state in a versioned `VID1` tail after serial mouse state. A `VGE1` envelope
version 2 contains the triangle, BitBLT and primary Streams payloads, including
pending CPU uploads. Its 20-byte header records all three block lengths.
The 36-byte VST1 block stores seven primary presentation latches. Version 1
bundles remain readable and reset the absent Streams state to zero.
Original triangle-only `VRG1` payloads remain readable and reset the target's
blitter and Streams latches when restored. Loaded legacy files resave without rewriting their
payload format. Older files without
this tail retain their Trio identity and remain without the tail when resaved.
Restore checks the model, RAM and video payload sizes, engine state, PCI
topology, interrupt sources, and agreement between BAR0 and CR59 before changing
guest state. VGA/PCI synchronization is suspended while both register banks
are restored, then the memory windows are reconciled once. Restoring engine
latches does not submit a drawing command or replay consumed upload pixels.

An independent save/load probe resumes the CPU immediately before an
auto-execute trigger. Its 39,340-pixel frame matches uninterrupted execution
byte for byte, and the target's former MMIO address no longer decodes. A Trio
snapshot in the older, pre-ViRGE format also restores and resaves
byte-identically with the current loader.

The end-to-end fixture submits `CMD_SET` through a real CPU `MOV`, reaches the
MMIO engine, updates VRAM, and renders the result. It produced a 640x480 frame
with 39,340 coloured pixels, each packed as `FF2040E0` in the display buffer's
`0xAABBGGRR` format. The 15 engine tests cover
command submission, automatic and explicit triggers, clipping, both triangle
halves, direction, unsupported commands, CPU submission, and renderer dirty
VRAM notification.

With `CMD_SET` bit 0 clear, a dword write to `B500` submits the command;
with bit 0 set, a write to `B57C` submits it. The engine supports fixed-point edge values,
inclusive clipping, and constant-colour RGB888 output. The measured
`80C00006h` command additionally draws Gouraud RGB1555 with strict less-than
16-bit Z comparison and Z updates. Other 3D command variants, textures, fog,
alpha, FIFO timing, interrupts and capture of every machine device remain
unsupported. The engine's 24 register latches have a separate 104-byte `VRG1`
version 2 snapshot payload; version 1's 84 bytes restore with zero Z latches. Restore validates the
whole header and length before changing latches, without submitting commands,
touching VRAM, or raising command diagnostics. VRAM remains separate state.

Some engine tests attach it directly at a low fixture address, `0x01200000`;
others drive it through actual PCI configuration ports: CPU `OUT` instructions
move BAR0 to `0x74000000` and enable memory decoding, then CPU `MOV` instructions
reach the engine at `0x75000000`. Both paths produce the same 39,340-pixel
frame, byte for byte. After relocation, the address BAR0 held before reads
all ones. Display timing is prepared by the fixture, rather than
by a guest display driver.

BAR0 reserves 64 MB, resets to `0x70000000`, and reports `0xFC000000` during
sizing. PCI and CRTC writes share its upper six address bits. CR53 selects new
MMIO, CR58 controls the linear window, and enhanced mapping requires CR31 and
CR66. The new MMIO window also exposes PCI configuration and VGA register
aliases. PCI I/O enable and SR9 govern programmed I/O separately from MMIO.

The physical bus keeps installed RAM separate from its 4 GB address table.
Relocatable windows retain the mapping beneath them, including ROM protection
and RAM read watches. Disabling PCI memory decoding reveals that current
mapping. Overlapping windows use a documented, stable creation order; this
is an emulator mapping policy, not a claim about electrical bus arbitration.

The standard VGA legacy memory window follows GDC6: 128 KB at `A0000`,
64 KB at `A0000`, 32 KB at `B0000`, or 32 KB at `B8000`. PCI memory enable
controls decoding. Guest PIO and MMIO register writes update the same window;
host register edits can be completed with `SynchronizeMappings()`. The BAR
retains priority if relocated over legacy memory. A CPU word write at `B8000`
has been checked against the character and attribute planes and the 720x400
text display produced by mode 03h's nine-dot cells.

Enhanced banked access now uses a fixed 64 KB window at `A0000h` when
`CR31.3` and `CR66.0` are set. This overrides GDC6 independently of VBE mode
metadata or Chain-4. `CR31.0` enables 64 KB pages: a nonzero `CR6A[5:0]`
overrides `CR35[3:0]` and `CR51[3:2]`; disabling the CPU base selects page zero.
`CR58.4` disables this enhanced banked window when linear addressing is enabled.
The old-linear exception for a 64 KB aperture based at A0000 remains unsupported.
Old MMIO and an enhanced request without `CR66.0` still disable the legacy
window and emit diagnostics. These limitations are not silicon behaviour.
Big-endian windows remain unsupported and diagnosed.
This card has no OEM option ROM or IRQ timing.
It does not borrow the Trio accelerator to impersonate ViRGE 2D support.

The ViRGE BitBLT subset provides 16-bit PATCOPY rectangle fills, 8/16-bit
SRCCOPY screen copies and 16-bit SRCCOPY CPU image uploads. Aligned DWORD
writes program the native A4D4h–A50Ch registers; image data arrives through
the lower half of the new-MMIO window. Uploads consume clipped pixels and
discard DWORD row padding. The shared status register reports busy while an
upload awaits data. Other operations and formats remain explicitly unsupported,
including 2D autoexecute. Without a modeled FIFO, a new command cancels a
pending upload; this is a documented emulator limitation.

The primary Streams subset uses `CR67[3:2]=11b` to select presentation from
its own MMIO latches. `8180=03000000h` selects unscaled KRGB1555; `81CC.0`
selects the base in `81C0` or `81C4`; `81C8` supplies the byte stride, independently
of VBE metadata. `81F0` positions the primary window with one-based screen
coordinates, and `81F4` stores width minus one and the actual height. The
window is clipped to the current screen. Page writes and CR67 transitions
redraw even if VRAM is unchanged. Active Streams redraws all rows when VRAM
changes because the existing dirty-row mapping describes the VGA surface.

The databook does not specify VSYNC-delayed latches for these seven primary
registers. They are stored immediately and sampled when the host renders;
this does not model beam-accurate tearing or FIFO timing. Zero stride repeats
a source row. Undefined power-on latches start at zero as an emulator policy.
Primary registers accept aligned DWORD transactions. Secondary buffer fields
in `81CC`, composition, scaling and other formats remain unsupported. Full
Streams with an unsupported format/filter produces black, without falling
back to an unrelated VGA page. `CR67[3:2]=00b` retains the VGA/VBE path; `01b`
still displays its VGA background without a secondary overlay. The Trio
renderer receives no Streams state and retains its existing behavior.

The frontend consumes the common `IVideoOutput` interface. The default VM
continues to use the existing Trio implementation.

The [Trio guest regression matrix](TRIO-REGRESSION.md) verifies DOS 6.22,
Windows 3.11 with VGA and native S3 drivers, and Windows 95 desktop interaction
after primary Streams integration. It also records the unresolved Windows 95
startup glyph symptom and the precise scope of these smoke checks.

The [DOS startup probe](../tools/Virge3DProbe/README.md) runs the original
Toolkit 2.6 cube sample on your own DOS image and supports a hash-checked DOS
prompt checkpoint. The sample reaches DPMI mappings for `70000000h` and
`71000000h` and enters VBE mode `0110h` through INT 10h `AX=4F02`. Observed
unsupported accesses remain in secondary-stream, composition and FIFO controls.
The triangle engine accepts command `80C00006h`, and the primary Streams
renderer now presents the page selected by the guest. The 640x480 display
matches raw page `096000h` byte for byte, showing the cube's front face with
green, magenta, blue and white vertex colours over the tiled background.
Esc returns to the DOS prompt and clears the Streams enable bits in CR67.
This establishes the sample's initial Gouraud/Z frame; texture modes, secondary
composition and general period-driver compatibility remain unverified.

The [drawing capture](VIRGE-CUBE.md) records the command setup, implementation
limits and observed output. The first triangle updates 4,880 depth pixels
before the next guest command. At the final sample boundary, the guest has
already cleared Z for another frame; this is why the probe also captures
immediately after the first triangle. Primary page selection now follows
`81C0/81C4/81CC` independently of VGA/VBE display-start metadata.
The probe records CPU fault events for reference; their presence alone does
not establish a CPU defect, and the probe makes no instruction changes.
A write watch on physical memory is not valid for hardware conclusions here:
it splits DWORD transactions into bytes. The probe uses passive CPU/card
diagnostics only.

References:

- [S3 ViRGE 86C325 databook](https://vgamuseum.info/images/doc/s3/virge.pdf)
- [86Box S3 ViRGE source](https://github.com/86Box/86Box/blob/51a3f7683e9691832e423a90ad9cf8b39cd7053e/src/video/vid_s3_virge.c)
