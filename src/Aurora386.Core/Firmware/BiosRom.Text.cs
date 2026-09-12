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

    /// <summary>
    /// Executes guest text services through the DOS VM's address translation.
    /// The saved register frame lets each service change only its documented outputs.
    /// </summary>
    private static void WriteTextServices(byte[] rom)
    {
        var code = new Asm(rom, Entry.TextServices);

        // BP names ES, DS, BP, DI, SI, DX, CX, BX, AX, FLAGS in that order.
        code.B(0x9C, 0x50, 0x53, 0x51, 0x52, 0x56, 0x57, 0x55, 0x1E, 0x06);
        code.B(0x89, 0xE5);                               // MOV BP,SP

        // Other BIOS functions must not acquire a new BDA selector access.
        // Check the service first, then inspect the selected VM's mode.
        foreach (byte function in new byte[] { 0x02, 0x03, 0x08, 0x0E, 0x0F })
        {
            code.B(0x80, 0xFC, function);
            code.Jcc(0x74, "candidate");
        }

        code.JmpNear("fallback");
        code.Label("candidate");
        code.B(0xB8, 0x40, 0x00, 0x8E, 0xD8);
        code.B(0x80, 0x3E, 0x49, 0x00, 0x03);             // CMP BYTE [49h],3
        code.Jcc(0x76, "text");
        code.JmpNear("fallback");
        code.Label("text");
        code.B(0x8B, 0x46, 0x10);                         // MOV AX,SS:[BP+16]
        code.B(0x80, 0xFC, 0x02);
        code.Jcc(0x75, "not_set_cursor");
        code.JmpNear("set_cursor");
        code.Label("not_set_cursor");
        code.B(0x80, 0xFC, 0x03);
        code.Jcc(0x75, "not_get_cursor");
        code.JmpNear("get_cursor");
        code.Label("not_get_cursor");
        code.B(0x80, 0xFC, 0x08);
        code.Jcc(0x75, "not_read");
        code.JmpNear("read");
        code.Label("not_read");
        code.B(0x80, 0xFC, 0x0E);
        code.Jcc(0x75, "not_teletype");
        code.JmpNear("teletype");
        code.Label("not_teletype");
        code.B(0x80, 0xFC, 0x0F);
        code.Jcc(0x74, "mode");
        code.JmpNear("fallback");

        code.Label("mode");
        code.B(0xA0, 0x49, 0x00, 0x8A, 0x26, 0x4A, 0x00);
        code.B(0x89, 0x46, 0x10);                         // Saved AX = mode/columns
        code.B(0xA0, 0x62, 0x00, 0x88, 0x46, 0x0F);       // Saved BH = active page
        code.JmpNear("done");

        code.Label("get_cursor");
        code.B(0xA1, 0x60, 0x00, 0x89, 0x46, 0x0C);       // Cursor shape -> saved CX
        code.B(0xA1, 0x50, 0x00, 0x89, 0x46, 0x0A);       // Position -> saved DX
        code.JmpNear("done");

        code.Label("set_cursor");
        code.B(0x8B, 0x76, 0x0A);                         // SI = caller DX
        code.CallNear("cursor");
        code.JmpNear("done");

        code.Label("read");
        code.B(0x8B, 0x16, 0x50, 0x00);
        code.CallNear("address");
        code.B(0xB8, 0x00, 0xB8, 0x8E, 0xC0);
        code.B(0x26, 0x8B, 0x05, 0x89, 0x46, 0x10);       // Guest cell -> saved AX
        code.JmpNear("done");

        // A full final column wraps only on the next printable character.
        // CR/LF, backspace and bell keep the native BIOS convention.
        code.Label("teletype");
        code.B(0xFC, 0x8B, 0x16, 0x50, 0x00);             // CLD / DX = guest cursor
        code.B(0x3C, 0x0D);
        code.Jcc(0x74, "carriage_return");
        code.B(0x3C, 0x0A);
        code.Jcc(0x74, "line_feed");
        code.B(0x3C, 0x08);
        code.Jcc(0x74, "backspace");
        code.B(0x3C, 0x07);
        code.Jcc(0x74, "advance");
        code.B(0x3A, 0x16, 0x4A, 0x00);                   // CMP DL,[columns]
        code.Jcc(0x72, "character");
        code.B(0x30, 0xD2, 0xFE, 0xC6);                   // Column zero / next row
        code.Label("character");
        code.B(0x89, 0xD6);                               // SI keeps uncommitted cursor
        code.CallNear("address");
        code.B(0xB8, 0x00, 0xB8, 0x8E, 0xC0);
        code.B(0x8A, 0x46, 0x10, 0x26, 0x88, 0x05);       // MOV ES:[DI],AL; #PF retries here
        code.B(0x89, 0xF2, 0xFE, 0xC2);                   // Restore cursor / increment column
        code.Jmp("advance");
        code.Label("carriage_return");
        code.B(0x30, 0xD2);
        code.Jmp("advance");
        code.Label("line_feed");
        code.B(0xFE, 0xC6);
        code.Jmp("advance");
        code.Label("backspace");
        code.B(0x84, 0xD2);
        code.Jcc(0x74, "advance");
        code.B(0xFE, 0xCA);

        code.Label("advance");
        code.B(0x89, 0xD6, 0x3A, 0x36, 0x84, 0x00);       // SI = cursor / CMP DH,[last row]
        code.Jcc(0x76, "finish_cursor");

        // Copy whole character/attribute pairs forward. SI/DI/CX remain in
        // the real fault frame when a monitor supplies a missing text page.
        code.B(0x56, 0x1E, 0x31, 0xD2);                   // Save cursor/DS, address row zero
        code.CallNear("address");
        code.B(0x8B, 0x0E, 0x4A, 0x00, 0x51);             // Columns, saved for final clear
        code.B(0xA0, 0x84, 0x00, 0x30, 0xE4, 0xF7, 0xE1);
        code.B(0x89, 0xC1);                               // CX = (rows-1)*columns
        code.B(0xA1, 0x4A, 0x00, 0xD1, 0xE0);
        code.B(0x89, 0xFE, 0x01, 0xC6);                   // SI = start + one row
        code.B(0xB8, 0x00, 0xB8, 0x8E, 0xC0, 0x8E, 0xD8);
        code.B(0xF3, 0xA5);                               // REP MOVSW
        code.B(0x59, 0xB8, 0x20, 0x07, 0xF3, 0xAB);       // Clear last row with 0720h
        code.B(0x1F, 0x5E, 0x89, 0xF2);
        code.B(0x8A, 0x36, 0x84, 0x00, 0x89, 0xD6);       // Clamp completed cursor to last row
        code.Label("finish_cursor");
        code.CallNear("cursor");
        code.JmpNear("done");

        code.Label("fallback");
        WriteTextRegisterRestore(code);
        code.B(0x80, 0xFC, 0x10);
        code.Jcc(0x75, "not_palette");
        code.CallNear(Entry.VgaPalette);
        code.Jmp("hook");
        code.Label("not_palette");
        code.CallNear(Entry.VesaProgram);
        code.Label("hook");
        code.B(HookByte1, HookByte2, (byte)Handler.Video, 0xCF);

        code.Label("done");
        WriteTextRegisterRestore(code);
        code.B(0xCF);
        // DX is the BDA row/column pair. The monitor's columns determine
        // the linear cell; no physical renderer geometry participates.
        code.Label("cell");
        code.B(0x89, 0xD3, 0x81, 0xE3, 0xFF, 0x00);
        code.B(0x88, 0xF0, 0x30, 0xE4);                   // MOV AL,DH / XOR AH,AH
        code.B(0xF7, 0x26, 0x4A, 0x00, 0x01, 0xD8, 0xC3);

        // Read the virtual CRTC display start, which DOS console drivers
        // can advance independently of the BIOS cursor position.
        code.Label("address");
        code.CallNear("cell");
        code.B(0x89, 0xC3, 0xBA, 0xD4, 0x03);
        code.B(0xB0, 0x0C, 0xEE, 0x42, 0xEC, 0x88, 0xC5); // CH = start high
        code.B(0x4A, 0xB0, 0x0D, 0xEE, 0x42, 0xEC, 0x88, 0xC1);
        code.B(0x01, 0xCB, 0xD1, 0xE3, 0x89, 0xDF, 0xC3); // DI = (cell + start)*2

        // SI keeps the completed cursor until all faultable text stores
        // have finished. Cursor ports intentionally retain the old BIOS
        // row*columns+column convention, without the display start.
        code.Label("cursor");
        code.B(0x89, 0x36, 0x50, 0x00, 0x89, 0xF2);
        code.CallNear("cell");
        code.B(0x89, 0xC3, 0xBA, 0xD4, 0x03);
        code.B(0xB0, 0x0E, 0xEE, 0x42, 0x88, 0xF8, 0xEE);
        code.B(0x4A, 0xB0, 0x0F, 0xEE, 0x42, 0x88, 0xD8, 0xEE, 0xC3);

        code.Resolve();
        if (code.Position > Entry.TextServicesEnd)
        {
            throw new InvalidOperationException("Guest text services exceed their ROM allocation.");
        }
    }

    /// <summary>Restores the interrupt caller, including its direction flag.</summary>
    private static void WriteTextRegisterRestore(Asm code)
    {
        code.B(0x07, 0x1F, 0x5D, 0x5F, 0x5E, 0x5A, 0x59, 0x5B, 0x58, 0x9D);
    }
}
