using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Parallel;
using Aurora386.Core.Firmware;

namespace Aurora386.Tests;

/// <summary>
/// LPT1 and its printer as fitted to a machine (docs/PARALLEL-PORT.md): what
/// the BIOS publishes about the port, INT 17h run through the real CPU, the
/// Plug and Play node Windows 95 finds it by, the job reaching the host, and
/// the registers surviving a snapshot.
/// </summary>
public class MachinePrinterTests
{
    private const ushort BiosDataSegment = 0x40;
    private const uint Lpt1BaseAddress = 0x408;
    private const uint EquipmentWordAddress = 0x410;
    private const ushort ParallelPortCountMask = 0xC000;
    private const ushort OneParallelPort = 0x4000;
    private const byte ReadyStatus = 0x90;

    [Fact]
    public void AfterPostTheBiosDataAreaNamesLpt1AndTheEquipmentWordCountsOnePrinter()
    {
        using Machine machine = Started();

        Assert.Equal(ParallelPort.DataPort, machine.Memory.ReadWord(Lpt1BaseAddress));
        Assert.Equal(OneParallelPort, machine.Memory.ReadWord(EquipmentWordAddress) & ParallelPortCountMask);
    }

    [Fact]
    public void Int17StatusReportsAReadyPrinter()
    {
        using Machine machine = Started();

        Int17(machine, ax: 0x0200, dx: 0);

        Assert.Equal(ReadyStatus, machine.Cpu.State.GetReg8(4));   // AH
    }

    /// <summary>
    /// What DOS's PRN device does for every character: INT 17h AH=00h. The
    /// bytes reach the host folder as a .prn job, and a plain-text job as a
    /// PDF too, once the job is closed.
    /// </summary>
    [Fact]
    public void CharactersPrintedThroughInt17ReachTheHostFolderAsAJob()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-printer-{Guid.NewGuid():N}");
        try
        {
            using Machine machine = Started();
            machine.PrintOutputFolder = folder;

            foreach (char character in "OK\r\n")
            {
                Int17(machine, ax: (ushort)character, dx: 0);
                Assert.Equal(ReadyStatus, machine.Cpu.State.GetReg8(4));
            }

            machine.Printer.Flush();

            string job = Assert.Single(Directory.GetFiles(folder, "*.prn"));
            Assert.Equal("OK\r\n"u8.ToArray(), File.ReadAllBytes(job));
            Assert.Single(Directory.GetFiles(folder, "*.pdf"));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void AnAbsentPrinterNumberTimesOut()
    {
        const byte TimeOut = 0x01;
        using Machine machine = Started();

        Int17(machine, ax: 0x0200, dx: 1);

        Assert.Equal(TimeOut, machine.Cpu.State.GetReg8(4));
    }

    [Fact]
    public void ThePlugAndPlayBiosListsLpt1WithItsPortsAndInterrupt()
    {
        using Machine machine = Started();

        byte[] node = machine.PlugAndPlay.Nodes.First(candidate =>
            candidate[3..7].SequenceEqual(PnpBios.CompressId("PNP0400")));

        Assert.Equal(0x07, node[7]);   // communication device
        Assert.Equal(0x01, node[8]);   // parallel port
    }

    [Fact]
    public void ASnapshotCarriesTheControlRegister()
    {
        const byte InterruptEnableAndNotInit = 0x14;
        const byte ControlReadsBitsFiveToSevenAsOne = 0xE0;
        string path = Path.GetTempFileName();
        try
        {
            using (Machine source = Started())
            {
                source.Io.WriteByte(ParallelPort.ControlPort, InterruptEnableAndNotInit);
                MachineSnapshot.Capture(source).Save(path);
            }

            using Machine target = Started();
            MachineSnapshot.Load(path).RestoreTo(target);

            Assert.Equal(InterruptEnableAndNotInit | ControlReadsBitsFiveToSevenAsOne, target.Io.ReadByte(ParallelPort.ControlPort));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ACardConfiguredOverTheLpt1PortsIsRefused()
    {
        var hardware = new Aurora386.Core.Devices.Cards.HardwareConfiguration
        {
            Network = { new Aurora386.Core.Devices.Cards.CardSettings { Card = "ne2000-isa", Port = 0x360, Irq = 10 } },
        };

        Assert.Throws<InvalidOperationException>(() => new Machine(4 * 1024 * 1024, hardware));
    }

    /// <summary>
    /// A Sound Blaster on IRQ 7 is a period configuration and stays allowed;
    /// LPT1's Plug and Play node then lists no interrupt rather than one the
    /// card also drives.
    /// </summary>
    [Fact]
    public void AnIsaCardOnIrq7LeavesLpt1DescribedWithoutAnInterrupt()
    {
        const byte SmallIrqTag = 0x22;
        var hardware = new Aurora386.Core.Devices.Cards.HardwareConfiguration
        {
            Sound = { new Aurora386.Core.Devices.Cards.CardSettings { Card = "sb16", Port = 0x220, Irq = 7, Dma = 1, HighDma = 5 } },
        };
        using var machine = new Machine(4 * 1024 * 1024, hardware);
        machine.RunInstructions(200);

        byte[] node = machine.PlugAndPlay.Nodes.First(candidate =>
            candidate[3..7].SequenceEqual(PnpBios.CompressId("PNP0400")));

        Assert.False(machine.ParallelPortInterruptReported);
        Assert.DoesNotContain(SmallIrqTag, node[12..]);
    }

    /// <summary>
    /// A reboot ends the job the printer was receiving: it is delivered as it
    /// stands, not merged with what the next boot prints.
    /// </summary>
    [Fact]
    public void ARebootDeliversTheOpenJobInsteadOfMergingIt()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-printer-{Guid.NewGuid():N}");
        try
        {
            using Machine machine = Started();
            machine.PrintOutputFolder = folder;
            Int17(machine, ax: 'A', dx: 0);

            machine.Cpu.Reset();
            machine.RunInstructions(200);                  // the POST runs again
            Int17(machine, ax: 'B', dx: 0);
            machine.Printer.Flush();

            Assert.Equal(2, Directory.GetFiles(folder, "*.prn").Length);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static Machine Started()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        return machine;
    }

    /// <summary>INT 17h through the real CPU, from a small program at 2000:0100.</summary>
    private static void Int17(Machine machine, ushort ax, ushort dx)
    {
        machine.Cpu.State.Ax = ax;
        machine.Cpu.State.Dx = dx;
        byte[] code = [0xCD, 0x17, 0xF4];                         // INT 17h / HLT
        code.CopyTo(machine.Memory.GetRamSpan(0x20100, code.Length));
        machine.Cpu.SetSegment(SegReg.Cs, 0x2000);
        machine.Cpu.State.Eip = 0x100;
        machine.Cpu.SetSegment(SegReg.Ss, 0x3000);
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;
        for (int count = 0; count < 10_000 && !machine.Cpu.State.Halted; count++)
        {
            machine.RunInstructions(1);
        }

        Assert.True(machine.Cpu.State.Halted);
    }
}
