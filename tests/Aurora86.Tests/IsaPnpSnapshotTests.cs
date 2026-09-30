using System.Buffers.Binary;
using Aurora86.Core;
using Aurora86.Core.Devices.IsaPnp;
using Aurora86.Core.IO;

namespace Aurora86.Tests;

/// <summary>
/// Drives the ISA PnP 1.0a ports as a 1990s configuration driver does, then
/// verifies that raw snapshot restore resumes keys, isolation and resources.
/// </summary>
public sealed class IsaPnpSnapshotTests
{
    private const ushort AddressPort = IsaPnpBus.AddressPort;
    private const ushort WriteDataPort = IsaPnpBus.WriteDataPort;
    private const ushort ReadDataPort = IsaPnpBus.DefaultReadDataPort;
    private const ushort RelocatedReadDataPort = 0x20B;
    private static readonly byte[] InitiationKey = BuildKey();

    [Fact]
    public void InitiationKeyCursorResumesFromByte13OnFreshAndDirtyTargets()
    {
        Fixture source = CreateFixture();
        foreach (byte value in InitiationKey[..13])
        {
            source.Io.WriteByte(AddressPort, value);
        }

        byte[] saved = source.Bus.Capture();
        foreach (bool dirty in new[] { false, true })
        {
            Fixture target = CreateFixture();
            if (dirty)
            {
                Enumerate(target);
                Write(target.Io, 0x00, RelocatedReadDataPort >> 2);
            }

            target.Bus.Restore(saved);
            Assert.Equal(saved, target.Bus.Capture());
            Assert.Empty(target.Callbacks);
            foreach (byte value in InitiationKey[13..])
            {
                target.Io.WriteByte(AddressPort, value);
            }

            Assert.All(target.Bus.Cards, card => Assert.Equal(IsaPnpCard.CardState.Sleep, card.State));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(143)]
    public void SerialIsolationRegister01ResumesBothReadsOfEveryBit(int completedReads)
    {
        Fixture source = CreateFixture();
        SendKey(source.Io);
        Write(source.Io, 0x03, 0);
        source.Io.WriteByte(AddressPort, 0x01);
        for (int read = 0; read < completedReads; read++)
        {
            source.Io.ReadByte(ReadDataPort);
        }

        byte[] saved = source.Bus.Capture();
        Fixture target = CreateFixture();
        Enumerate(target);
        target.Bus.Restore(saved);
        for (int read = completedReads; read < 146; read++)
        {
            Assert.Equal(source.Io.ReadByte(ReadDataPort), target.Io.ReadByte(ReadDataPort));
        }

        Assert.Equal(source.Bus.Capture(), target.Bus.Capture());
        Write(source.Io, 0x06, 7);
        Write(target.Io, 0x06, 7);
        Assert.Equal(source.Bus.Capture(), target.Bus.Capture());
        Assert.Empty(target.Callbacks);
    }

    [Fact]
    public void ResourceRegister04CursorLogicalDevice07AndPendingRegistersSurviveRestore()
    {
        Fixture source = CreateFixture();
        Enumerate(source);
        IsaPnpCard selectedCard = source.Bus.Cards[0];
        Write(source.Io, 0x03, selectedCard.Csn);
        Write(source.Io, 0x07, 1);
        Write(source.Io, 0x30, 0);
        Write(source.Io, 0x60, 0x02);
        Write(source.Io, 0x61, 0x60);
        Write(source.Io, 0x70, 7);
        Write(source.Io, 0x74, 3);
        Write(source.Io, 0x75, 6);
        for (int read = 0; read < 17; read++)
        {
            Read(source.Io, 0x04);
        }

        Write(source.Io, 0x00, RelocatedReadDataPort >> 2);
        source.Io.WriteByte(AddressPort, 0x04);
        byte[] saved = source.Bus.Capture();
        IsaPnpBus.LogicalDeviceSnapshot savedDevice = IsaPnpBus.GetSnapshotLogicalDevices(saved)
            .Single(device => device.CardIndex == 0 && device.DeviceIndex == 1);
        Assert.False(savedDevice.Active);
        Assert.Equal(0x240, savedDevice.IoBase(0));
        Assert.Equal(0x60, savedDevice.Registers[0x61]);
        Assert.Equal(0x20B, IsaPnpBus.GetSnapshotReadDataPort(saved));

        Fixture target = CreateFixture();
        Enumerate(target);
        target.Bus.DetachSnapshotPortMapping();
        target.Bus.Restore(saved);
        Assert.Empty(target.Callbacks);
        Assert.Equal(0xFF, target.Io.ReadByte(ReadDataPort));
        for (int read = 0; read < 80; read++)
        {
            Assert.Equal(source.Io.ReadByte(RelocatedReadDataPort), target.Io.ReadByte(RelocatedReadDataPort));
        }

        Assert.Equal(1, Read(target.Io, 0x07, RelocatedReadDataPort));
        Assert.Equal(0, Read(target.Io, 0x30, RelocatedReadDataPort));
        Assert.Equal(0x60, Read(target.Io, 0x61, RelocatedReadDataPort));
        Assert.Equal(7, Read(target.Io, 0x70, RelocatedReadDataPort));
        Assert.Equal(3, Read(target.Io, 0x74, RelocatedReadDataPort));
        Assert.Equal(6, Read(target.Io, 0x75, RelocatedReadDataPort));
        Write(target.Io, 0x30, 1);
        Assert.Single(target.Callbacks);
        Assert.Equal((0x260, 7, 3, 6, true), target.Callbacks[0]);
    }

    [Fact]
    public void AttachedNe2000Registers60And61And70AreCapturedWithoutRelocationCallbacks()
    {
        using var source = new Machine();
        using var target = new Machine();
        Enumerate(source.IsaPlugAndPlay, source.Io);
        IsaPnpCard networkCard = source.IsaPlugAndPlay.Cards.Single(card =>
            card.LogicalDevices.Any(device => device.Id == "RTL8019"));
        Write(source.Io, 0x03, networkCard.Csn);
        Write(source.Io, 0x60, 0x03);
        Write(source.Io, 0x61, 0x20);
        Write(source.Io, 0x70, 10);
        byte[] saved = source.IsaPlugAndPlay.Capture();
        target.IsaPlugAndPlay.Restore(saved);

        Assert.Equal(saved, target.IsaPlugAndPlay.Capture());
        IsaPnpBus.LogicalDeviceSnapshot savedNetwork = IsaPnpBus.GetSnapshotLogicalDevices(saved)
            .Single(device => device.Id == "RTL8019");
        Assert.Equal(0x320, savedNetwork.IoBase(0));
        Assert.Equal(10, savedNetwork.Irq(0));
        // Standalone restore is raw: the enclosing machine owns chip relocation.
        Assert.Equal(0x300, target.Network.BasePort);
    }

    [Fact]
    public void ResetSnapshotStateRestoresConstructedDefaultsAndReadPortWithoutCallbacks()
    {
        Fixture fixture = CreateFixture();
        byte[] defaults = fixture.Bus.Capture();
        Enumerate(fixture);
        Write(fixture.Io, 0x03, fixture.Bus.Cards[0].Csn);
        Write(fixture.Io, 0x07, 1);
        Write(fixture.Io, 0x61, 0x60);
        Write(fixture.Io, 0x00, RelocatedReadDataPort >> 2);
        fixture.Callbacks.Clear();
        fixture.Bus.DetachSnapshotPortMapping();
        fixture.Bus.ResetSnapshotState();

        Assert.Equal(defaults, fixture.Bus.Capture());
        Assert.Empty(fixture.Callbacks);
        Assert.Equal(0xFF, fixture.Io.ReadByte(RelocatedReadDataPort));
        SendKey(fixture.Io);
        Write(fixture.Io, 0x03, 0);
        fixture.Io.WriteByte(AddressPort, 0x01);
        fixture.Io.ReadByte(ReadDataPort);
        fixture.Io.ReadByte(ReadDataPort);
        Assert.Equal(0x55, fixture.Io.ReadByte(ReadDataPort));
    }

    [Fact]
    public void SameReadDataPortIsReboundAfterSnapshotMappingDetach()
    {
        Fixture source = CreateFixture();
        Enumerate(source);
        Write(source.Io, 0x03, source.Bus.Cards[0].Csn);
        source.Io.WriteByte(AddressPort, 0x05);
        byte[] saved = source.Bus.Capture();
        Fixture target = CreateFixture();
        target.Bus.DetachSnapshotPortMapping();
        target.Bus.Restore(saved);
        Assert.Equal(1, target.Io.ReadByte(ReadDataPort));
    }

    [Fact]
    public void CaptureResetSnapshotPreflightsDefaultsWithoutChangingDirtyRegistersOrMapping()
    {
        Fixture fixture = CreateFixture();
        byte[] defaults = fixture.Bus.Capture();
        Enumerate(fixture);
        Write(fixture.Io, 0x03, fixture.Bus.Cards[0].Csn);
        Write(fixture.Io, 0x61, 0x60);
        Write(fixture.Io, 0x00, RelocatedReadDataPort >> 2);
        byte[] dirty = fixture.Bus.Capture();
        int callbackCount = fixture.Callbacks.Count;

        Assert.Equal(defaults, fixture.Bus.CaptureResetSnapshot());
        Assert.Equal(dirty, fixture.Bus.Capture());
        Assert.Equal(callbackCount, fixture.Callbacks.Count);
        Assert.Equal(RelocatedReadDataPort, fixture.Bus.ReadDataPort);
    }

    [Fact]
    public void InvalidHeaderCursorCardStateAndActivation30AreRejectedAtomically()
    {
        Fixture fixture = CreateFixture();
        Enumerate(fixture);
        Write(fixture.Io, 0x00, RelocatedReadDataPort >> 2);
        byte[] original = fixture.Bus.Capture();
        byte[] trailingData = [.. original, 0];
        var malformed = new List<byte[]> { original[..^1], trailingData };
        AddByteMutation(malformed, original, 0, 0);
        AddByteMutation(malformed, original, 4, 2);
        AddByteMutation(malformed, original, 8, 0);
        AddByteMutation(malformed, original, 10, 32); // Key cursor.
        AddByteMutation(malformed, original, 15, 2); // Isolation phase.
        AddByteMutation(malformed, original, 16, 73); // Isolation bit.
        AddByteMutation(malformed, original, 20, 2); // Strict Boolean.
        int firstCardResourceLength = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(30));
        int firstCardCsnOffset = 34 + firstCardResourceLength;
        AddByteMutation(malformed, original, firstCardCsnOffset + 1, 4); // Card state.
        AddByteMutation(malformed, original, firstCardCsnOffset + 2, 73); // Serial bit.
        AddByteMutation(malformed, original, firstCardCsnOffset + 10, 2); // LDN, only 0/1 exist.
        int firstDeviceRegistersOffset = firstCardCsnOffset + 16 + 7 + 1;
        AddByteMutation(malformed, original, firstDeviceRegistersOffset + 0x30, 2);
        AddByteMutation(malformed, original, firstDeviceRegistersOffset + 0x80 + 0x30, 2);
        // A bad latch in the last attached card must not install earlier cards.
        AddByteMutation(malformed, original, original.Length - 0x80 + 0x30, 2);
        foreach (byte[] data in malformed)
        {
            Assert.Throws<InvalidDataException>(() => IsaPnpBus.ValidateSnapshot(data));
            Assert.Throws<InvalidDataException>(() => fixture.Bus.Restore(data));
            Assert.Equal(original, fixture.Bus.Capture());
            Assert.Empty(fixture.Callbacks);
        }
    }

    [Fact]
    public void MismatchedCardIdentityAndLogicalDeviceTopologyAreRejectedAtomically()
    {
        Fixture source = CreateFixture();
        Enumerate(source);
        byte[] saved = source.Bus.Capture();
        foreach (Fixture target in new[] { CreateFixture(serialNumber: 9), CreateFixture(includeSecondDevice: false) })
        {
            byte[] original = target.Bus.Capture();
            Assert.Throws<InvalidDataException>(() => target.Bus.ValidateSnapshotForRestore(saved));
            Assert.Throws<InvalidDataException>(() => target.Bus.Restore(saved));
            Assert.Equal(original, target.Bus.Capture());
            Assert.Empty(target.Callbacks);
        }
    }

    private static void AddByteMutation(List<byte[]> malformed, byte[] original, int offset, byte value)
    {
        byte[] changed = (byte[])original.Clone();
        changed[offset] = value;
        malformed.Add(changed);
    }

    private static Fixture CreateFixture(uint serialNumber = 1, bool includeSecondDevice = true)
    {
        var callbacks = new List<(ushort Base, int Irq, int Dma8, int Dma16, bool Active)>();
        var first = new IsaPnpLogicalDevice("TSTA001", null, writer => writer.Io(0x220, 0x280, 0x20, 16));
        first.SetDefaults(true, [0x220], [5], [1, 5]);
        var second = new IsaPnpLogicalDevice("TSTA002", null, writer => writer.Io(0x240, 0x280, 0x20, 16));
        second.SetDefaults(true, [0x240], [5], [1, 5]);
        first.Configured = device => callbacks.Add((device.IoBase(0), device.Irq(0), device.Dma(0), device.Dma(1), device.Active));
        second.Configured = first.Configured;
        var network = new IsaPnpLogicalDevice("RTL8019", null, writer => writer.Io(0x300, 0x360, 0x20, 32));
        network.SetDefaults(true, [0x300], [10], []);
        var bus = new IsaPnpBus();
        bus.Add(new IsaPnpCard("TST0001", serialNumber, "Audio", includeSecondDevice ? [first, second] : [first]));
        bus.Add(new IsaPnpCard("RTL8019", 2, "Network", [network]));
        var io = new IoBus();
        bus.Register(io);
        return new Fixture(io, bus, callbacks);
    }

    private static void Enumerate(Fixture fixture)
    {
        Enumerate(fixture.Bus, fixture.Io);
    }

    private static void Enumerate(IsaPnpBus bus, IoBus io)
    {
        SendKey(io);
        for (byte csn = 1; csn <= bus.Cards.Count; csn++)
        {
            Write(io, 0x03, 0);
            io.WriteByte(AddressPort, 0x01);
            for (int read = 0; read < 144; read++)
            {
                io.ReadByte(ReadDataPort);
            }

            Write(io, 0x06, csn);
        }
    }

    private static void SendKey(IoBus io)
    {
        foreach (byte value in InitiationKey)
        {
            io.WriteByte(AddressPort, value);
        }
    }

    private static void Write(IoBus io, byte register, int value)
    {
        io.WriteByte(AddressPort, register);
        io.WriteByte(WriteDataPort, (byte)value);
    }

    private static byte Read(IoBus io, byte register, ushort port = ReadDataPort)
    {
        io.WriteByte(AddressPort, register);
        return io.ReadByte(port);
    }

    private static byte[] BuildKey()
    {
        byte[] key = new byte[32];
        byte shiftRegister = 0x6A;
        for (int index = 0; index < key.Length; index++)
        {
            key[index] = shiftRegister;
            shiftRegister = (byte)((shiftRegister >> 1)
                | (((shiftRegister ^ (shiftRegister >> 1)) & 1) << 7));
        }

        return key;
    }

    /// <summary>Independent PnP cards and callback observations for port replay.</summary>
    private sealed record Fixture(IoBus Io, IsaPnpBus Bus,
        List<(ushort Base, int Irq, int Dma8, int Dma16, bool Active)> Callbacks);
}
