using System.Buffers.Binary;
using System.Text;
using Aurora86.Core;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Chipset;
using Aurora86.Core.Devices.Pci;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Firmware;

namespace Aurora86.Tests;

/// <summary>
/// Firmware and drivers enumerate the 1997 i430TX/PIIX4 motherboard through
/// PCI configuration space, while existing machines retain their i430FX IDs.
/// </summary>
public class ChipsetTests
{
    [Fact]
    public void DefaultMachineRetainsThePioOnlyPiixAndHasNoIntegratedUsb()
    {
        using var machine = new Machine();

        Assert.Equal(Chipset.I430Fx, machine.Chipset);
        Assert.Equal(0x122Du, machine.Pci.ReadConfiguration(0, 2, 2));
        Assert.Equal(0x122Eu, machine.Pci.ReadConfiguration(8, 2, 2));
        Assert.Equal(0x1230u, machine.Pci.ReadConfiguration(9, 2, 2));
        Assert.Equal(0x010100u, machine.Pci.ReadConfiguration(9, 8, 4) >> 8);
        Assert.Null(machine.BusMasterIde);
        Assert.Null(machine.Piix4Power);
        Assert.Null(machine.Usb);
    }

    [Fact]
    public void TxExposesAllFourPiix4FunctionsAndProductionHostRevision()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx);

        Assert.Equal(0x7100u, machine.Pci.ReadConfiguration(0, 2, 2));
        Assert.Equal(1u, machine.Pci.ReadConfiguration(0, 8, 1));
        for (ushort function = 0; function < 4; function++)
        {
            Assert.Equal(0x7110u + function,
                machine.Pci.ReadConfiguration((ushort)(8 + function), 2, 2));
        }

        Assert.Equal(0x010180u, machine.Pci.ReadConfiguration(9, 8, 4) >> 8);
        Assert.Equal(0x80u, machine.Pci.ReadConfiguration(8, 14, 1));
        Assert.Equal((ushort)0x000A, machine.Usb!.Function.Address);
        Assert.NotNull(machine.BusMasterIde);
    }

    [Fact]
    public void VmConfigurationBuildsTheSelectedChipsetAndRejectsUnknownNames()
    {
        var configuration = new VmConfiguration();
        configuration.Hardware.Chipset = "I430TX";
        using Machine machine = configuration.BuildMachine();

        Assert.Equal(Chipset.I430Tx, machine.Chipset);
        configuration.Hardware.Chipset = "not-a-chipset";
        Assert.Throws<InvalidDataException>(() => configuration.BuildMachine());
    }

    [Fact]
    public void RomRoutingTableNamesPiix4AndIncludesItsUsbPirqDWire()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx);
        byte[] expected = PciInterruptRouter.BuildRoutingTable(0x7110);
        byte[] actual = new byte[expected.Length];
        for (int index = 0; index < actual.Length; index++)
        {
            actual[index] = machine.Memory.ReadByte(
                BiosRom.Base + BiosRom.Entry.PciRoutingTable + (uint)index);
        }

        Assert.Equal(expected, actual);
        Assert.Equal(0, actual.Sum(value => value) & 0xFF);
        Assert.Equal(0x7110, BinaryPrimitives.ReadUInt16LittleEndian(actual.AsSpan(14)));
        Assert.Equal(3, PciInterruptRouter.Link(0x000A, 1));
        Assert.True(machine.Pci.InterruptRouter!.Route(0x000A, 1, 11));
        Assert.Equal(11u, machine.Pci.ReadConfiguration(8, 0x63, 1));
    }

    [Fact]
    public void IdeTimingBit15GatesEachLegacyCableIndependently()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx);

        machine.Pci.WriteConfiguration(9, 0x40, 2, 0);
        Assert.Equal(0xFF, machine.Io.ReadByte(0x1F7));
        Assert.NotEqual(0xFF, machine.Io.ReadByte(0x177));

        machine.Pci.WriteConfiguration(9, 0x40, 2, 0x8000);
        machine.Pci.WriteConfiguration(9, 0x42, 2, 0);
        Assert.NotEqual(0xFF, machine.Io.ReadByte(0x1F7));
        Assert.Equal(0xFF, machine.Io.ReadByte(0x177));
    }

    [Fact]
    public void PostMarksTheAttachedMasterDiskDmaCapableButNotThePioOnlySlave()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx);
        machine.AttachDisk(DiskImage.CreateInMemory(0x80, 20, 16, 63));
        machine.RunInstructions(200);

        Assert.Equal(0x20, machine.Io.ReadByte(0xC002) & 0x60);
        Assert.Equal(0, machine.Io.ReadByte(0xC00A) & 0x60);

        // These are BIOS defaults in ordinary R/W bits, not hardware straps.
        machine.Io.WriteByte(0xC002, 0);
        Assert.Equal(0, machine.Io.ReadByte(0xC002) & 0x60);
    }

    [Fact]
    public void SnapshotRestoresPmDecodeRegistersAndTimerAtTheCapturedCycle()
    {
        using var machine = new Machine(chipset: Chipset.I430Tx);
        machine.RunInstructions(200);
        machine.Pci.WriteConfiguration(0x0B, 0x80, 1, 1);
        machine.Io.WriteWord(0xB002, 0x0521); // PMEN's implemented event enables.
        uint capturedTimer = machine.Io.ReadDword(0xB008);
        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);

        machine.RunInstructions(2000);
        machine.Pci.WriteConfiguration(0x0B, 0x40, 4, 0xB401);
        snapshot.RestoreTo(machine);

        Assert.Equal(0x0521, machine.Io.ReadWord(0xB002));
        Assert.Equal(capturedTimer, machine.Io.ReadDword(0xB008));
        Assert.Equal(0xFF, machine.Io.ReadByte(0xB400));
    }

    [Fact]
    public void SnapshotFromAnotherChipsetIsRejectedBeforeRamChanges()
    {
        using var source = new Machine();
        using var target = new Machine(chipset: Chipset.I430Tx);
        target.Memory.WriteByte(0x1000, 0xA5);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);

        Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));
        Assert.Equal(0xA5, target.Memory.ReadByte(0x1000));
    }

    [Theory]
    [InlineData("BMID", 1)] // Reserved BMICOM bit 7.
    [InlineData("PIXP", 7)] // Bits above the PM timer's 24-bit counter.
    public void InvalidPiix4RegisterPayloadIsRejectedWhenTheFileIsLoaded(
        string sectionName, int invalidOffset)
    {
        using var machine = new Machine(chipset: Chipset.I430Tx);
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            using (var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true))
            {
                stream.Position = 8; // AURASNAP signature.
                bool found = false;
                while (stream.Position < stream.Length)
                {
                    string name = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    reader.ReadUInt16(); // Per-section format version.
                    int length = reader.ReadInt32();
                    if (name == sectionName)
                    {
                        stream.Position += invalidOffset;
                        stream.WriteByte(0xFF);
                        found = true;
                        break;
                    }

                    stream.Position += length;
                }

                Assert.True(found, $"Snapshot must contain {sectionName}.");
            }

            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
