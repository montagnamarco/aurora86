using Aurora86.Core.Cpu;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// Behaviour the SingleStepTests 80386 real-mode vectors -- captures from a
/// real 386EX -- showed the emulator getting wrong, stated here in the words
/// of the instruction set reference so that it cannot come back.
/// </summary>
public class RealModeVectorFindingsTests
{
    private const uint CodeBase = 0x1000;

    private static Cpu Run(int steps, params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);

        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealMode(segment, 0);
        }

        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));

        for (int i = 0; i < steps; i++)
        {
            cpu.Step();
        }

        return cpu;
    }

    /// <summary>
    /// SAHF loads SF, ZF, AF, PF and CF from AH; bits 3 and 5 of EFLAGS stay
    /// 0 and bit 1 stays 1 whatever AH holds. The 386EX turns AH = FE into a
    /// low flags byte of D6.
    /// </summary>
    [Fact]
    public void SahfLoadsOnlyTheFiveFlagsFromAh()
    {
        // MOV AH,FE ; SAHF
        Cpu cpu = Run(2, 0xB4, 0xFE, 0x9E);

        Assert.Equal(0xD6u, cpu.Flags.Value & 0xFF);
    }

    /// <summary>
    /// SHLD by one sets OF when the sign bit changes. SHLD CX,BP,1 with
    /// CX = BB7A and BP = 4000 shifts in a zero and gives 76F4: the sign went
    /// from 1 to 0, so OF = 1, and the bit shifted out sets CF.
    /// </summary>
    [Fact]
    public void ShldByOneSetsOverflowOnASignChange()
    {
        // MOV CX,BB7A ; MOV BP,4000 ; SHLD CX,BP,1
        Cpu cpu = Run(3, 0xB9, 0x7A, 0xBB, 0xBD, 0x00, 0x40, 0x0F, 0xA4, 0xE9, 0x01);

        Assert.Equal(0x76F4, cpu.State.Cx);
        Assert.True(cpu.Flags.Overflow);
        Assert.True(cpu.Flags.Carry);
    }

    /// <summary>
    /// LOCK CMPXCHG and LOCK XADD on memory are legal on a 486: they are the
    /// instructions a spinlock and a reference count are built from. With
    /// [0x3000] = 5 and EAX = 5, LOCK CMPXCHG [0x3000],CX stores CX; then
    /// LOCK XADD [0x3000],BX adds BX and returns the old value in BX.
    /// </summary>
    [Fact]
    public void LockedCmpxchgAndXaddOnMemoryRunInsteadOfFaulting()
    {
        // MOV AX,5 ; MOV CX,9 ; MOV BX,2 ; LOCK CMPXCHG [3000],CX ; LOCK XADD [3000],BX
        byte[] program =
        [
            0xB8, 0x05, 0x00,
            0xB9, 0x09, 0x00,
            0xBB, 0x02, 0x00,
            0xF0, 0x0F, 0xB1, 0x0E, 0x00, 0x30,
            0xF0, 0x0F, 0xC1, 0x1E, 0x00, 0x30,
        ];

        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);

        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealMode(segment, 0);
        }

        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        program.CopyTo(bus.GetRamSpan(CodeBase, program.Length));
        bus.WriteWord(0x3000, 5);

        byte vector = 0xFF;
        cpu.ProcessorException += (number, _, _, _) => vector = number;

        for (int i = 0; i < 5; i++)
        {
            cpu.Step();
        }

        Assert.Equal(0xFF, vector);
        Assert.Equal(11, bus.ReadWord(0x3000));
        Assert.Equal(9, cpu.State.Bx);
    }

    /// <summary>
    /// SHRD by one likewise: CX = BB7A shifted right with a zero from BP gives
    /// 5DBD, the sign changed, so OF = 1; the bit shifted out was 0.
    /// </summary>
    [Fact]
    public void ShrdByOneSetsOverflowOnASignChange()
    {
        // MOV CX,BB7A ; MOV BP,4000 ; SHRD CX,BP,1
        Cpu cpu = Run(3, 0xB9, 0x7A, 0xBB, 0xBD, 0x00, 0x40, 0x0F, 0xAC, 0xE9, 0x01);

        Assert.Equal(0x5DBD, cpu.State.Cx);
        Assert.True(cpu.Flags.Overflow);
        Assert.False(cpu.Flags.Carry);
    }
}
