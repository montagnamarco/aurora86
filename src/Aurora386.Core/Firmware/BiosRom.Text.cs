namespace Aurora386.Core.Firmware;

public static partial class BiosRom
{
    /// <summary>
    /// Completes the colour BIOS text modes with guest instructions after the
    /// standard register program.  Windows virtualises these stores and OUT
    /// instructions for a DOS VM, so doing them in the native INT 10h hook
    /// changes the Windows desktop instead of the guest console.
    /// </summary>
    private static void WriteTextModeCompletion(byte[] rom)
    {
        var code = new Asm(rom, Entry.TextModeCompletion);

        code.B(0x50, 0x24, 0x7F);                         // PUSH AX / AND AL,7Fh
        code.B(0x3C, 0x00);
        code.Jcc(0x74, "text");
        code.B(0x3C, 0x01);
        code.Jcc(0x74, "text");
        code.B(0x3C, 0x02);
        code.Jcc(0x74, "text");
        code.B(0x3C, 0x03);
        code.Jcc(0x74, "text");
        code.B(0x58, 0xC3);                               // POP AX / RET

        code.Label("text");
        code.B(0x58);                                     // POP AX
        code.B(0x9C, 0x50, 0x53, 0x51, 0x52, 0x56, 0x57, 0x55, 0x1E, 0x06);
        code.B(0x0E, 0x1F, 0xFC);                         // PUSH CS / POP DS / CLD
        code.B(0x89, 0xC5, 0x24, 0x7F, 0x88, 0xC3);       // MOV BP,AX / AND AL,7Fh / MOV BL,AL
        code.B(0xB9, 0x50, 0x00, 0x3C, 0x02);             // MOV CX,80 / CMP AL,2
        code.Jcc(0x73, "columns");
        code.B(0xB9, 0x28, 0x00);                         // MOV CX,40

        code.Label("columns");
        code.B(0xB8, 0x40, 0x00, 0x8E, 0xC0);             // MOV AX,40h / MOV ES,AX
        code.B(0x26, 0x88, 0x1E, 0x49, 0x00);             // MOV ES:[49h],BL
        code.B(0x26, 0x89, 0x0E, 0x4A, 0x00);             // MOV ES:[4Ah],CX
        code.B(0x89, 0xC8, 0xB2, 0x32, 0xF6, 0xE2);       // MOV AX,CX / MOV DL,50 / MUL DL
        code.B(0x26, 0xA3, 0x4C, 0x00);                   // MOV ES:[4Ch],AX
        code.B(0x26, 0xC6, 0x06, 0x84, 0x00, 0x18);       // MOV BYTE ES:[84h],24
        code.B(0xF7, 0xC5, 0x80, 0x00);                   // TEST BP,80h
        code.Jcc(0x75, "cursor");
        code.B(0x26, 0xC7, 0x06, 0x50, 0x00, 0x00, 0x00); // MOV WORD ES:[50h],0

        code.Label("cursor");
        code.B(0x26, 0xA1, 0x50, 0x00, 0x89, 0xC6);       // MOV AX,ES:[50h] / MOV SI,AX
        code.B(0x89, 0xF0, 0x30, 0xE4, 0x50);             // MOV AX,SI / XOR AH,AH / PUSH AX
        code.B(0x89, 0xF0, 0x88, 0xE0, 0x30, 0xE4);       // MOV AX,SI / MOV AL,AH / XOR AH,AH
        code.B(0xF7, 0xE1, 0x5E, 0x01, 0xF0, 0x50);       // MUL CX / POP SI / ADD AX,SI / PUSH AX
        code.B(0xF7, 0xC5, 0x80, 0x00);                   // TEST BP,80h
        code.Jcc(0x75, "palette");
        code.B(0xB8, 0x00, 0xB8);                         // MOV AX,B800h
        code.B(0x8E, 0xC0, 0x31, 0xFF, 0xB8, 0x20, 0x07); // MOV ES,AX / XOR DI,DI / MOV AX,0720h
        code.B(0xB9, 0xD0, 0x07, 0x80, 0xFB, 0x02);       // MOV CX,2000 / CMP BL,2
        code.Jcc(0x73, "clear");
        code.B(0xB9, 0xE8, 0x03);                         // MOV CX,1000
        code.Label("clear");
        code.B(0xF3, 0xAB);                               // REP STOSW

        code.Label("palette");
        code.B(0xBA, 0xC8, 0x03, 0x30, 0xC0, 0xEE, 0x42); // MOV DX,3C8h / XOR AL,AL / OUT / INC DX
        code.B(0xBE, unchecked((byte)Entry.TextModePalette), (byte)(Entry.TextModePalette >> 8));
        code.B(0xB9, 0x00, 0x03, 0xF3, 0x6E);             // MOV CX,768 / REP OUTSB
        code.B(0x5B, 0xBA, 0xCC, 0x03, 0xEC);             // POP BX / MOV DX,3CCh / IN AL,DX
        code.B(0xBA, 0xD4, 0x03, 0xA8, 0x01);             // MOV DX,3D4h / TEST AL,1
        code.Jcc(0x75, "crtc");
        code.B(0xB2, 0xB4);                               // MOV DL,B4h
        code.Label("crtc");
        code.B(0xB0, 0x0E, 0xEE, 0x42, 0x88, 0xF8, 0xEE); // CRTC cursor high
        code.B(0x4A, 0xB0, 0x0F, 0xEE, 0x42, 0x88, 0xD8, 0xEE); // CRTC cursor low
        code.B(0x07, 0x1F, 0x5D, 0x5F, 0x5E, 0x5A, 0x59, 0x5B, 0x58, 0x9D);

        code.B(0xC3);
        code.Resolve();
        if (code.Position > Entry.PnpHeader)
        {
            throw new InvalidOperationException("Text-mode ROM routine overlaps the Plug and Play header.");
        }

        int position = Entry.TextModePalette;
        for (int index = 0; index < 64; index++)
        {
            rom[position++] = (byte)(((index >> 2) & 1) * 0x2A + ((index >> 5) & 1) * 0x15);
            rom[position++] = (byte)(((index >> 1) & 1) * 0x2A + ((index >> 4) & 1) * 0x15);
            rom[position++] = (byte)((index & 1) * 0x2A + ((index >> 3) & 1) * 0x15);
        }

        Array.Clear(rom, position, 192 * 3);
        position += 192 * 3;
        if (position > 0x3300)
        {
            throw new InvalidOperationException("Text-mode DAC table exceeds its ROM allocation.");
        }
    }
}
