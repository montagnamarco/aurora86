# S3 ViRGE 86C325

Aurora386 implements a limited S3 ViRGE 86C325 triangle engine.
The implementation covers flat RGB888 triangles through the engine's
64 KB MMIO register window and writes the same VRAM consumed by the display
renderer.

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
interrupts, or complete snapshot support.

The existing Trio machine is unchanged. Tests attach the engine explicitly
at the low fixture address `0x01200000`; this is a test fixture and does not
represent the real ViRGE PCI aperture. The hardware profile uses a 64 MB BAR
and places the new MMIO window at BAR + `0x01000000`; PCI integration and guest
driver compatibility remain future work.

The frontend consumes the common `IVideoOutput` interface. No ViRGE model is
selectable from VM configuration or GUI.

References:

- [S3 ViRGE 86C325 databook](https://vgamuseum.info/images/doc/s3/virge.pdf)
- [86Box S3 ViRGE source](https://github.com/86Box/86Box/blob/51a3f7683e9691832e423a90ad9cf8b39cd7053e/src/video/vid_s3_virge.c)
