using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Cards;
using Aurora86.Core.Devices.Usb;

namespace Aurora86.Tests;

/// <summary>
/// The xHCI controller fitted to a machine (plans/usb3-xhci.md): at PCI
/// device 8, the slot the PIRQ rotation gained for it; the interrupt the POST
/// routes to it; its registers through a 64-bit BAR0 the guest places high;
/// a USB stick on port 0; its "XHCI" snapshot section; vm.json's
/// hardware.xhci. The register engine is XhciControllerTests' subject.
/// </summary>
public class MachineXhciTests
{
    private const ushort XhciFunction = 8 << 3;           // 00:08.0
    private const uint XhciIdentity = 0x000D1B36;         // device 000Dh, vendor 1B36h
    private const uint SerialBusUsbXhci = 0x0C0330;       // class, subclass, programming interface
    private const int Bar0Offset = 0x10;
    private const int Bar0HighOffset = 0x14;
    private const int CommandOffset = 0x04;
    private const uint MemorySpaceEnable = 1u << 1;
    private const uint Bar0Address = 0xFEB40000;
    private const uint CapabilityLength = 0x40;           // CAPLENGTH, and HCIVERSION 0100h above it
    private const uint Port1Status = 0x40 + 0x400;        // operational base + PORTSC for port 1
    private const uint CurrentConnectStatus = 1u << 0;

    [Fact]
    public void TheControllerAppearsAtPciDevice8WithTheXhciClassCode()
    {
        using Machine machine = XhciMachine();

        Assert.Equal(XhciIdentity, machine.Pci.ReadConfiguration(XhciFunction, 0, 4));
        Assert.Equal(SerialBusUsbXhci, machine.Pci.ReadConfiguration(XhciFunction, 8, 4) >> 8);
    }

    /// <summary>INTA of device 8 reaches PIRQC (device 4's link), IRQ 11 by default; the POST writes it into 3Ch.</summary>
    [Fact]
    public void ThePostRoutesTheInterrupt()
    {
        const uint PirqcDefaultIrq = 11;
        using Machine machine = XhciMachine();

        Assert.Equal(PirqcDefaultIrq, machine.Pci.ReadConfiguration(XhciFunction, 0x3C, 1));
    }

    [Fact]
    public void TheCapabilityRegistersAnswerThroughAHighBar0()
    {
        using Machine machine = XhciMachine();
        MapBar0(machine);

        uint capabilityDword = machine.Memory.ReadDword(Bar0Address);
        Assert.Equal(CapabilityLength, capabilityDword & 0xFF);
        Assert.Equal(0x0100u, capabilityDword >> 16);
    }

    /// <summary>A stick attached before the guest looks shows as a connection on port 1 (PORTSC.CCS).</summary>
    [Fact]
    public void AnAttachedStickShowsAsAConnectionOnPortOne()
    {
        using Machine machine = XhciMachine();
        machine.AttachXhciStorage(Stick());
        MapBar0(machine);

        Assert.Equal(CurrentConnectStatus, machine.Memory.ReadDword(Bar0Address + Port1Status) & CurrentConnectStatus);
    }

    [Fact]
    public void AttachingAStickWithoutAControllerIsRefused()
    {
        using var machine = new Machine();

        Assert.Throws<InvalidOperationException>(() => machine.AttachXhciStorage(Stick()));
    }

    /// <summary>The controller's state travels in the "XHCI" section: USBCMD.RS written before the snapshot reads back after it.</summary>
    [Fact]
    public void ASnapshotCarriesTheControllerRegisters()
    {
        const uint UsbCommand = 0x40;                     // operational base + USBCMD
        const uint RunStop = 1u << 0;
        string path = Path.GetTempFileName();
        try
        {
            using (Machine source = XhciMachine())
            {
                MapBar0(source);
                source.Memory.WriteDword(Bar0Address + UsbCommand, RunStop);
                MachineSnapshot.Capture(source).Save(path);
            }

            using Machine target = XhciMachine();
            MachineSnapshot.Load(path).RestoreTo(target);
            MapBar0(target);

            Assert.Equal(RunStop, target.Memory.ReadDword(Bar0Address + UsbCommand) & RunStop);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VmJsonHardwareXhciBuildsTheControllerWithItsStick()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-xhci-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string stickPath = Path.Combine(folder, "stick.img");
            File.WriteAllBytes(stickPath, new byte[1024 * 1024]);
            var configuration = new VmConfiguration
            {
                Hardware = { Xhci = new VmConfiguration.XhciSettings { Storage = new VmConfiguration.UsbDiskEntry { Path = stickPath } } },
            };
            configuration.Save(folder);

            using Machine machine = VmConfiguration.Load(folder).BuildMachine();
            machine.RunInstructions(200);
            MapBar0(machine);

            Assert.NotNull(machine.Xhci);
            Assert.Equal(CurrentConnectStatus, machine.Memory.ReadDword(Bar0Address + Port1Status) & CurrentConnectStatus);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void VmJsonHardwareXhciRefusesAUsbCardThatIsNotAnXhciController()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-xhci-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            // "uhci-piix" is a usb card too, but has no xHCI port for the stick.
            var configuration = new VmConfiguration
            {
                Hardware = { Xhci = new VmConfiguration.XhciSettings { Card = "uhci-piix" } },
            };
            configuration.Save(folder);

            Assert.Throws<InvalidDataException>(() => VmConfiguration.Load(folder).BuildMachine().Dispose());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void VmJsonHardwareUsbRefusesTheXhciController()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-xhci-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new VmConfiguration { Hardware = { Usb = new VmConfiguration.UsbSettings { Card = "qemu-xhci" } } };
            configuration.Save(folder);

            Assert.Throws<InvalidDataException>(() => VmConfiguration.Load(folder).BuildMachine().Dispose());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Machine XhciMachine()
    {
        var hardware = new HardwareConfiguration { Xhci = new CardSettings { Card = "qemu-xhci" } };
        var machine = new Machine(16 * 1024 * 1024, hardware);
        machine.RunInstructions(200);   // the POST, which routes the PCI interrupts
        return machine;
    }

    private static UsbMassStorageDevice Stick() => new(new MemoryStream(new byte[1024 * 1024]));

    /// <summary>Places the 64-bit BAR0 below 4 GB and enables memory decode, with A20 open as under any protected-mode OS.</summary>
    private static void MapBar0(Machine machine)
    {
        machine.Memory.A20Enabled = true;
        machine.Pci.WriteConfiguration(XhciFunction, Bar0HighOffset, 4, 0);
        machine.Pci.WriteConfiguration(XhciFunction, Bar0Offset, 4, Bar0Address);
        machine.Pci.WriteConfiguration(XhciFunction, CommandOffset, 2, MemorySpaceEnable);
    }
}
