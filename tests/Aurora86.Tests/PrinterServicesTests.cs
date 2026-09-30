using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.Parallel;
using Aurora86.Core.Firmware;
using Aurora86.Core.Memory;
using CpuNs = Aurora86.Core.Cpu;

namespace Aurora86.Tests;

/// <summary>
/// INT 17h, the BIOS printer service (docs/PARALLEL-PORT.md section 2),
/// driven directly through a <see cref="CpuState"/> the way DOS's PRINT.COM
/// and a program's print routine reach it. A machine-level test that goes
/// through the real INT instruction belongs with the owner's BIOS tests.
/// </summary>
public class PrinterServicesTests
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
        public RecordingPrinter Printer { get; } = new();
        public ParallelPort Port { get; }
        public PrinterServices Services { get; }
        public CpuNs.Cpu Cpu { get; }

        /// <summary>The vector IRQ 7 answers with, once ICW2 sets the base to 08h.</summary>
        public const byte Irq7Vector = 0x0F;

        public Rig()
        {
            Port = new ParallelPort(Interrupts, Printer);
            Services = new PrinterServices(Port);
            Cpu = new CpuNs.Cpu(new PhysicalMemoryBus());

            // Programs the master 8259 the way SerialMouseTests and
            // ParallelPortTests do: vector base 08h, so IRQ 7's vector is
            // 0Fh, and every line unmasked -- needed so GetPendingVector()
            // can actually report a pending IRQ 7 rather than reading it as
            // masked out by the chip's uninitialised, all-ones default.
            Interrupts.Master.WriteByte(0x20, 0x11);
            Interrupts.Master.WriteByte(0x21, 0x08);
            Interrupts.Master.WriteByte(0x21, 0x04);
            Interrupts.Master.WriteByte(0x21, 0x01);
            Interrupts.Master.WriteByte(0x21, 0x00);
        }

        /// <summary>Sets AH/AL/DX and calls the handler, returning what it left in AH.</summary>
        public byte Call(byte ah, byte al = 0, ushort dx = 0)
        {
            Cpu.State.SetReg8(4, ah);
            Cpu.State.SetReg8(0, al);
            Cpu.State.Dx = dx;
            Services.Handle(Cpu);
            return Cpu.State.GetReg8(4);
        }

        public byte ReadControl() => Port.ReadByte(ParallelPort.ControlPort);
    }

    [Fact]
    public void Ah02ReadStatusAnswers90hOnAReadyPrinter()
    {
        var rig = new Rig();

        Assert.Equal(0x90, rig.Call(ah: 0x02));
        Assert.Empty(rig.Printer.Received);
        Assert.Equal(0, rig.Printer.InitializeCalls);
    }

    [Fact]
    public void Ah00DeliversAlToThePrinterAndAnswers90h()
    {
        var rig = new Rig();

        Assert.Equal(0x90, rig.Call(ah: 0x00, al: 0x59));

        Assert.Equal(new byte[] { 0x59 }, rig.Printer.Received);

        // The IBM BIOS listing leaves CONTROL at 0Ch (STROBE cleared,
        // /INIT and SELECT IN both set) once the call returns; bits 5-7
        // read back as 1 (ParallelPort's *choice*, no bidirectional mode).
        Assert.Equal(0xE0 | 0x0C, rig.ReadControl());
    }

    [Fact]
    public void Ah00NeverSetsControlBit4EvenIfADriverHadEnabledTheIrq()
    {
        var rig = new Rig();

        // A driver enabled LPT1's interrupt before handing off to the BIOS
        // -- CONTROL = 1Ch: IRQ enable set, /INIT and SELECT IN set,
        // STROBE clear.
        rig.Port.WriteByte(ParallelPort.ControlPort, 0x1C);

        rig.Call(ah: 0x00, al: 0x41);

        // The BIOS's own fixed CONTROL values (0Dh then 0Ch) never carry
        // the IRQ enable bit, so the STROBE-falling edge they cause never
        // reaches the PIC, no matter what the driver had set beforehand.
        Assert.Equal(-1, rig.Interrupts.Master.GetPendingVector());
        Assert.Equal(0xE0 | 0x0C, rig.ReadControl());
    }

    [Fact]
    public void Ah01InitializesThePrinter()
    {
        var rig = new Rig();

        Assert.Equal(0x90, rig.Call(ah: 0x01));

        Assert.Equal(1, rig.Printer.InitializeCalls);

        // The BIOS listing leaves CONTROL at 0Ch once /INIT's pulse (08h,
        // then 0Ch) is done.
        Assert.Equal(0xE0 | 0x0C, rig.ReadControl());
    }

    [Fact]
    public void DxOneAnswersATimeOutWithoutTouchingThePort()
    {
        var rig = new Rig();

        Assert.Equal(0x01, rig.Call(ah: 0x00, al: 0x41, dx: 1));

        // Printer 0 is the only one fitted: a call naming printer 1 never
        // reaches the port at all, so nothing was delivered.
        Assert.Empty(rig.Printer.Received);
    }
}
