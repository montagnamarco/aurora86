# S3 ViRGE 86C325

Aurora386 implements an experimental S3 ViRGE 86C325 PCI card.
It owns the VGA register bank, 2 MB of VRAM, the display renderer, and a limited
triangle engine. Internal callers can select it with
`new Machine(videoModel: VmVideoModel.S3Virge325)`. POST enables PCI I/O and
memory decoding before initializing standard VGA. The real CPU INT 10h path
has produced and rendered text at 720x400. Windows 95 driver compatibility
remains unverified. To select the experimental card in `vm.json`, set
`"video": "S3Virge325"`. Omitting `video` selects `S3Trio64VPlus`, preserving
existing machines. Both frontends apply this setting when opening a VM folder;
a graphical GPU selector is not exposed yet. Unknown models are rejected
before the machine is built or its disks are opened.

The generated firmware supplies VGA only for ViRGE. VBE calls return
`AX=014F` before changing VGA registers, VRAM, or caller buffers. The default
Trio BIOS remains byte-identical to the previous version.

Machine snapshots include the selected video model and ViRGE drawing-engine
latches in a versioned `VID1` tail after serial mouse state. Older files without
this tail retain their Trio identity and remain without the tail when resaved.
Restore checks the model, RAM and video payload sizes, engine state, PCI
topology, interrupt sources, and agreement between BAR0 and CR59 before changing
guest state. VGA/PCI synchronization is suspended while both register banks
are restored, then the memory windows are reconciled once. Restoring engine
latches does not submit a drawing command.

An independent save/load probe resumes the CPU immediately before an
auto-execute trigger. Its 39,340-pixel frame matches uninterrupted execution
byte for byte, and the target's former MMIO address no longer decodes. A Trio
snapshot captured by the previous implementation also restores and resaves
byte-identically with the new loader.

The end-to-end fixture submits `CMD_SET` through a real CPU `MOV`, reaches the
MMIO engine, updates VRAM, and renders the result. It produced a 640x480 frame
with 39,340 coloured pixels, each packed as `FF2040E0` in the display buffer's
`0xAABBGGRR` format. The 15 engine tests cover
command submission, automatic and explicit triggers, clipping, both triangle
halves, direction, unsupported commands, CPU submission, and renderer dirty
VRAM notification.

With `CMD_SET` bit 0 clear, a dword write to `B500` submits the command;
with bit 0 set, a write to `B57C` submits it. The engine supports fixed-point edge values,
inclusive clipping, and constant-colour RGB888 output. The implementation
does not provide Gouraud gradients, textures, depth, fog, alpha, FIFO timing,
interrupts, or capture of every machine device. The engine's 19 register
latches have a separate 84-byte `VRG1` snapshot payload. Restore validates the
whole header and length before changing latches, without submitting commands,
touching VRAM, or raising command diagnostics. VRAM remains separate state.

The original engine tests attach it at the low fixture address `0x01200000`.
The new card fixture uses actual PCI configuration ports: CPU `OUT` instructions
move BAR0 to `0x74000000` and enable memory decoding, then CPU `MOV` instructions
reach the engine at `0x75000000`. An independent 640x480 run produces the same
39,340-pixel frame, byte for byte, as the isolated engine. The old MMIO address
then reads all ones. Display timing is prepared by the fixture, rather than
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
Big-endian windows, image transfer and ViRGE 2D commands are also unsupported
and diagnosed. This card has no OEM option ROM or IRQ timing.
It does not borrow the Trio accelerator to impersonate ViRGE 2D support.

The frontend consumes the common `IVideoOutput` interface. The default VM
continues to use the existing Trio implementation.

The [DOS startup probe](../tools/Virge3DProbe/README.md) now runs the original
Toolkit 2.6 cube sample on a private image and supports a hash-checked DOS
prompt checkpoint. The sample reaches DPMI mappings for `70000000h` and
`71000000h`, requests VBE mode `0110h` through INT 10h `AX=4F02`, and continues
to issue commands despite the firmware's current VBE refusal. Observed
unsupported accesses include a DWORD at new-MMIO offset `8208h` and the
triangle command `80C00006h`. The display remains 720x400 text, and Esc returns
to the DOS prompt. No period-program 3D frame has been demonstrated.

The next prerequisite is an actual ViRGE VBE mode program, including aperture
and pixel-format setup. The sample also needs capabilities beyond the current
flat RGB888 engine; enabling VBE alone will not establish compatibility.
The startup log records 84 CPU fault events as well; their presence alone does
not establish a CPU defect, and this probe makes no instruction changes.
Earlier experimental logs using physical-memory write watches are invalid for
hardware conclusions: those watches split DWORD transactions into bytes.
The retained probe uses passive CPU/card diagnostics only.

References:

- [S3 ViRGE 86C325 databook](https://vgamuseum.info/images/doc/s3/virge.pdf)
- [86Box S3 ViRGE source](https://github.com/86Box/86Box/blob/51a3f7683e9691832e423a90ad9cf8b39cd7053e/src/video/vid_s3_virge.c)
