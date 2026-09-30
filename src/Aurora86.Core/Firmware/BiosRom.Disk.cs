namespace Aurora86.Core.Firmware;

public static partial class BiosRom
{
    /// <summary>
    /// Reads IDE sectors with real port instructions so a virtual-machine
    /// monitor can intercept the BIOS's disk access. Validation and address
    /// translation stay in DiskServices; this routine owns the transfer.
    /// </summary>
    private static void WriteDiskPioRead(byte[] rom)
    {
        var code = new Asm(rom, Entry.DiskPioRead);
        code.B(0x66, 0x60);                         // PUSHAD
        code.B(HookByte1, HookByte2, (byte)Handler.DiskReadPrepare);
        code.B(0x66, 0x89, 0xC6);                   // MOV ESI,EAX: LBA
        code.B(0x66, 0x89, 0xCD);                   // MOV EBP,ECX: sector count

        // Poll without generating IRQs. EBX holds the previous device
        // control in its high word and the command base in its low word.
        code.B(0x89, 0xDA, 0x81, 0xC2, 0x06, 0x02); // MOV DX,BX / ADD DX,206h
        code.B(0x66, 0x89, 0xD8, 0x66, 0xC1, 0xE8, 0x10); // MOV EAX,EBX / SHR EAX,16
        code.B(0x0C, 0x02, 0xEE);                   // OR AL,2 / OUT DX,AL
        code.B(0x89, 0xDA, 0x83, 0xC2, 0x06);       // MOV DX,BX / ADD DX,6
        code.B(0x66, 0x89, 0xF0, 0x66, 0xC1, 0xE8, 0x18); // MOV EAX,ESI / SHR EAX,24
        code.B(0x24, 0x0F, 0x0C, 0xE0, 0xEE);       // AND AL,0Fh / OR AL,E0h / OUT
        code.B(0x89, 0xDA, 0x83, 0xC2, 0x02);       // MOV DX,BX / ADD DX,2
        code.B(0x66, 0x89, 0xE8, 0xEE);             // MOV EAX,EBP / OUT: count
        code.B(0x42, 0x66, 0x89, 0xF0, 0xEE);       // INC DX / MOV EAX,ESI / OUT: LBA low
        code.B(0x42, 0x66, 0xC1, 0xE8, 0x08, 0xEE); // INC DX / SHR EAX,8 / OUT: middle
        code.B(0x42, 0x66, 0xC1, 0xE8, 0x08, 0xEE); // INC DX / SHR EAX,8 / OUT: high
        code.B(0x83, 0xC2, 0x02, 0xB0, 0x20, 0xEE); // ADD DX,2 / MOV AL,20h / OUT: read

        code.Label("sector");
        code.B(0x89, 0xDA, 0x83, 0xC2, 0x07);       // MOV DX,BX / ADD DX,7
        code.B(0x66, 0xB9, 0xFF, 0xFF, 0, 0);       // MOV ECX,FFFFh: bounded polling
        code.Label("poll");
        code.B(0xEC, 0xA8, 0x80);                   // IN AL,DX / TEST AL,BSY
        code.Jcc(0x75, "wait");
        code.B(0xA8, 0x21);                         // TEST AL,DF|ERR
        code.Jcc(0x75, "error");
        code.B(0xA8, 0x08);                         // TEST AL,DRQ
        code.Jcc(0x75, "data");
        code.Label("wait");
        code.Jcc(0xE2, "poll");                     // LOOP
        code.Label("error");
        code.B(0xB0, 0x20);                         // BIOS controller failure
        code.Jmp("finish");
        code.Label("data");
        code.B(0x89, 0xDA, 0xB9, 0x00, 0x01, 0xFC); // MOV DX,BX / MOV CX,256 / CLD
        code.B(0x67, 0xF3, 0x6D);                   // REP INSW to ES:EDI
        code.B(0x66, 0x4D);                         // DEC EBP
        code.Jcc(0x75, "sector");
        code.B(0x30, 0xC0);                         // XOR AL,AL: success
        code.Label("finish");
        code.B(0x50);                               // Preserve completion status
        code.B(0x89, 0xDA, 0x83, 0xC2, 0x07, 0xEC); // Acknowledge pending IDE status
        code.B(0x89, 0xDA, 0x81, 0xC2, 0x06, 0x02); // Restore device control
        code.B(0x66, 0x89, 0xD8, 0x66, 0xC1, 0xE8, 0x10, 0xEE);
        code.B(0x58);
        code.B(HookByte1, HookByte2, (byte)Handler.DiskReadComplete);
        code.B(0x66, 0x61, 0xCF);                   // POPAD / IRET
        code.Resolve();
        if (code.Position > 0x1D00)
        {
            throw new InvalidOperationException("IDE BIOS routine exceeds its ROM allocation.");
        }
    }
}
