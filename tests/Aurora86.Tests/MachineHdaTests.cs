using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Cards;

namespace Aurora86.Tests;

/// <summary>
/// Intel HD Audio fitted to a machine (plans/piattaforma-moderna.md M8): its
/// place on the PCI bus, the interrupt the POST routes to it, its registers
/// through a BAR0 the guest places high, its snapshot section and
/// <c>vm.json</c>'s sound list. The controller and codec themselves are
/// HdaControllerTests' subject.
/// </summary>
public class MachineHdaTests
{
    private const ushort HdaFunction = 7 << 3;            // 00:07.0
    private const uint HdaIdentity = 0x293E8086;          // device 293Eh, vendor 8086h
    private const uint MultimediaHdAudio = 0x040300;      // class, subclass, programming interface
    private const int InterruptLineOffset = 0x3C;
    private const int CommandOffset = 0x04;
    private const int Bar0Offset = 0x10;
    private const uint MemorySpaceEnable = 1u << 1;
    private const uint Bar0Address = 0xFEB10000;
    private const uint GlobalCapabilities = 0x00;         // GCAP (16 bits), VMIN, VMAJ
    private const uint GlobalControl = 0x08;              // GCTL
    private const uint ControllerReset = 1u << 0;         // GCTL.CRST

    [Fact]
    public void TheControllerAppearsAtPciDevice7WithTheHdAudioClassCode()
    {
        using Machine machine = HdaMachine();

        Assert.Equal(HdaIdentity, machine.Pci.ReadConfiguration(HdaFunction, 0, 4));
        Assert.Equal(MultimediaHdAudio, machine.Pci.ReadConfiguration(HdaFunction, 8, 4) >> 8);
    }

    /// <summary>The POST routes INTA of device 7 through PIRQB (device 3's link) and records the IRQ in 3Ch.</summary>
    [Fact]
    public void ThePostRoutesTheInterruptAndRecordsItInTheInterruptLine()
    {
        const uint PirqbDefaultIrq = 10;
        using Machine machine = HdaMachine();

        Assert.Equal(PirqbDefaultIrq, machine.Pci.ReadConfiguration(HdaFunction, InterruptLineOffset, 1));
    }

    /// <summary>GCAP 4401h (four input and four output streams, 64-bit capable) and VMAJ 1, read through a BAR0 placed near the top of the 32-bit space.</summary>
    [Fact]
    public void TheRegistersAnswerThroughAHighBar0()
    {
        const uint ExpectedGcapVminVmaj = 0x01004401;
        using Machine machine = HdaMachine();
        MapBar0(machine);

        Assert.Equal(ExpectedGcapVminVmaj, machine.Memory.ReadDword(Bar0Address + GlobalCapabilities));
    }

    [Fact]
    public void TheControllerJoinsTheHostAudioMix()
    {
        using Machine machine = HdaMachine();

        HdaCard card = Assert.Single(machine.Cards.OfType<HdaCard>());
        Assert.Contains(card.Controller, machine.Audio.Sources);
    }

    /// <summary>GCTL.CRST, set by the driver to bring the controller out of reset, survives a snapshot under section "HDA ".</summary>
    [Fact]
    public void ASnapshotCarriesTheControllerRegisters()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (Machine source = HdaMachine())
            {
                MapBar0(source);
                source.Memory.WriteDword(Bar0Address + GlobalControl, ControllerReset);
                MachineSnapshot.Capture(source).Save(path);
            }

            using Machine target = HdaMachine();
            MachineSnapshot.Load(path).RestoreTo(target);
            MapBar0(target);

            Assert.Equal(ControllerReset, target.Memory.ReadDword(Bar0Address + GlobalControl) & ControllerReset);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VmJsonSoundListBuildsTheController()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-hda-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new VmConfiguration();
            configuration.Hardware.Sound = [new VmConfiguration.SoundCardEntry { Card = "hda-ich9" }];
            configuration.Save(folder);

            using Machine machine = VmConfiguration.Load(folder).BuildMachine();

            Assert.Single(machine.Cards.OfType<HdaCard>());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Machine HdaMachine()
    {
        var hardware = new HardwareConfiguration
        {
            Sound = { new CardSettings { Card = "hda-ich9" } },
        };

        var machine = new Machine(16 * 1024 * 1024, hardware);
        machine.RunInstructions(200);   // the POST, which routes the PCI interrupts
        return machine;
    }

    /// <summary>Places BAR0 and enables memory decode, as a driver does; A20 open, as under any protected-mode OS.</summary>
    private static void MapBar0(Machine machine)
    {
        machine.Memory.A20Enabled = true;
        machine.Pci.WriteConfiguration(HdaFunction, Bar0Offset, 4, Bar0Address);
        machine.Pci.WriteConfiguration(HdaFunction, CommandOffset, 2, MemorySpaceEnable);
    }
}
