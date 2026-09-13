using System.Text;
using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Dos;

namespace Aurora386.Tests;

/// <summary>
/// DOS exposes its resident tables through INT 21h, rather than a fixed
/// address. DOS 7 reserves more CDS entries than there are drive letters;
/// installation must accept that table without exposing punctuation drives.
/// </summary>
public sealed class RedirectorInstallerTests
{
    private const uint SwappableDataArea = 0x9000;
    private const uint ListOfLists = 0x9100;
    private const uint CurrentDirectories = 0x30000;
    private const byte LastLetterCount = 26;

    /// <summary>
    /// Boots a tiny disk and supplies the two actual DOS query interrupts.
    /// The test controls the returned structures, not the installer's result.
    /// </summary>
    private static Machine CreateIdleDos(byte rawDriveCount)
    {
        var machine = new Machine();
        var disk = DiskImage.CreateInMemory(0, 80, 2, 18);
        var bootSector = new byte[DiskImage.SectorSize];
        bootSector[0] = 0xEB;                             // JMP back to this instruction
        bootSector[1] = 0xFE;
        bootSector[510] = 0x55;
        bootSector[511] = 0xAA;
        Assert.True(disk.WriteLinear(0, 1, bootSector));
        machine.AttachDisk(disk);
        for (int instruction = 0; instruction < 10_000 && !machine.Booted; instruction++)
        {
            machine.RunInstructions(1);
        }

        Assert.True(machine.Booted);
        machine.Bda.KeyboardPollCount = 10_000;
        machine.Cpu.State.Halted = false;

        // INT 21h AH=52 returns ES:BX=0900:0100; AX=5D06 returns
        // DS:SI=0900:0000. Both paths return through the real IRET frame.
        byte[] handler =
        [
            0x80, 0xFC, 0x52, 0x75, 0x09,
            0xB8, 0x00, 0x09, 0x8E, 0xC0, 0xBB, 0x00, 0x01, 0xCF,
            0xB8, 0x00, 0x09, 0x8E, 0xD8, 0xBE, 0x00, 0x00, 0xCF
        ];
        for (uint offset = 0; offset < handler.Length; offset++)
        {
            machine.Memory.WriteByte(0x20000 + offset, handler[offset]);
        }

        machine.Memory.WriteWord(0x21 * 4, 0);
        machine.Memory.WriteWord(0x21 * 4 + 2, 0x2000);
        machine.Memory.WriteWord(ListOfLists + 0x16, 0);
        machine.Memory.WriteWord(ListOfLists + 0x18, 0x3000);
        machine.Memory.WriteByte(ListOfLists + 0x21, rawDriveCount);
        return machine;
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(26, 26)]
    [InlineData(32, 26)]
    public void DosDriveTablesInstallAllTheirUsableLetters(int rawCount, int usableCount)
    {
        using Machine machine = CreateIdleDos((byte)rawCount);
        machine.Redirector.Mount(4, new HostDirectoryFileSystem(Path.GetTempPath()));
        var installer = new RedirectorInstaller(machine);
        var functions = new List<ushort>();
        machine.Cpu.SoftwareInterruptIssued += vector =>
        {
            if (vector == 0x21)
            {
                functions.Add(machine.Cpu.State.Ax);
            }
        };

        Assert.True(installer.TryInstall(machine.Redirector));
        Assert.Contains((ushort)0x5D06, functions);
        Assert.Contains(functions, value => (value >> 8) == 0x52);
        Assert.Equal(SwappableDataArea, machine.Redirector.SwappableDataArea);
        Assert.Equal(CurrentDirectories, machine.Redirector.CurrentDirectoryStructure);
        Assert.Equal(usableCount, machine.Redirector.DriveCount);
        Assert.Equal(DosStructures.CdsValidFlag | DosStructures.CdsRedirectedFlag,
            machine.Memory.ReadWord(CurrentDirectories + 4 * DosStructures.CdsEntrySize
                + DosStructures.CdsFlags));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    [InlineData(255)]
    public void ImplausibleDosDriveTablesRemainUninstalled(int rawCount)
    {
        using Machine machine = CreateIdleDos((byte)rawCount);
        var installer = new RedirectorInstaller(machine);

        Assert.False(installer.TryInstall(machine.Redirector));
        Assert.False(machine.Redirector.IsInstalled);
    }

    [Fact]
    public void Dos7ReservedEntriesNeverBecomeLettersOrReceiveMountFlags()
    {
        using Machine machine = CreateIdleDos(32);
        uint reservedStart = CurrentDirectories + LastLetterCount * DosStructures.CdsEntrySize;
        int reservedLength = 6 * DosStructures.CdsEntrySize;
        for (uint offset = 0; offset < reservedLength; offset++)
        {
            machine.Memory.WriteByte(reservedStart + offset, 0xA5);
        }

        var installer = new RedirectorInstaller(machine);
        Assert.True(installer.TryInstall(machine.Redirector));
        var share = new HostDirectoryFileSystem(Path.GetTempPath());
        for (byte drive = 4; drive < 25; drive++)
        {
            Assert.True(machine.Redirector.Mount(drive, share));
        }

        Assert.Equal((byte)25, machine.NextSharedFolderDrive());
        Assert.True(machine.Redirector.Mount(25, share));
        Assert.False(machine.TryGetNextSharedFolderDrive(out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => machine.Redirector.Mount(26, share));
        for (uint offset = 0; offset < reservedLength; offset++)
        {
            Assert.Equal((byte)0xA5, machine.Memory.ReadByte(reservedStart + offset));
        }
    }

    /// <summary>Runs the shell EXEC request through the real CPU, then leaves an idle guest loop.</summary>
    private static void LaunchCommandProcessor(Machine machine)
    {
        const uint caller = 0x40000;
        byte[] path = Encoding.ASCII.GetBytes(@"C:\WINDOWS\COMMAND.COM" + "\0");
        for (uint offset = 0; offset < path.Length; offset++)
        {
            machine.Memory.WriteByte(0x50000 + offset, path[offset]);
        }

        byte[] program = [0xB8, 0x00, 0x4B, 0xCD, 0x21, 0xEB, 0xFE];
        for (uint offset = 0; offset < program.Length; offset++)
        {
            machine.Memory.WriteByte(caller + offset, program[offset]);
        }

        machine.Cpu.SetSegment(SegReg.Cs, 0x4000);
        machine.Cpu.SetSegment(SegReg.Ds, 0x5000);
        machine.Cpu.SetSegment(SegReg.Ss, 0x6000);
        machine.Cpu.State.Eip = 0;
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Dx = 0;

        for (int instruction = 0; instruction < 30; instruction++)
        {
            machine.Cpu.Step();
            if (machine.Cpu.State.Cs.Selector == 0x4000 && machine.Cpu.State.Eip == 5)
            {
                break;
            }
        }

        Assert.True(machine.CommandProcessorStarted);
        Assert.Equal(5u, machine.Cpu.State.Eip);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADueRefreshUsesTheNextSafeDosWindowAndConfirmsTheMultiplexHook(bool tableMoves)
    {
        using Machine machine = CreateIdleDos(3);
        machine.Redirector.Mount(4, new HostDirectoryFileSystem(Path.GetTempPath()));
        Assert.True(new RedirectorInstaller(machine).TryInstall(machine.Redirector));
        LaunchCommandProcessor(machine);
        machine.RedirectorHooksMultiplex = true;
        machine.Memory.WriteDword(0x2F * 4, 0x20000100);
        machine.Memory.WriteByte(0x20100, 0xCF); // Previous DOS multiplex handler: IRET.
        uint finalTable = tableMoves ? 0x70000u : CurrentDirectories;
        machine.Memory.WriteWord(ListOfLists + 0x18, (ushort)(finalTable >> 4));
        machine.Memory.WriteByte(ListOfLists + 0x21, 32);

        int queries = 0;
        machine.Cpu.SoftwareInterruptIssued += vector =>
        {
            if (vector == 0x21)
            {
                queries++;
            }
        };
        machine.Memory.WriteByte(SwappableDataArea + 1, 1); // DOS owns its internal stack.
        machine.RunInstructions(2000);
        Assert.Equal(0, queries);
        Assert.Equal(3, machine.Redirector.DriveCount);
        Assert.Equal(0x20000100u, machine.Memory.ReadDword(0x2F * 4));

        machine.Memory.WriteByte(SwappableDataArea + 1, 0);
        machine.RunInstructions(2000);
        Assert.Equal(finalTable, machine.Redirector.CurrentDirectoryStructure);
        Assert.Equal(26, machine.Redirector.DriveCount);
        Assert.Equal(2, queries);
        machine.RunInstructions(2000); // Confirm unchanged structures while DOS is still safe.
        Assert.Equal(4, queries);
        machine.RunInstructions(2000); // Successful confirmation restores the normal interval.
        Assert.Equal(4, queries);
        Assert.Equal((ushort)0xF000, machine.Memory.ReadWord(0x2F * 4 + 2));
        Assert.NotEqual(0x20000100u, machine.Memory.ReadDword(0x2F * 4));

        // A real multiplex installation query must reach the newly installed
        // redirector. The displaced IRET handler would leave AL at zero.
        byte[] query = [0xB8, 0x00, 0x11, 0xCD, 0x2F, 0xF4];
        for (uint offset = 0; offset < query.Length; offset++)
        {
            machine.Memory.WriteByte(0x40000 + offset, query[offset]);
        }
        machine.Cpu.State.Eip = 0;

        for (int instruction = 0; instruction < 100 && !machine.Cpu.State.Halted; instruction++)
        {
            machine.Cpu.Step();
        }

        Assert.True(machine.Cpu.State.Halted);
        Assert.Equal((ushort)0x11FF, machine.Cpu.State.Ax);

    }

    [Fact]
    public void AConfirmedHookKeepsTheNormalIntervalAfterALaterBusyDosCall()
    {
        using Machine machine = CreateIdleDos(32);
        machine.Redirector.Mount(4, new HostDirectoryFileSystem(Path.GetTempPath()));
        Assert.True(new RedirectorInstaller(machine).TryInstall(machine.Redirector));
        LaunchCommandProcessor(machine);
        machine.RedirectorHooksMultiplex = true;
        machine.Memory.WriteDword(0x2F * 4, 0x20000100);
        machine.Memory.WriteByte(0x20100, 0xCF);

        int queries = 0;
        machine.Cpu.SoftwareInterruptIssued += vector =>
        {
            if (vector == 0x21)
            {
                queries++;
            }
        };
        machine.RunInstructions(2000);
        Assert.Equal(2, queries);
        Assert.NotEqual(0x20000100u, machine.Memory.ReadDword(0x2F * 4));

        machine.Memory.WriteByte(SwappableDataArea + 1, 1);
        machine.RunInstructions(400_000); // The next normally scheduled query is busy.
        Assert.Equal(2, queries);
        machine.Memory.WriteByte(SwappableDataArea + 1, 0);
        machine.RunInstructions(2000);
        Assert.Equal(2, queries); // A confirmed hook must not restart aggressive startup polling.
    }

    [Fact]
    public void BusyDosAttemptsCannotCertifyTheProvisionalTableOrTakeItsMultiplexVector()
    {
        using Machine machine = CreateIdleDos(3);
        machine.Redirector.Mount(4, new HostDirectoryFileSystem(Path.GetTempPath()));
        Assert.True(new RedirectorInstaller(machine).TryInstall(machine.Redirector));
        LaunchCommandProcessor(machine);
        machine.RedirectorHooksMultiplex = true;
        machine.Memory.WriteDword(0x2F * 4, 0x20000100);
        machine.Memory.WriteByte(0x20100, 0xCF);
        machine.Memory.WriteByte(SwappableDataArea + 1, 1);
        for (int batch = 0; batch < 80_000; batch++)
        {
            machine.RunInstructions(1);
        }

        Assert.Equal(3, machine.Redirector.DriveCount);
        Assert.Equal(0x20000100u, machine.Memory.ReadDword(0x2F * 4));
    }

}
