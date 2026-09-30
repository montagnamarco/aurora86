# VBE modes and pixel formats

Aurora386 reports VBE 2.0 through INT 10h AX=4F00h. Applications should walk
the returned mode list and query each entry with AX=4F01h. Modes that exceed
the adapter's 2 MB of video memory are omitted.

The 32-bit modes use OEM numbers:

| Mode | Resolution | Bytes per scan line | Pixel format |
|---|---|---|---|
| 140h | 640×480 | 2560 | XRGB8888 |
| 141h | 800×600 | 3200 | XRGB8888 |

Each pixel occupies four bytes in memory: blue, green, red, unused. The
unused byte does not affect the displayed colour. Both the 64 KB banked
window at A0000h and the linear framebuffer support this format. A20 must
be enabled when accessing the entire linear framebuffer through CPU
physical addresses. Functions 4F06h and 4F07h support a wider virtual scan
line and a changed display origin in these modes.

The mode information follows the [VBE 2.0 specification, function 4F01h](https://www.phatcode.net/res/221/files/vbe20.pdf):
window A attributes occupy byte 2, the additional-image count occupies
byte 29, and the RGB/reserved masks occupy bytes 31–38. Bytes 50–255 remain
reserved; VBE 2.0 uses the same pixel masks for banked and linear access.

`VesaTests` exercises enumeration, mode information, bank boundaries,
linear writes, virtual stride, display origin, ignored high bytes and
agreement between incremental and full rendering. Run it with:

```sh
dotnet test tests/Aurora386.Tests --filter 'FullyQualifiedName~Vesa'
```

This covers the VBE framebuffer interface. Validation with DOS VESA
applications and Windows SVGA256.DRV is not yet established.
