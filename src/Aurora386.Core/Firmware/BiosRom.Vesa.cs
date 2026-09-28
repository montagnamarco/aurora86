using Aurora386.Core.Devices.Video;

namespace Aurora386.Core.Firmware;

public static partial class BiosRom
{
    /// <summary>
    /// Programs the VBE pitch through guest OUT instructions. Windows 95's
    /// VDD shadows these registers; updating only the host's mode object
    /// leaves the S3 driver reading mode 13h's 320-byte pitch at 640x480.
    /// </summary>
    private static void WriteVesaModeProgram(byte[] rom)
    {
        var code = new Asm(rom, Entry.VesaModeProgram);
        code.B(0x3D, 0x02, 0x4F);                         // CMP AX,4F02h
        code.Jcc(0x74, "mode");
        code.B(0xC3);
        code.Label("mode");
        code.B(0x50, 0x53, 0x52, 0x56, 0x1E);             // PUSH AX,BX,DX,SI,DS
        code.B(0x0E, 0x1F);                               // PUSH CS / POP DS
        // The table scan uses LODSW and LODSB. INT 10h must accept a caller
        // that has set DF; IRET restores that caller's flags afterwards.
        code.B(0xFC);                                     // CLD
        code.B(0x81, 0xE3, 0xFF, 0x3F);                   // AND BX,3FFFh
        code.B(0x81, 0xFB, 0x00, 0x01);                   // CMP BX,100h
        code.Jcc(0x73, "extended");
        code.B(0x88, 0xD8);                               // MOV AL,BL
        code.CallNear(Entry.VgaProgram);
        code.Jmp("done");
        code.Label("extended");
        code.B(0xBE, (byte)(Entry.VesaRegisterTable & 0xFF), (byte)(Entry.VesaRegisterTable >> 8));
        code.Label("search");
        code.B(0xAD, 0x3D, 0xFF, 0xFF);                   // LODSW / CMP AX,FFFFh
        code.Jcc(0x74, "done");
        code.B(0x3B, 0xC3);                               // CMP AX,BX
        code.Jcc(0x74, "found");
        code.B(0x83, 0xC6, 0x03);                         // ADD SI,3
        code.Jmp("search");
        code.Label("found");
        code.B(0xAC);                                     // LODSB: baseline VGA mode
        code.CallNear(Entry.VgaProgram);
        code.B(0xBA, 0xD4, 0x03);                         // MOV DX,3D4h
        code.B(0xB8, 0x38, 0x48, 0xEF);                   // Unlock S3 CR30-3F.
        code.B(0xB8, 0x39, 0xA0, 0xEF);                   // Unlock S3 CR40 and above.
        code.B(0xAC, 0x88, 0xC4, 0xB0, 0x13, 0xEF);       // Pitch bits 7-0 -> CR13.
        code.B(0xAC, 0x88, 0xC4, 0xB0, 0x51, 0xEF);       // Pitch bits 9-8 -> CR51 bits 5-4.
        code.B(0xB8, 0x43, 0x00, 0xEF);                   // Clear the alternative pitch extension.
        code.Label("done");
        code.B(0x1F, 0x5E, 0x5A, 0x5B, 0x58, 0xC3);
        code.Resolve();
        if (code.Position > Entry.VesaRegisterTable)
        {
            throw new InvalidOperationException("VBE mode program overlaps its register table.");
        }

        int position = Entry.VesaRegisterTable;
        foreach (VesaModes.VesaModeInfo mode in VesaModes.Available(VgaMemory.TotalSize))
        {
            int offset = mode.BytesPerRow / (mode.IsPlanar ? 2 : 8);
            rom[position++] = (byte)mode.Number;
            rom[position++] = (byte)(mode.Number >> 8);
            rom[position++] = mode.IsPlanar ? (byte)0x12 : (byte)0x13;
            rom[position++] = (byte)offset;
            rom[position++] = (byte)((offset >> 4) & 0x30);
        }
        rom[position++] = 0xFF;
        rom[position++] = 0xFF;
        if (position > 0x1F00)
        {
            throw new InvalidOperationException("VBE register table exceeds its ROM allocation.");
        }
    }
}
