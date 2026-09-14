using System.Buffers.Binary;

using Aurora386.Core;
using Aurora386.Core.Devices.Input;

namespace Aurora386.Tests;

/// <summary>
/// Serial mouse state in machine snapshots.
///
/// A UART can have a packet half read when a snapshot is taken. The remaining
/// bytes, its IRQ line, and movement not yet made into a packet must agree
/// after a restore to a fresh machine.
/// </summary>
public sealed class SerialMouseSnapshotTests
{
    private const ushort Com1 = SerialMouse.BasePort;
    private const int TrioVideoTailLength = sizeof(int) + 12;

    private static Machine ConfiguredMachine()
    {
        var machine = new Machine();
        machine.Mouse.ConnectedInterface = MouseInterface.Serial;

        Write(machine, 1, 0x01);
        Write(machine, 3, 0x82);
        Write(machine, 0, 0x34);
        Write(machine, 1, 0x12);
        Write(machine, 3, 0x02);
        Write(machine, 4, 0x0B);
        Write(machine, 7, 0xA5);

        Assert.Equal((byte)'M', Read(machine, 0));
        return machine;
    }

    private static byte Read(Machine machine, ushort register)
    {
        return machine.Io.ReadByte((ushort)(Com1 + register));
    }

    private static void Write(Machine machine, ushort register, byte value)
    {
        machine.Io.WriteByte((ushort)(Com1 + register), value);
    }

    private static void ReadPacket(Machine machine, List<byte> bytes)
    {
        machine.SerialMouse.Poll();

        while ((Read(machine, 5) & 0x01) != 0)
        {
            bytes.Add(Read(machine, 0));
        }
    }

    [Fact]
    public void AConfiguredUartSurvivesAnInMemorySnapshot()
    {
        using Machine source = ConfiguredMachine();
        Write(source, 3, 0x82);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        using Machine target = new Machine();

        snapshot.RestoreTo(target);

        Assert.True(target.SerialMouse.PoweredOn);
        Assert.Equal(0x82, Read(target, 3));
        Assert.Equal(0x34, Read(target, 0));
        Assert.Equal(0x12, Read(target, 1));
        Write(target, 3, 0x02);
        Assert.Equal(0x01, Read(target, 1));
        Assert.Equal(0x0B, Read(target, 4));
        Assert.Equal(0xA5, Read(target, 7));
        Assert.Equal(0x01, Read(target, 2));
    }

    [Fact]
    public void APartlyReadMousePacketAndItsPendingIrqSurviveAFileSnapshot()
    {
        using Machine source = ConfiguredMachine();
        source.Mouse.SetButton(MouseButtons.Left, true);
        source.Mouse.Move(10, -4);
        source.SerialMouse.Poll();

        Assert.Equal(0x6C, Read(source, 0));
        byte[] interruptState = source.Interrupts.Capture();
        string path = Path.GetTempFileName();

        try
        {
            MachineSnapshot.Capture(source).Save(path);
            using Machine target = new Machine();
            MachineSnapshot.Load(path).RestoreTo(target);

            Assert.Equal(interruptState, target.Interrupts.Capture());
            Assert.Equal(0x04, Read(target, 2));
            Assert.Equal(0x61, Read(target, 5));
            Assert.Equal(0x0A, Read(target, 0));
            Assert.Equal(0x3C, Read(target, 0));
            Assert.Equal(0x01, Read(target, 2));
            Assert.Equal(0x60, Read(target, 5));

            Assert.Equal(0x0A, Read(source, 0));
            Assert.Equal(0x3C, Read(source, 0));
            Assert.Equal(source.Interrupts.Capture(), target.Interrupts.Capture());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PendingMotionAndHeldButtonsProduceTheSameFuturePacketsAfterRestore()
    {
        using Machine source = ConfiguredMachine();
        source.Mouse.SetButton(MouseButtons.Left, true);
        source.Mouse.Move(10, -4);

        var ignoredPacket = new List<byte>();
        ReadPacket(source, ignoredPacket);

        source.Mouse.Move(300, 200);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        using Machine target = new Machine();
        snapshot.RestoreTo(target);

        var sourceBytes = new List<byte>();
        var targetBytes = new List<byte>();
        ReadPacket(source, sourceBytes);
        ReadPacket(source, sourceBytes);
        ReadPacket(source, sourceBytes);
        ReadPacket(target, targetBytes);
        ReadPacket(target, targetBytes);
        ReadPacket(target, targetBytes);

        Assert.Equal(sourceBytes, targetBytes);
        Assert.Equal(9, targetBytes.Count);
        Assert.Equal(0x01, Read(target, 2));

        source.Mouse.SetButton(MouseButtons.Left, false);
        target.Mouse.SetButton(MouseButtons.Left, false);
        source.Mouse.Move(-12, 5);
        target.Mouse.Move(-12, 5);
        ReadPacket(source, sourceBytes);
        ReadPacket(target, targetBytes);

        Assert.Equal(sourceBytes, targetBytes);
    }

    [Fact]
    public void RestoringAReportedButtonDoesNotRaiseMouseEvents()
    {
        using Machine source = ConfiguredMachine();
        source.Mouse.SetButton(MouseButtons.Left, true);

        var packet = new List<byte>();
        ReadPacket(source, packet);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        using Machine target = new Machine();
        int events = 0;
        target.Mouse.Moved += () => events++;

        snapshot.RestoreTo(target);
        target.SerialMouse.Poll();

        Assert.Equal(0, events);
        Assert.Equal(0x01, Read(target, 2));
    }

    [Fact]
    public void AnOlderSnapshotWithoutSerialStateLeavesTheConfiguredUartUnchanged()
    {
        using Machine source = ConfiguredMachine();
        string legacyPath = Path.GetTempFileName();
        string resavedPath = Path.GetTempFileName();

        try
        {
            MachineSnapshot.Capture(source).Save(legacyPath);
            int serialStateLength = sizeof(int) + source.SerialMouse.Capture().Length;
            using (FileStream file = File.OpenWrite(legacyPath))
            {
                RemoveTrioVideoTail(file);
                file.SetLength(file.Length - serialStateLength);
            }

            MachineSnapshot legacySnapshot = MachineSnapshot.Load(legacyPath);
            legacySnapshot.Save(resavedPath);
            Assert.Equal(new FileInfo(legacyPath).Length, new FileInfo(resavedPath).Length);

            using Machine target = ConfiguredMachine();
            Write(target, 3, 0x83);
            Write(target, 0, 0x78);
            Write(target, 1, 0x56);
            Write(target, 3, 0x03);

            MachineSnapshot.Load(resavedPath).RestoreTo(target);

            Assert.Equal(0x03, Read(target, 3));
            Write(target, 3, 0x83);
            Assert.Equal(0x78, Read(target, 0));
            Assert.Equal(0x56, Read(target, 1));
        }
        finally
        {
            File.Delete(legacyPath);
            File.Delete(resavedPath);
        }
    }

    [Fact]
    public void ATruncatedSerialStateIsRejected()
    {
        using Machine machine = ConfiguredMachine();
        string path = Path.GetTempFileName();

        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            using (FileStream file = File.OpenWrite(path))
            {
                RemoveTrioVideoTail(file);
                file.SetLength(file.Length - 1);
            }

            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnUnknownSerialStateVersionIsRejected()
    {
        using Machine machine = ConfiguredMachine();
        string path = Path.GetTempFileName();

        try
        {
            byte[] serialState = machine.SerialMouse.Capture();
            MachineSnapshot.Capture(machine).Save(path);
            using (FileStream file = File.OpenWrite(path))
            {
                RemoveTrioVideoTail(file);
                file.Position = file.Length - serialState.Length;
                file.WriteByte(2);
            }

            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnInvalidDirectSerialRestoreLeavesTheTargetUnchanged()
    {
        using Machine target = ConfiguredMachine();
        byte[] before = target.SerialMouse.Capture();
        byte[] interruptState = target.Interrupts.Capture();
        byte[] invalid = (byte[])before.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(17), -1);
        int events = 0;
        target.Mouse.Moved += () => events++;

        Assert.Throws<InvalidDataException>(() => target.SerialMouse.Restore(invalid));

        Assert.Equal(before, target.SerialMouse.Capture());
        Assert.Equal(interruptState, target.Interrupts.Capture());
        Assert.Equal(0, events);
    }

    private static void RemoveTrioVideoTail(FileStream file)
    {
        file.SetLength(file.Length - TrioVideoTailLength);
    }
}
