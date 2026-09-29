using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.Cards;
using Aurora386.Core.Devices.Usb;

namespace Aurora386.Tests;

/// <summary>
/// The EHCI controller fitted to a machine (plans/usb2-ehci.md): at 00:03.7
/// beside its companion UHCI at 00:03.0, which then reports a multifunction
/// header; the USB stick moving between the two as the guest sets
/// CONFIGFLAG and PORTSC.PortOwner; the interrupt the POST routes; the state
/// in the "UHCI" snapshot section's version 1; vm.json's "ehci-ich9". The
/// controller itself is EhciControllerTests' subject.
/// </summary>
public class MachineEhciTests
{
    private const ushort EhciFunction = (3 << 3) | 7;   // 00:03.7
    private const ushort StorageUhciFunction = 3 << 3;  // 00:03.0
    private const uint EhciIdentity = 0x293A8086;       // device 293Ah, vendor 8086h
    private const uint SerialBusUsbEhci = 0x0C0320;     // class, subclass, programming interface
    private const int HeaderTypeOffset = 0x0E;
    private const byte MultifunctionHeader = 0x80;
    private const int Bar0Offset = 0x10;
    private const int CommandOffset = 0x04;
    private const uint MemorySpaceEnable = 1u << 1;
    private const uint Bar0Address = 0xFEB20000;
    private const uint ConfigFlag = 0x20 + 0x40;        // operational base (CAPLENGTH 20h) + CONFIGFLAG
    private const uint Port0Status = 0x20 + 0x44;       // PORTSC[0]
    private const uint PortOwner = 1u << 13;
    private const uint CurrentConnectStatus = 1u << 0;
    private const ushort StorageUhciPort0 = 0xC120 + 0x10;   // PORTSC1 of the storage UHCI

    [Fact]
    public void EhciAppearsAsFunctionSevenBesideAMultifunctionCompanion()
    {
        using Machine machine = EhciMachine();

        Assert.Equal(EhciIdentity, machine.Pci.ReadConfiguration(EhciFunction, 0, 4));
        Assert.Equal(SerialBusUsbEhci, machine.Pci.ReadConfiguration(EhciFunction, 8, 4) >> 8);
        Assert.Equal(MultifunctionHeader, machine.Pci.ReadConfiguration(StorageUhciFunction, HeaderTypeOffset, 1) & MultifunctionHeader);
    }

    [Fact]
    public void WithoutEhciTheStorageControllerIsASingleFunctionDevice()
    {
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "uhci-piix" } };
        using var machine = new Machine(16 * 1024 * 1024, hardware);

        Assert.Equal(0u, machine.Pci.ReadConfiguration(StorageUhciFunction, HeaderTypeOffset, 1) & MultifunctionHeader);
    }

    /// <summary>INTA of device 3 reaches PIRQB, IRQ 10 by default; the POST writes it into 3Ch.</summary>
    [Fact]
    public void ThePostRoutesTheEhciInterrupt()
    {
        const uint PirqbDefaultIrq = 10;
        using Machine machine = EhciMachine();

        Assert.Equal(PirqbDefaultIrq, machine.Pci.ReadConfiguration(EhciFunction, 0x3C, 1));
    }

    /// <summary>
    /// With CONFIGFLAG 0 the stick is on the companion UHCI (full speed);
    /// once the driver sets CONFIGFLAG it moves to EHCI, and PortOwner hands
    /// it back.
    /// </summary>
    [Fact]
    public void TheStickFollowsConfigFlagAndPortOwner()
    {
        using Machine machine = EhciMachine();
        machine.AttachUsbStorage(Stick());
        MapBar0(machine);

        Assert.Equal(1, machine.Io.ReadWord(StorageUhciPort0) & 1);
        Assert.Equal(0u, machine.Memory.ReadDword(Bar0Address + Port0Status) & CurrentConnectStatus);

        machine.Memory.WriteDword(Bar0Address + ConfigFlag, 1);

        Assert.Equal(0, machine.Io.ReadWord(StorageUhciPort0) & 1);
        Assert.Equal(CurrentConnectStatus, machine.Memory.ReadDword(Bar0Address + Port0Status) & CurrentConnectStatus);

        uint portStatus = machine.Memory.ReadDword(Bar0Address + Port0Status);
        machine.Memory.WriteDword(Bar0Address + Port0Status, (portStatus & ~0x2Au) | PortOwner);   // keep RW1C bits clear

        Assert.Equal(1, machine.Io.ReadWord(StorageUhciPort0) & 1);
    }

    /// <summary>The EHCI state travels in the "UHCI" section's version 1, and the stick is plugged back where CONFIGFLAG says.</summary>
    [Fact]
    public void ASnapshotRestoresEhciAndItsRouting()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (Machine source = EhciMachine())
            {
                source.AttachUsbStorage(Stick());
                MapBar0(source);
                source.Memory.WriteDword(Bar0Address + ConfigFlag, 1);
                MachineSnapshot.Capture(source).Save(path);
            }

            using Machine target = EhciMachine();
            target.AttachUsbStorage(Stick());
            MachineSnapshot.Load(path).RestoreTo(target);
            MapBar0(target);

            Assert.Equal(1u, target.Memory.ReadDword(Bar0Address + ConfigFlag) & 1);
            Assert.Equal(CurrentConnectStatus, target.Memory.ReadDword(Bar0Address + Port0Status) & CurrentConnectStatus);
            Assert.Equal(0, target.Io.ReadWord(StorageUhciPort0) & 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VmJsonEhciCardBuildsTheController()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"aurora-ehci-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new VmConfiguration
            {
                Hardware = { Usb = new VmConfiguration.UsbSettings { Card = "ehci-ich9" } },
            };
            configuration.Save(folder);

            using Machine machine = VmConfiguration.Load(folder).BuildMachine();

            UhciCard card = Assert.Single(machine.Cards.OfType<UhciCard>());
            Assert.NotNull(card.Ehci);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Machine EhciMachine()
    {
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "ehci-ich9" } };
        var machine = new Machine(16 * 1024 * 1024, hardware);
        machine.RunInstructions(200);   // the POST, which routes the PCI interrupts
        return machine;
    }

    private static UsbMassStorageDevice Stick() => new(new MemoryStream(new byte[1024 * 1024]));

    /// <summary>Places BAR0 and enables memory decode, with A20 open as under any protected-mode OS.</summary>
    private static void MapBar0(Machine machine)
    {
        machine.Memory.A20Enabled = true;
        machine.Pci.WriteConfiguration(EhciFunction, Bar0Offset, 4, Bar0Address);
        machine.Pci.WriteConfiguration(EhciFunction, CommandOffset, 2, MemorySpaceEnable);
    }
}
