using Aurora386.Core;
using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
namespace Aurora386.Tests;

/// <summary>
/// Tests against the whole machine: power-on, POST, loading the boot sector,
/// and running code that calls into the BIOS services.
/// </summary>
public class MachineTests
{
    /// <summary>
    /// Builds a boot sector around the given code, complete with the 0xAA55
    /// signature the BIOS insists on before it will boot anything.
    /// </summary>
    private static byte[] BuildBootSector(params byte[] code)
    {
        var sector = new byte[512];
        code.CopyTo(sector, 0);
        sector[510] = 0x55;
        sector[511] = 0xAA;
        return sector;
    }

    private static DiskImage CreateBootableFloppy(byte[] bootSector)
    {
        DiskImage image = DiskImage.CreateInMemory(0x00, 80, 2, 18);   // 1.44 MB
        image.WriteSectors(0, 0, 1, 1, bootSector);
        return image;
    }

    [Fact]
    public void PowerOnRunsPostAndFillsTheBda()
    {
        using var machine = new Machine();

        // The CPU starts at the reset vector and runs into the POST.
        machine.RunInstructions(50);

        Assert.Equal(639, machine.Bda.MemorySizeKb);   // the last kilobyte is the EBDA
        Assert.Equal(0x03, machine.Bda.VideoMode);
        Assert.Equal(80, machine.Bda.VideoColumns);
        Assert.Equal(24, machine.Bda.VideoRowsMinusOne);
        Assert.Equal(0x3D4, machine.Bda.CrtcPort);
    }

    [Fact]
    public void PostInstallsTheInterruptVectorTable()
    {
        using var machine = new Machine();
        machine.RunInstructions(50);

        // Every one of these vectors must point into the BIOS segment.
        foreach (byte vector in new byte[] { 0x08, 0x09, 0x10, 0x13, 0x16, 0x1A })
        {
            ushort segment = machine.Memory.ReadWord((uint)vector * 4 + 2);
            Assert.Equal(0xF000, segment);
        }
    }

    [Fact]
    public void MachineHaltsWithoutABootableDisk()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        Assert.False(machine.Booted);
        Assert.True(machine.Cpu.State.Halted);
    }

    [Fact]
    public void SectorWithoutTheBootSignatureIsNotBooted()
    {
        using var machine = new Machine();

        // Perfectly valid content, but no signature at the end.
        var sector = new byte[512];
        sector[0] = 0xF4;   // HLT
        DiskImage image = DiskImage.CreateInMemory(0x00, 80, 2, 18);
        image.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(image);

        machine.RunInstructions(200);
        Assert.False(machine.Booted);
    }

    [Fact]
    public void BootSectorIsLoadedAndExecuted()
    {
        // The sector writes a recognisable value into memory and halts.
        //   B8 34 12    MOV AX,0x1234
        //   A3 00 60    MOV [0x6000],AX
        //   F4          HLT
        byte[] boot = BuildBootSector(0xB8, 0x34, 0x12, 0xA3, 0x00, 0x60, 0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(500);

        Assert.True(machine.Booted);
        Assert.Equal(0x1234, machine.Memory.ReadWord(0x6000));
    }

    [Fact]
    public void BootSectorReceivesTheDriveNumberInDl()
    {
        // The sector saves DL, which the BIOS must have loaded with the
        // number of the drive it was read from -- that is how a real boot
        // sector knows where to look for the rest of the system.
        //   88 16 00 60   MOV [0x6000],DL
        //   F4            HLT
        byte[] boot = BuildBootSector(0x88, 0x16, 0x00, 0x60, 0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(500);

        Assert.Equal(0x00, machine.Memory.ReadByte(0x6000));   // the first floppy
    }

    [Fact]
    public void Int10hWritesCharactersToTheScreen()
    {
        // Writes "OK" through the teletype function, which is what DOS uses
        // for very nearly all of its output.
        //   B4 0E       MOV AH,0x0E
        //   B0 4F       MOV AL,'O'
        //   CD 10       INT 10h
        //   B0 4B       MOV AL,'K'
        //   CD 10       INT 10h
        //   F4          HLT
        byte[] boot = BuildBootSector(
            0xB4, 0x0E, 0xB0, 0x4F, 0xCD, 0x10,
            0xB0, 0x4B, 0xCD, 0x10, 0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(1000);

        string screen = machine.GetScreenText();
        Assert.StartsWith("OK", screen);
    }

    [Fact]
    public void CarriageReturnAndLineFeedMoveTheCursor()
    {
        // "AB" followed by CR LF, then "C": the C must land on the next line.
        var code = new List<byte> { 0xB4, 0x0E };   // MOV AH,0x0E
        foreach (char ch in "AB\r\nC")
        {
            code.Add(0xB0); code.Add((byte)ch);     // MOV AL,ch
            code.Add(0xCD); code.Add(0x10);         // INT 10h
        }
        code.Add(0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(BuildBootSector([.. code])));
        machine.RunInstructions(2000);

        string[] lines = machine.GetScreenText().Split(Environment.NewLine);
        Assert.StartsWith("AB", lines[0]);
        Assert.StartsWith("C", lines[1]);
    }

    [Fact]
    public void Int13hReadsSectorsIntoMemory()
    {
        // Reads the disk's second sector to 0x6000 and halts.
        //   B8 00 00    MOV AX,0
        //   8E C0       MOV ES,AX
        //   BB 00 60    MOV BX,0x6000
        //   B4 02       MOV AH,2       (read sectors)
        //   B0 01       MOV AL,1       (one sector)
        //   B5 00       MOV CH,0       (cylinder 0)
        //   B1 02       MOV CL,2       (sector 2 -- sectors count from 1)
        //   B6 00       MOV DH,0       (head 0)
        //   B2 00       MOV DL,0       (first floppy)
        //   CD 13       INT 13h
        //   F4          HLT
        byte[] boot = BuildBootSector(
            0xB8, 0x00, 0x00, 0x8E, 0xC0, 0xBB, 0x00, 0x60,
            0xB4, 0x02, 0xB0, 0x01, 0xB5, 0x00, 0xB1, 0x02,
            0xB6, 0x00, 0xB2, 0x00, 0xCD, 0x13, 0xF4);

        using var machine = new Machine();
        DiskImage image = CreateBootableFloppy(boot);

        // Put something recognisable in the second sector.
        var payload = new byte[512];
        "SETTORE-DUE"u8.CopyTo(payload);
        image.WriteSectors(0, 0, 2, 1, payload);
        machine.AttachDisk(image);

        machine.RunInstructions(2000);

        string loaded = System.Text.Encoding.ASCII.GetString(
            machine.Memory.GetRamSpan(0x6000, 11).ToArray());
        Assert.Equal("SETTORE-DUE", loaded);
    }

    [Fact]
    public void TimerInterruptIncrementsTheTickCount()
    {
        // The sector enables interrupts and waits.
        //   FB          STI
        //   F4          HLT
        //   EB FD       JMP -3  (back to the HLT)
        byte[] boot = BuildBootSector(0xFB, 0xF4, 0xEB, 0xFD);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));

        machine.RunInstructions(500);          // run up to the wait loop
        uint before = machine.Bda.TimerTicks;

        // Long enough for the timer to fire several times.
        machine.RunFor(TimeSpan.FromMilliseconds(500));

        Assert.True(machine.Bda.TimerTicks > before,
            $"the tick counter did not advance: {before} -> {machine.Bda.TimerTicks}");
    }

    [Fact]
    public void KeyPressIsDeliveredThroughInt16h()
    {
        // Waits for a keystroke and stores its ASCII code.
        //   B4 00       MOV AH,0
        //   CD 16       INT 16h
        //   A2 00 60    MOV [0x6000],AL
        //   F4          HLT
        byte[] boot = BuildBootSector(0xFB, 0xB4, 0x00, 0xCD, 0x16, 0xA2, 0x00, 0x60, 0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));

        machine.RunInstructions(500);       // run up to the key wait

        machine.PressKey(0x1E);             // scancode for A
        machine.RunInstructions(3000);

        Assert.Equal((byte)'a', machine.Memory.ReadByte(0x6000));
    }

    [Fact]
    public void Int15hReportsTheExtendedMemorySize()
    {
        //   B4 88       MOV AH,0x88
        //   CD 15       INT 15h
        //   A3 00 60    MOV [0x6000],AX
        //   F4          HLT
        byte[] boot = BuildBootSector(0xB4, 0x88, 0xCD, 0x15, 0xA3, 0x00, 0x60, 0xF4);

        using var machine = new Machine(16 * 1024 * 1024);
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(1000);

        // 16 MB less the first megabyte = 15360 KB.
        Assert.Equal(15360, machine.Memory.ReadWord(0x6000));
    }

    [Fact]
    public void Int12hReportsTheConventionalMemorySize()
    {
        byte[] boot = BuildBootSector(0xCD, 0x12, 0xA3, 0x00, 0x60, 0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(1000);

        Assert.Equal(639, machine.Memory.ReadWord(0x6000));   // 640 minus the EBDA
    }
}
