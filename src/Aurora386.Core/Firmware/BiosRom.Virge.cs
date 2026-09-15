namespace Aurora386.Core.Firmware;

public static partial class BiosRom
{
    /// <summary>
    /// Programs the original 1996 ViRGE 86C325 for VBE modes 101h and 110h using x86
    /// OUT instructions. The mode uses the VGA 640x480 timing and 25.175 MHz
    /// clock, then enables the documented indexed 8-bit or RGB1555 format and
    /// either its 64 KB banked window or the two-megabyte linear aperture.
    /// Unsupported requests return without touching an adapter port.
    /// </summary>
    private static void WriteVirgeVesaModeProgram(byte[] rom)
    {
        var code = new Asm(rom, Entry.VesaModeProgram);

        code.B(0x3D, 0x02, 0x4F);                         // CMP AX,4F02h
        code.Jcc(0x74, "mode_request");
        code.JmpNear("not_mode");
        code.Label("mode_request");
        code.B(0x50, 0x89, 0xD8, 0x25, 0xFF, 0x3F);       // PUSH AX / MOV AX,BX / AND AX,3FFFh
        code.B(0x3D, 0x01, 0x01);                         // CMP AX,0101h
        code.Jcc(0x74, "mode_extended");
        code.B(0x3D, 0x10, 0x01);                         // CMP AX,0110h
        code.Jcc(0x74, "mode_extended");
        code.B(0x3D, 0x03, 0x00);                         // CMP AX,0003h
        code.Jcc(0x74, "mode_03");
        code.B(0x58, 0xC3);                               // POP AX / RET

        // VBE 4F02h can return to text mode. Run the same visible completion
        // as INT 10h AH=00, translating VBE's preserve bit 15 to VGA bit 7,
        // then restore the VBE call registers for its native completion.
        code.Label("mode_03");
        code.B(0x58, 0x50, 0x53);                         // POP AX / PUSH AX,BX
        code.B(0xB8, 0x03, 0x00);                         // MOV AX,0003h
        code.B(0xF6, 0xC7, 0x80);                         // TEST BH,80h
        code.Jcc(0x74, "program_text");
        code.B(0x0C, 0x80);                               // OR AL,80h
        code.Label("program_text");
        code.CallNear(Entry.VgaProgram);
        code.B(HookByte1, HookByte2, (byte)Handler.VideoModeCompletion);
        code.CallNear(Entry.TextModeCompletion);
        code.B(0x5B, 0x58, 0xC3);                         // POP BX,AX / RET

        code.Label("mode_extended");
        code.B(0x58, 0x50, 0x53, 0x52);                   // POP AX / PUSH AX,BX,DX
        code.B(0xB0, 0x12);                               // VGA 640x480 timing, 25.175 MHz DCLK.
        code.CallNear(Entry.VgaProgram);
        code.B(0xBA, 0xC4, 0x03);                         // MOV DX,3C4h
        code.B(0xB8, 0x08, 0x06, 0xEE, 0x42, 0x88, 0xE0, 0xEE); // SR08=06h: unlock extended sequencer.
        code.B(0xBA, 0xD4, 0x03);                         // MOV DX,3D4h
        WriteCrtc(0x38, 0x48);                            // Unlock CR30-3F.
        WriteCrtc(0x39, 0xA0);                            // Unlock CR40 and above.

        // Databook sections 16-20 and 18-2: CR31.3 forces doubleword
        // addressing, whose offset unit is eight bytes. Indexed 8-bit uses
        // 640 / 8 = 50h and RGB1555 640 * 2 / 8 = A0h, so neither pitch
        // needs the CR43.2 or CR51[5:4] extension bits. The Windows 95 ViRGE
        // driver reads these three registers back after the mode set and
        // derives its 2D engine stride from them. Clear both old pitch and
        // bank extensions.
        code.B(0x89, 0xD8, 0x25, 0xFF, 0x3F);             // MOV AX,BX / AND AX,3FFFh
        code.B(0x3D, 0x01, 0x01);                         // CMP AX,0101h
        code.Jcc(0x75, "rgb1555_pitch");
        WriteCrtc(0x13, 0x50);
        code.JmpNear("pitch_extensions");
        code.Label("rgb1555_pitch");
        WriteCrtc(0x13, 0xA0);
        code.Label("pitch_extensions");
        WriteCrtc(0x35, 0x00);                            // Clear the old bank selector.
        WriteCrtc(0x43, 0x00);                            // CR43.2: old pitch extension disabled.
        WriteCrtc(0x51, 0x00);                            // CR51[5:4]=00b: no pitch bits 9-8.
        WriteCrtc(0x5D, 0x00);                            // No horizontal timing extensions.
        WriteCrtc(0x5E, 0x00);                            // No vertical timing extensions.
        WriteCrtc(0x69, 0x00);                            // Display start address extension zero.

        // Databook sections 15-2/3 and 18-2/20/24/25/28. Enhanced mapping
        // gives A0000h a 64 KB bank selected by CR6A. New MMIO remains
        // selected at the PCI BAR; CR58 chooses banked or linear access.
        WriteCrtc(0x53, 0x08);                            // New MMIO.
        WriteCrtc(0x66, 0x01);                            // Enhanced/non-planar functions.
        WriteCrtc(0x31, 0x09);                            // Enhanced map and 64 KB bank.
        WriteCrtc(0x6A, 0x00);                            // Start on bank zero.
        code.B(0x89, 0xD8, 0x25, 0xFF, 0x3F);             // MOV AX,BX / AND AX,3FFFh
        code.B(0x3D, 0x01, 0x01);                         // CMP AX,0101h
        code.Jcc(0x75, "rgb1555_format");

        // Databook sections 16-30, 18-10 and 8-2: GR05.6 selects 256-colour
        // shifts, CR3A.4 enables enhanced attribute shifts, and CR67=00h
        // selects one indexed pixel per VCLK with the Streams Processor off.
        WriteCrtc(0x3A, 0x10);
        WriteCrtc(0x67, 0x00);
        code.B(0xBA, 0xCE, 0x03);                         // MOV DX,3CEh
        code.B(0xB8, 0x05, 0x40, 0xEE, 0x42, 0x88, 0xE0, 0xEE); // GR05=40h, GR06 remains VGA graphics A0000h.
        code.B(0xBA, 0xD4, 0x03);                         // MOV DX,3D4h
        code.JmpNear("format_complete");
        code.Label("rgb1555_format");
        WriteCrtc(0x67, 0x30);                            // RGB555, Streams disabled.
        code.Label("format_complete");
        code.B(0xB8, 0x58, 0x00);                         // CR58=00h: banked window.
        code.B(0xF6, 0xC7, 0x40);                         // TEST BH,40h: linear-frame-buffer flag.
        code.Jcc(0x74, "write_aperture");
        code.B(0xB4, 0x12);                               // CR58=12h: enabled, two-megabyte aperture.
        code.Label("write_aperture");
        code.CallNear(Entry.VirgeCrtcWrite);
        code.B(0x5A, 0x5B, 0x58, 0xC3);                   // POP DX,BX,AX / RET

        code.Label("not_mode");
        code.B(0x3D, 0x05, 0x4F);                         // CMP AX,4F05h
        code.Jcc(0x75, "done");
        code.B(0x81, 0xFB, 0x00, 0x00);                   // CMP BX,0000h: set window A only.
        code.Jcc(0x75, "done");
        code.B(0x83, 0xFA, 0x1F);                         // CMP DX,31
        code.Jcc(0x77, "done");                          // JA: outside two-megabyte VRAM.

        code.B(0x50, 0x53, 0x52);                         // PUSH AX,BX,DX
        code.B(0x89, 0xD3, 0x88, 0xDF, 0xB3, 0x6A);       // BX=(bank << 8)|CR6A index.
        code.B(0xBA, 0xD4, 0x03);                         // MOV DX,3D4h
        WriteCrtc(0x38, 0x48);                            // Keep old and new bank fields accessible.
        WriteCrtc(0x39, 0xA0);
        code.B(0xB0, 0x35, 0xEE, 0x42, 0xEC, 0x24, 0xF0, 0xEE, 0x4A); // CR35[3:0]=0.
        code.B(0xB0, 0x51, 0xEE, 0x42, 0xEC, 0x24, 0xF3, 0xEE, 0x4A); // CR51[3:2]=0.
        code.B(0x89, 0xD8);                               // MOV AX,BX
        code.CallNear(Entry.VirgeCrtcWrite);              // CR6A=DL bank.
        code.B(0x5A, 0x5B, 0x58);                         // POP DX,BX,AX

        code.Label("done");
        code.B(0xC3);
        code.Resolve();
        if (code.Position > Entry.VirgeCrtcWrite)
        {
            throw new InvalidOperationException("ViRGE VBE mode program exceeds its ROM allocation.");
        }

        // AX carries index in AL and data in AH. Byte OUT instructions make
        // both physical ports visible to VDD-style monitors and diagnostics.
        var writeCrtc = new Asm(rom, Entry.VirgeCrtcWrite);
        writeCrtc.B(0xEE, 0x42, 0x88, 0xE0, 0xEE, 0x4A, 0xC3); // OUT / INC DX / MOV AL,AH / OUT / DEC DX / RET

        void WriteCrtc(byte index, byte value)
        {
            code.B(0xB8, index, value);                    // MOV AX,(value << 8)|index
            code.CallNear(Entry.VirgeCrtcWrite);
        }
    }
}
