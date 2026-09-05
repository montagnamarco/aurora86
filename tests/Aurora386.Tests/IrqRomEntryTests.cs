using Aurora386.Core;
using Xunit;

namespace Aurora386.Tests;

/// <summary>
/// The ROM's hardware interrupt entries finish with real instructions: the
/// timer calls the user hook at INT 1Ch and every entry acknowledges the
/// interrupt controller with OUT, so that a monitor trapping the ports sees
/// the end of interrupt.
/// </summary>
public class IrqRomEntryTests
{
    private static bool WaitForTick(Machine machine)
    {
        uint before = machine.Bda.TimerTicks;
        for (int i = 0; i < 20_000 && machine.Bda.TimerTicks == before; i++) machine.RunInstructions(1000);
        return machine.Bda.TimerTicks != before;
    }

    [Fact]
    public void ATimerTickCallsTheUserHookAndAcknowledgesTheController()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);

        // INT 1Ch -> 2000:0000: INC BYTE PTR CS:[0010] / IRET
        machine.Memory.WriteByte(0x20000, 0x2E);
        machine.Memory.WriteByte(0x20001, 0xFE);
        machine.Memory.WriteByte(0x20002, 0x06);
        machine.Memory.WriteWord(0x20003, 0x0010);
        machine.Memory.WriteByte(0x20005, 0xCF);
        machine.Memory.WriteByte(0x20010, 0);
        machine.Memory.WriteWord(0x1C * 4, 0x0000);
        machine.Memory.WriteWord(0x1C * 4 + 2, 0x2000);

        // Idle in a real-mode loop with interrupts on: JMP $ at 3000:0000.
        machine.Memory.WriteByte(0x30000, 0xEB);
        machine.Memory.WriteByte(0x30001, 0xFE);
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Cs, 0x3000);
        machine.Cpu.State.Eip = 0;
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Ss, 0x4000);
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;
        machine.Cpu.Flags.Interrupt = true;

        Assert.True(WaitForTick(machine), "no timer tick arrived");
        machine.RunInstructions(200);   // let the handler run to its IRET

        Assert.True(machine.Memory.ReadByte(0x20010) >= 1);
        Assert.False(machine.Interrupts.Master.HasInterruptInService);
        Assert.Equal(0x3000, machine.Cpu.State.Cs.Selector);   // back in the loop
    }

    [Fact]
    public void TheEndOfInterruptIsWrittenThroughThePort()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        var writes = new List<byte>();
        var pic = new PortSpy(machine, 0x20, v => writes.Add(v));
        machine.Io.Register(0x20, pic);

        machine.Memory.WriteByte(0x30000, 0xEB);
        machine.Memory.WriteByte(0x30001, 0xFE);
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Cs, 0x3000);
        machine.Cpu.State.Eip = 0;
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Ss, 0x4000);
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;
        machine.Cpu.Flags.Interrupt = true;

        Assert.True(WaitForTick(machine));
        machine.RunInstructions(200);
        Assert.Contains((byte)0x20, writes);
    }

    private sealed class PortSpy(Machine machine, ushort port, Action<byte> onWrite) : Aurora386.Core.IO.IIoDevice
    {
        private readonly Aurora386.Core.IO.IIoDevice _inner = machine.Interrupts.Master;
        public byte ReadByte(ushort p) => _inner.ReadByte(p);
        public void WriteByte(ushort p, byte value) { onWrite(value); _inner.WriteByte(p, value); }
    }
}
