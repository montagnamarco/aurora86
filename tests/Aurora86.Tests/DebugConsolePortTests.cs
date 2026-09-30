using System.Text;

using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.System;
using Aurora86.Core.IO;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// The debug console port, 0xE9.
///
/// Every assertion here names that port number, because the whole value of
/// the device is that it sits at the address the Bochs custom established:
/// a guest built for another emulator writes to 0xE9 and nowhere else, so a
/// device that worked perfectly at any other address would be useless.
/// </summary>
public class DebugConsolePortTests
{
    private const uint CodeBase = 0x1000;

    [Fact]
    public void BytesWrittenToPortE9ReachTheHost()
    {
        var port = new DebugConsolePort();
        var io = new IoBus();
        port.Register(io);

        foreach (char character in "hi\n") io.WriteByte(0xE9, (byte)character);

        Assert.Equal("hi\n", port.Text.ToString());
        Assert.Equal(3, port.BytesWritten);
    }

    [Fact]
    public void ReadingPortE9ReturnsTheSignatureRatherThanTheIdleBus()
    {
        var port = new DebugConsolePort();
        var io = new IoBus();
        port.Register(io);

        // 0xE9, not 0xFF: this is exactly how a guest tells an emulator that
        // forwards these bytes from one where the port is unassigned.
        Assert.Equal(0xE9, io.ReadByte(0xE9));
        Assert.Equal(DebugConsolePort.Signature, io.ReadByte(DebugConsolePort.Port));
    }

    [Fact]
    public void AnUnattachedPortE9StillReadsAsTheIdleBus()
    {
        // The counterpart of the test above, and the reason the signature is
        // worth anything: without the device the same read gives 0xFF.
        var io = new IoBus();

        Assert.Equal(0xFF, io.ReadByte(0xE9));
    }

    [Fact]
    public void EveryByteValueSurvivesTheTripUnchanged()
    {
        var port = new DebugConsolePort();
        var io = new IoBus();
        port.Register(io);

        for (int value = 0; value < 256; value++) io.WriteByte(0xE9, (byte)value);

        // A guest writing UTF-8, or raw binary, must come back byte for byte.
        string text = port.Text.ToString();
        Assert.Equal(256, text.Length);
        for (int value = 0; value < 256; value++) Assert.Equal(value, text[value]);
    }

    [Fact]
    public void TextIsMirroredToTheDestinationWriter()
    {
        var destination = new StringWriter();
        var port = new DebugConsolePort(destination);
        var io = new IoBus();
        port.Register(io);

        foreach (char character in "logged\n") io.WriteByte(0xE9, (byte)character);

        Assert.Equal("logged\n", destination.ToString());
    }

    [Fact]
    public void EachByteRaisesTheEvent()
    {
        var port = new DebugConsolePort();
        var io = new IoBus();
        port.Register(io);

        var seen = new StringBuilder();
        port.ByteWritten += value => seen.Append((char)value);

        foreach (char character in "abc") io.WriteByte(0xE9, (byte)character);

        Assert.Equal("abc", seen.ToString());
    }

    [Fact]
    public void AGuestsOutInstructionReachesThePort()
    {
        // The end-to-end case: a real CPU executing a real OUT 0xE9, AL.
        // The tests above drive the bus directly, which would not catch the
        // instruction decoding the port number differently.
        var port = new DebugConsolePort();
        var io = new IoBus();
        port.Register(io);

        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus, io);
        foreach (SegReg segment in Enum.GetValues<SegReg>())
            cpu.State.LoadSegmentRealMode(segment, 0);
        cpu.State.Eip = CodeBase;

        // MOV AL,'K' ; OUT 0xE9,AL
        new byte[] { 0xB0, (byte)'K', 0xE6, 0xE9 }
            .CopyTo(bus.GetRamSpan(CodeBase, 4));

        cpu.Step();   // MOV
        cpu.Step();   // OUT

        Assert.Equal("K", port.Text.ToString());
    }

    [Fact]
    public void AGuestsInInstructionReadsTheSignature()
    {
        var port = new DebugConsolePort();
        var io = new IoBus();
        port.Register(io);

        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus, io);
        foreach (SegReg segment in Enum.GetValues<SegReg>())
            cpu.State.LoadSegmentRealMode(segment, 0);
        cpu.State.Eip = CodeBase;
        cpu.State.Eax = 0x00000000;

        // IN AL, 0xE9 -- how a guest asks whether anybody is listening.
        new byte[] { 0xE4, 0xE9 }.CopyTo(bus.GetRamSpan(CodeBase, 2));

        cpu.Step();

        Assert.Equal(0xE9u, cpu.State.Eax & 0xFF);
    }
}
