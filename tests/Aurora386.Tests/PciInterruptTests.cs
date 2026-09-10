using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.IO;
using Aurora386.Core;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Firmware;

namespace Aurora386.Tests;

public class PciInterruptTests
{
    private static (InterruptController Pic, IoBus Io) Ready()
    {
        var pic = new InterruptController();
        var io = new IoBus();
        pic.Register(io);
        foreach (ushort port in new ushort[] { 0x20, 0xA0 })
        {
            io.WriteByte(port, 0x11);
            io.WriteByte((ushort)(port + 1), port == 0x20 ? (byte)0x20 : (byte)0x28);
            io.WriteByte((ushort)(port + 1), port == 0x20 ? (byte)4 : (byte)2);
            io.WriteByte((ushort)(port + 1), 1);
            io.WriteByte((ushort)(port + 1), 0);
        }
        return (pic, io);
    }

    [Fact]
    public void HigherPriorityRequestsPreemptButLowerPriorityWaitsForEoi()
    {
        var (pic, io) = Ready();
        pic.RaiseIrq(4);
        Assert.Equal(0x24, pic.GetPendingVector());
        pic.RaiseIrq(5);
        Assert.False(pic.HasPendingInterrupt);
        pic.RaiseIrq(1);
        Assert.Equal(0x21, pic.GetPendingVector());
        io.WriteByte(0x20, 0x20);
        Assert.Equal(-1, pic.GetPendingVector());
        io.WriteByte(0x20, 0x20);
        Assert.Equal(0x25, pic.GetPendingVector());
    }

    [Fact]
    public void ElcrKeepsTheAtFixedEdgeInputsReadOnly()
    {
        var (_, io) = Ready();
        Assert.Equal(0, io.ReadByte(0x4D0));
        Assert.Equal(0, io.ReadByte(0x4D1));
        io.WriteWord(0x4D0, 0xFFFF);
        Assert.Equal(0xDEF8, io.ReadWord(0x4D0));
    }

    [Fact]
    public void ALevelSlaveIrqRepeatsAfterBothEoisUntilTheDeviceReleasesIt()
    {
        var (pic, io) = Ready();
        io.WriteByte(0x4D1, 8); // IRQ11
        pic.RaiseIrq(11);
        Assert.Equal(0x2B, pic.GetPendingVector());
        Assert.False(pic.HasPendingInterrupt);
        io.WriteByte(0xA0, 0x20);
        Assert.False(pic.HasPendingInterrupt);
        io.WriteByte(0x20, 0x20);
        Assert.Equal(0x2B, pic.GetPendingVector());
        pic.LowerIrq(11);
        io.WriteByte(0xA0, 0x20);
        io.WriteByte(0x20, 0x20);
        Assert.False(pic.HasPendingInterrupt);
    }

    [Fact]
    public void AMaskedSlaveRequestDoesNotPreventALowerMasterRequest()
    {
        var (pic, io) = Ready();
        io.WriteByte(0xA1, 8);
        pic.RaiseIrq(11);
        pic.RaiseIrq(4);
        Assert.Equal(0x24, pic.GetPendingVector());
        io.WriteByte(0x20, 0x20);
        io.WriteByte(0xA1, 0);
        Assert.Equal(0x2B, pic.GetPendingVector());
    }
    [Fact]
    public void SharedPciSourcesAndLiveReroutingPreserveTheRemainingRequest()
    {
        var (pic, io) = Ready();
        var bridge = new PciFunction(8, 0x8086, 0x122E, 0x060100);
        var router = new PciInterruptRouter(pic, bridge);
        io.WriteByte(0x4D1, 0x0C); // IRQ10 and IRQ11 level
        router.SetInterrupt(0x10, 1, true);
        Assert.False(pic.HasPendingInterrupt); // disabled by default
        Assert.True(router.Route(0x10, 1, 11));
        Assert.True(router.Route(0x18, 1, 11));
        router.SetInterrupt(0x18, 1, true);
        Assert.Equal(0x2B, pic.GetPendingVector());
        router.SetInterrupt(0x10, 1, false);
        io.WriteByte(0xA0, 0x20); io.WriteByte(0x20, 0x20);
        Assert.Equal(0x2B, pic.GetPendingVector());
        Assert.True(router.Route(0x18, 1, 10));
        io.WriteByte(0xA0, 0x20); io.WriteByte(0x20, 0x20);
        Assert.Equal(0x2A, pic.GetPendingVector());
        router.SetInterrupt(0x18, 1, false);
        io.WriteByte(0xA0, 0x20); io.WriteByte(0x20, 0x20);
        Assert.False(pic.HasPendingInterrupt);
        Assert.False(router.Route(0x18, 1, 8));
    }

    [Fact]
    public void ReleasingPciDoesNotReleaseALegacyDeviceOnTheSameLevelInput()
    {
        var (pic, io) = Ready();
        io.WriteByte(0x4D1, 8);
        pic.RaiseIrq(11);
        pic.SetPciLevels(1 << 11);
        pic.SetPciLevels(0);
        Assert.Equal(0x2B, pic.GetPendingVector());
        pic.LowerIrq(11);
        io.WriteByte(0xA0, 0x20); io.WriteByte(0x20, 0x20);
        Assert.False(pic.HasPendingInterrupt);
    }

    [Fact]
    public void SnapshotRestoresLevelModeRouteAndAssertedDevice()
    {
        using var m = new Machine();
        m.RunInstructions(3000);
        m.Io.WriteByte(0x21, 0); m.Io.WriteByte(0xA1, 0);
        m.Io.WriteByte(0x4D1, 8);
        m.Pci.InterruptRouter!.Route(0x10, 1, 11);
        m.Pci.InterruptRouter.SetInterrupt(0x10, 1, true);
        var snapshot = MachineSnapshot.Capture(m);
        m.Pci.Reset(); m.Interrupts.ResetTriggerModes();
        snapshot.RestoreTo(m);
        Assert.Equal(8, m.Io.ReadByte(0x4D1));
        Assert.Equal(0x73, m.Interrupts.GetPendingVector());
        m.Io.WriteByte(0xA0, 0x20); m.Io.WriteByte(0x20, 0x20);
        Assert.Equal(0x73, m.Interrupts.GetPendingVector());
        m.Pci.InterruptRouter.SetInterrupt(0x10, 1, false);
        m.Io.WriteByte(0xA0, 0x20); m.Io.WriteByte(0x20, 0x20);
        Assert.False(m.Interrupts.HasPendingInterrupt);
    }

    [Fact]
    public void OlderPicSnapshotsDefaultToEdgeTriggered()
    {
        var (pic, io) = Ready();
        byte[] old = pic.Capture()[..16];
        io.WriteByte(0x4D1, 8);
        pic.Restore(old);
        Assert.Equal(0, io.ReadByte(0x4D1));
        pic.RaiseIrq(11);
        Assert.Equal(0x2B, pic.GetPendingVector());
        io.WriteByte(0xA0, 0x20); io.WriteByte(0x20, 0x20);
        Assert.False(pic.HasPendingInterrupt);
    }

    [Fact]
    public void PirTableMatchesTheRouterRegistersAndSlotWiring()
    {
        using var m = new Machine();
        uint table = BiosRom.Base + BiosRom.Entry.PciRoutingTable;
        Assert.Equal(0x52495024u, m.Memory.ReadDword(table));
        Assert.Equal(96, m.Memory.ReadWord(table + 6));
        Assert.Equal(8, m.Memory.ReadByte(table + 9));
        Assert.Equal(0x122E8086u, m.Memory.ReadDword(table + 12));
        int sum = 0;
        for (uint i = 0; i < 96; i++) sum += m.Memory.ReadByte(table + i);
        Assert.Equal(0, sum & 255);
        for (int slot = 0; slot < 4; slot++)
        for (byte pin = 1; pin <= 4; pin++)
        {
            uint entry = table + 32 + (uint)(slot * 16);
            ushort address = m.Memory.ReadByte(entry + 1);
            uint field = entry + 2 + (uint)((pin - 1) * 3);
            Assert.Equal(0x60 + PciInterruptRouter.Link(address, pin), m.Memory.ReadByte(field));
            Assert.Equal(PciInterruptRouter.AllowedIrqs, m.Memory.ReadWord(field + 1));
            m.Io.WriteDword(0xCF8, 0x80000860);
            m.Io.WriteByte((ushort)(0xCFC + m.Memory.ReadByte(field) - 0x60), 0xFF);
            Assert.Equal(0x8Fu, m.Pci.ReadConfiguration(8, m.Memory.ReadByte(field), 1));
        }
    }

}
