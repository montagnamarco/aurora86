using System.IO;

using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.Parallel;
using Aurora86.Core.IO;

namespace Aurora86.Tests;

/// <summary>
/// LPT1, the Standard Parallel Port at 378h-37Ah, IRQ 7
/// (docs/PARALLEL-PORT.md section 1).
/// </summary>
public class ParallelPortTests
{
    /// <summary>Records what the port delivered, without any print-job logic of its own.</summary>
    private sealed class RecordingPrinter : IParallelPrinter
    {
        public readonly List<byte> Received = [];
        public int InitializeCalls;

        public void Receive(byte value) => Received.Add(value);
        public void Initialize() => InitializeCalls++;
    }

    private sealed class Rig
    {
        public InterruptController Interrupts { get; } = new();
        public IoBus Io { get; } = new();
        public RecordingPrinter Printer { get; } = new();
        public ParallelPort Port { get; }

        public Rig()
        {
            Port = new ParallelPort(Interrupts, Printer);
            Port.Register(Io);
            Interrupts.Register(Io);

            // Programs the 8259 the way SerialMouseTests does: vector base
            // 08h, so IRQ 7's vector is 0Fh, and every line unmasked.
            Io.WriteByte(0x20, 0x11); Io.WriteByte(0x21, 0x08);
            Io.WriteByte(0x21, 0x04); Io.WriteByte(0x21, 0x01);
            Io.WriteByte(0x21, 0x00);
        }

        public byte ReadData() => Io.ReadByte(ParallelPort.DataPort);
        public void WriteData(byte value) => Io.WriteByte(ParallelPort.DataPort, value);
        public byte ReadStatus() => Io.ReadByte(ParallelPort.StatusPort);
        public byte ReadControl() => Io.ReadByte(ParallelPort.ControlPort);
        public void WriteControl(byte value) => Io.WriteByte(ParallelPort.ControlPort, value);

        /// <summary>The vector IRQ 7 answers with, once ICW2 sets the base to 08h.</summary>
        public const byte Irq7Vector = 0x0F;
    }

    [Fact]
    public void AnIdleReadyPrinterReadsStatusDFh()
    {
        var rig = new Rig();

        Assert.Equal(0xDF, rig.ReadStatus());
    }

    [Fact]
    public void DataReadsBackTheLastByteWritten()
    {
        var rig = new Rig();

        rig.WriteData(0x41);

        Assert.Equal(0x41, rig.ReadData());
    }

    [Fact]
    public void TheByteIsTakenOnlyWhenStrobeFalls()
    {
        var rig = new Rig();
        rig.WriteData(0x41);

        // Raising STROBE alone does not deliver anything: the printer takes
        // the byte on the falling edge, not the rising one.
        rig.WriteControl(ParallelPort.ControlStrobe);
        Assert.Empty(rig.Printer.Received);

        // Writing the same value again (STROBE still high) is not an edge either.
        rig.WriteControl(ParallelPort.ControlStrobe);
        Assert.Empty(rig.Printer.Received);

        rig.WriteControl(0x00);
        Assert.Equal(new byte[] { 0x41 }, rig.Printer.Received);
    }

    [Fact]
    public void Irq7FiresOnlyWhenControlBit4IsSet()
    {
        var rig = new Rig();
        rig.WriteData(0x58);

        // STROBE falls with the IRQ enable bit clear: no interrupt.
        rig.WriteControl(ParallelPort.ControlStrobe);
        rig.WriteControl(0x00);
        Assert.Equal(new byte[] { 0x58 }, rig.Printer.Received);
        Assert.Equal(-1, rig.Interrupts.Master.GetPendingVector());

        // STROBE falls again with the IRQ enable bit set this time.
        rig.WriteControl((byte)(ParallelPort.ControlStrobe | ParallelPort.ControlIrqEnable));
        rig.WriteControl(ParallelPort.ControlIrqEnable);
        Assert.Equal(Rig.Irq7Vector, rig.Interrupts.Master.GetPendingVector());
    }

    [Fact]
    public void InitFallingCallsPrinterInitialize()
    {
        var rig = new Rig();

        // /INIT starts high (not asserting reset); pulse it low.
        rig.WriteControl(0x00);
        Assert.Equal(1, rig.Printer.InitializeCalls);

        // Writing the same low value again is not a fresh edge.
        rig.WriteControl(0x00);
        Assert.Equal(1, rig.Printer.InitializeCalls);
    }

    [Fact]
    public void ControlReadBackReportsTheWrittenBitsAndOnesAbove()
    {
        var rig = new Rig();

        rig.WriteControl(ParallelPort.ControlAutoFeed | ParallelPort.ControlSelectIn);

        // Bits 5-7 always read 1 (*choice*: no bidirectional mode); the
        // bits actually written read back exactly as written.
        Assert.Equal(0xE0 | ParallelPort.ControlAutoFeed | ParallelPort.ControlSelectIn, rig.ReadControl());
    }

    [Fact]
    public void SnapshotRoundTripsDataAndControl()
    {
        var rig = new Rig();
        rig.WriteData(0x7A);
        rig.WriteControl(ParallelPort.ControlAutoFeed);

        byte[] snapshot = rig.Port.Capture();
        Assert.Equal(ParallelPort.SnapshotLength, snapshot.Length);

        var restored = new Rig();
        restored.Port.Restore(snapshot);

        Assert.Equal(rig.ReadData(), restored.ReadData());
        Assert.Equal(rig.ReadControl(), restored.ReadControl());

        // Restoring must not call the printer or raise IRQ 7, even though
        // the captured CONTROL still has the STROBE and /INIT bits it was
        // written with: docs/PARALLEL-PORT.md section 4 says a job in
        // progress is not saved and restoring closes nothing.
        Assert.Empty(restored.Printer.Received);
        Assert.Equal(0, restored.Printer.InitializeCalls);
        Assert.Equal(-1, restored.Interrupts.Master.GetPendingVector());
    }

    [Fact]
    public void RestoreRejectsAWrongLengthSnapshot()
    {
        var rig = new Rig();

        Assert.Throws<InvalidDataException>(() => rig.Port.Restore(new byte[] { 0x01, 0x02, 0x03 }));
        Assert.Throws<InvalidDataException>(() => ParallelPort.ValidateSnapshot(new byte[] { 0x01 }));
    }
}
