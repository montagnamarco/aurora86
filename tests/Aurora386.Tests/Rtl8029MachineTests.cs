using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.Cards;
using Aurora386.Core.Devices.Chipset;
using Aurora386.Core.Devices.Network;

namespace Aurora386.Tests;

/// <summary>
/// The 1997 RTL8029AS board in a complete PC: PCI enumeration, coexistence
/// with ISA and USB cards, network ownership and resumable guest state.
/// </summary>
public class Rtl8029MachineTests
{
    private const ushort NetworkFunction = 0x0028;
    private const int RamSize = 4 * 1024 * 1024;

    [Theory]
    [InlineData(Chipset.I430Fx)]
    [InlineData(Chipset.I430Tx)]
    public void PciNetworkAndUsbKeepDistinctFunctionsWithoutAnIsaNetworkIdentity(Chipset chipset)
    {
        HardwareConfiguration hardware = Configuration();
        hardware.Usb = new CardSettings { Card = "uhci-piix" };
        using var machine = new Machine(RamSize, hardware, chipset);

        Assert.Equal(0x802910ECu, machine.Pci.ReadConfiguration(NetworkFunction, 0, 4));
        ushort primaryUsb = chipset == Chipset.I430Tx ? (ushort)0x000A : (ushort)0x0010;
        Assert.NotEqual(uint.MaxValue, machine.Pci.ReadConfiguration(primaryUsb, 0, 4));
        Assert.NotEqual(uint.MaxValue, machine.Pci.ReadConfiguration(0x0018, 0, 4));
        Assert.DoesNotContain(machine.IsaPlugAndPlay.Cards,
            card => card.LogicalDevices.Any(device => device.Id == "RTL8019"));
        Rtl8029Card network = Assert.Single(machine.Cards.OfType<Rtl8029Card>());
        Assert.Same(network.Device.Core, machine.Network);
    }

    [Fact]
    public void ConfigurationSelectsPciNetworkingThroughTheExistingNetworkEntry()
    {
        var configuration = new VmConfiguration();
        configuration.Hardware.Network =
        [
            new VmConfiguration.NetworkCardEntry { Card = "rtl8029-pci", Port = "340" },
        ];
        using Machine machine = configuration.BuildMachine();

        Assert.Single(machine.Cards.OfType<Rtl8029Card>());
        Assert.Equal(0x21, machine.Io.ReadByte(0x340));
        Assert.Equal("network", CardCatalog.KindOf("rtl8029-pci"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyNetworkServiceUsesTheFirstConfiguredCard(bool pciFirst)
    {
        HardwareConfiguration hardware = Configuration();
        var isa = new CardSettings { Card = "ne2000-isa", Port = 0x340, Irq = 3 };
        hardware.Network.Insert(pciFirst ? 1 : 0, isa);
        using var machine = new Machine(RamSize, hardware);
        Ne2000 isaDevice = Assert.Single(machine.Cards.OfType<Ne2000Card>()).Device;
        Ne2000 pciDevice = Assert.Single(machine.Cards.OfType<Rtl8029Card>()).Device.Core;

        Assert.Same(pciFirst ? pciDevice : isaDevice, machine.Network);
        Assert.NotEqual(0xFF, machine.Io.ReadByte(0x300));
        Assert.NotEqual(0xFF, machine.Io.ReadByte(0x340));
        var replacement = new DisposableBackend();
        machine.ConnectNetwork(replacement);
        Assert.Same(replacement, machine.Network!.Backend);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CardsCannotClaimTheSameInitialIoWindow(int isaPosition)
    {
        HardwareConfiguration hardware = Configuration();
        hardware.Network.Insert(isaPosition,
            new CardSettings { Card = "ne2000-isa", Port = 0x300, Irq = 3 });

        Assert.Throws<InvalidOperationException>(() => new Machine(RamSize, hardware));
    }

    [Fact]
    public void DuplicatePciCardsAreRejectedBeforeOpeningTheirNatForwards()
    {
        HardwareConfiguration hardware = Configuration();
        hardware.Network.Add(new CardSettings
        {
            Card = "rtl8029-pci",
            Port = 0x340,
            Mode = NetworkMode.Nat,
            // Invalid socket input would throw a different exception if opened.
            Forwards = { new PortForward { HostPort = -1, GuestPort = 80 } },
        });

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new Machine(RamSize, hardware));
        Assert.Contains("R829", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposingTheMachineClosesEveryDistinctNetworkBackendOnce(bool shared)
    {
        HardwareConfiguration hardware = Configuration();
        hardware.Network.Add(new CardSettings { Card = "ne2000-isa", Port = 0x340, Irq = 3 });
        var machine = new Machine(RamSize, hardware);
        var first = new DisposableBackend();
        DisposableBackend second = shared ? first : new DisposableBackend();
        Assert.Single(machine.Cards.OfType<Rtl8029Card>()).Device.Core.Backend = first;
        Assert.Single(machine.Cards.OfType<Ne2000Card>()).Device.Backend = second;

        machine.Dispose();

        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public void ReplacingOneWireDoesNotCloseTheOtherCardsSharedBackend()
    {
        HardwareConfiguration hardware = Configuration();
        hardware.Network.Add(new CardSettings { Card = "ne2000-isa", Port = 0x340, Irq = 3 });
        using var machine = new Machine(RamSize, hardware);
        var shared = new DisposableBackend();
        machine.Network!.Backend = shared;
        Assert.Single(machine.Cards.OfType<Ne2000Card>()).Device.Backend = shared;
        var replacement = new DisposableBackend();

        machine.ConnectNetwork(replacement);

        Assert.Equal(0, shared.Disposals);
        Assert.Same(shared, Assert.Single(machine.Cards.OfType<Ne2000Card>()).Device.Backend);
        Assert.Same(replacement, machine.Network.Backend);
    }

    [Fact]
    public void SnapshotRestoresPciDecodeAndPageThreeIntoAFreshMachineKeepingItsBackend()
    {
        using var source = new Machine(RamSize, Configuration());
        source.Pci.WriteConfiguration(NetworkFunction, 0x10, 4, 0x341);
        source.Io.WriteByte(0x340, 0xE1); // Stop, page three.
        source.Io.WriteByte(0x341, 0xC0); // Unlock configuration writes.
        source.Io.WriteByte(0x346, 0x40); // Full duplex.
        source.Io.WriteByte(0x341, 0);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        using var target = new Machine(RamSize, Configuration());
        var backend = new DisposableBackend();
        target.ConnectNetwork(backend);

        snapshot.RestoreTo(target);

        Assert.Equal(0xFF, target.Io.ReadByte(0x300));
        Assert.Equal(0x40, target.Io.ReadByte(0x346));
        Assert.Same(backend, target.Network!.Backend);
        Assert.Equal(0, backend.Disposals);
    }

    [Fact]
    public void InvalidEmbeddedIsaIdentityIsRejectedWhileLoadingTheSnapshot()
    {
        using var machine = new Machine(RamSize, Configuration());
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            byte[] bytes = File.ReadAllBytes(path);
            int position = 8; // Container magic precedes section headers.
            bool found = false;
            while (position < bytes.Length)
            {
                string name = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
                int length = BitConverter.ToInt32(bytes, position + 6);
                int payload = position + 10;
                if (name == Rtl8029Card.SnapshotSectionId)
                {
                    bytes[payload] = 1; // PCI core must have internal base zero.
                    found = true;
                    break;
                }

                position = payload + length;
            }

            Assert.True(found);
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Builds an RTL-only machine without unrelated sound resources.</summary>
    private static HardwareConfiguration Configuration()
    {
        return new HardwareConfiguration
        {
            Network = { new CardSettings { Card = "rtl8029-pci", Port = 0x300 } },
        };
    }

    /// <summary>A wire that records ownership without opening host sockets.</summary>
    private sealed class DisposableBackend : INetworkBackend, IDisposable
    {
        public int Disposals { get; private set; }

        public void Send(ReadOnlySpan<byte> frame)
        {
        }

        public bool TryReceive(Span<byte> buffer, out int length)
        {
            length = 0;
            return false;
        }

        public void Dispose()
        {
            Disposals++;
        }
    }
}
