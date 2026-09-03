using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// These tests execute real machine code from 0x1000 in real mode and assert
/// on the resulting CPU state -- no mocking of the decoder, just bytes in and
/// registers out.
/// </summary>
public class CpuTests
{
    private const uint CodeBase = 0x1000;

    private static (Cpu cpu, PhysicalMemoryBus bus) Setup(params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);

        // Real mode starting at 0000:1000, with a usable stack.
        cpu.State.LoadSegmentRealMode(SegReg.Cs, 0);
        cpu.State.LoadSegmentRealMode(SegReg.Ds, 0);
        cpu.State.LoadSegmentRealMode(SegReg.Es, 0);
        cpu.State.LoadSegmentRealMode(SegReg.Ss, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;

        bus.GetRamSpan(CodeBase, code.Length).Clear();
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));
        return (cpu, bus);
    }

    private static Cpu Run(int steps, params byte[] code)
    {
        var (cpu, bus) = Setup(code);
        using (bus)
        {
            for (int i = 0; i < steps; i++) cpu.Step();
            return cpu;
        }
    }

    [Fact]
    public void MovLoadsAnImmediateIntoA16BitRegister()
    {
        // B8 34 12 = MOV AX, 0x1234
        var cpu = Run(1, 0xB8, 0x34, 0x12);
        Assert.Equal(0x1234, cpu.State.Ax);
        Assert.Equal(CodeBase + 3, cpu.State.Eip);
    }

    [Fact]
    public void MovToAn8BitRegisterLeavesTheOtherBytesAlone()
    {
        // B0 FF = MOV AL, 0xFF -- the upper half of AX must survive untouched
        var (cpu, bus) = Setup(0xB0, 0xFF);
        using (bus)
        {
            cpu.State.Eax = 0x11223344;
            cpu.Step();
            Assert.Equal(0x112233FFu, cpu.State.Eax);
        }
    }

    [Fact]
    public void MovToA16BitRegisterPreservesTheUpperHalf()
    {
        // An important difference from x86-64: writing AX does not zero the
        // upper 16 bits of EAX. Get this wrong and 32-bit code silently loses
        // half of every value it touches through a 16-bit operand.
        var (cpu, bus) = Setup(0xB8, 0xFF, 0xFF);
        using (bus)
        {
            cpu.State.Eax = 0x11223344;
            cpu.Step();
            Assert.Equal(0x1122FFFFu, cpu.State.Eax);
        }
    }

    [Fact]
    public void AddCombinesRegistersAndSetsFlags()
    {
        // B8 FF FF = MOV AX,0xFFFF ; 05 01 00 = ADD AX,1
        var cpu = Run(2, 0xB8, 0xFF, 0xFF, 0x05, 0x01, 0x00);
        Assert.Equal(0, cpu.State.Ax);
        Assert.True(cpu.Flags.Carry);
        Assert.True(cpu.Flags.Zero);
    }

    [Fact]
    public void SubSetsCarryAsABorrow()
    {
        // B8 05 00 = MOV AX,5 ; 2D 0A 00 = SUB AX,10
        var cpu = Run(2, 0xB8, 0x05, 0x00, 0x2D, 0x0A, 0x00);
        Assert.Equal(0xFFFB, cpu.State.Ax);   // -5 in two's complement
        Assert.True(cpu.Flags.Carry);
        Assert.True(cpu.Flags.Sign);
    }

    [Fact]
    public void CmpLeavesTheOperandsUnchanged()
    {
        // B8 05 00 = MOV AX,5 ; 3D 05 00 = CMP AX,5
        var cpu = Run(2, 0xB8, 0x05, 0x00, 0x3D, 0x05, 0x00);
        Assert.Equal(5, cpu.State.Ax);        // operand untouched
        Assert.True(cpu.Flags.Zero);
        Assert.False(cpu.Flags.Carry);
    }

    [Fact]
    public void PushAndPopRoundTripThroughTheStack()
    {
        // B8 CD AB = MOV AX,0xABCD ; 50 = PUSH AX ; 5B = POP BX
        var cpu = Run(3, 0xB8, 0xCD, 0xAB, 0x50, 0x5B);
        Assert.Equal(0xABCD, cpu.State.Bx);
        Assert.Equal(0x8000u, cpu.State.Esp);   // stack balanced
    }

    [Fact]
    public void ConditionalJumpIsTakenAndNotTaken()
    {
        // 31 C0 = XOR AX,AX (ZF=1) ; 74 02 = JZ +2 ; B0 FF = MOV AL,0xFF (skipped)
        var cpu = Run(2, 0x31, 0xC0, 0x74, 0x02, 0xB0, 0xFF);
        Assert.Equal(CodeBase + 6, cpu.State.Eip);   // the branch was taken
        Assert.Equal(0, cpu.State.GetReg8(0));

        // With ZF=0 the branch is not taken and MOV AL,0xFF does execute.
        //   B8 01 00  MOV AX,1 ; 83 F8 02  CMP AX,2 ; 74 02  JZ +2 ; B0 FF  MOV AL,0xFF
        var cpu2 = Run(4, 0xB8, 0x01, 0x00, 0x83, 0xF8, 0x02, 0x74, 0x02, 0xB0, 0xFF);
        Assert.False(cpu2.Flags.Zero);
        Assert.Equal(0xFF, cpu2.State.GetReg8(0));   // the MOV was not skipped
    }

    [Fact]
    public void CallAndRetReturnToTheRightAddress()
    {
        // E8 01 00 = CALL +1 ; 90 = NOP ; C3 = RET
        var cpu = Run(2, 0xE8, 0x01, 0x00, 0x90, 0xC3);
        // After the CALL we are at 0x1004 (the RET); after the RET we are
        // back at 0x1003.
        Assert.Equal(CodeBase + 3, cpu.State.Eip);
        Assert.Equal(0x8000u, cpu.State.Esp);
    }

    [Fact]
    public void ModRmAddressingWithDisplacementResolves()
    {
        // BB 00 20 = MOV BX,0x2000 ; C7 47 10 CD AB = MOV [BX+0x10],0xABCD
        var (cpu, bus) = Setup(0xBB, 0x00, 0x20, 0xC7, 0x47, 0x10, 0xCD, 0xAB);
        using (bus)
        {
            cpu.Step();
            cpu.Step();
            Assert.Equal(0xABCD, bus.ReadWord(0x2010));
        }
    }

    [Fact]
    public void EightBitDisplacementIsSignExtended()
    {
        // BB 00 20 = MOV BX,0x2000 ; C7 47 FE CD AB = MOV [BX-2],0xABCD
        var (cpu, bus) = Setup(0xBB, 0x00, 0x20, 0xC7, 0x47, 0xFE, 0xCD, 0xAB);
        using (bus)
        {
            cpu.Step();
            cpu.Step();
            Assert.Equal(0xABCD, bus.ReadWord(0x1FFE));   // 0x2000 - 2: the displacement is signed
        }
    }

    [Fact]
    public void SegmentOverridePrefixSelectsTheSegment()
    {
        // 26 C7 07 CD AB = MOV ES:[BX],0xABCD; with ES=0x300 that resolves
        // to linear 0x3000+BX rather than DS's segment
        var (cpu, bus) = Setup(0x26, 0xC7, 0x07, 0xCD, 0xAB);
        using (bus)
        {
            cpu.State.LoadSegmentRealMode(SegReg.Es, 0x300);
            cpu.State.Bx = 0x10;
            cpu.Step();
            Assert.Equal(0xABCD, bus.ReadWord(0x3010));
        }
    }

    [Fact]
    public void OperandSizePrefixSwitchesTo32Bit()
    {
        // 66 B8 78 56 34 12 = MOV EAX,0x12345678
        var cpu = Run(1, 0x66, 0xB8, 0x78, 0x56, 0x34, 0x12);
        Assert.Equal(0x12345678u, cpu.State.Eax);
    }

    [Fact]
    public void IncAndDecLeaveCarryUntouched()
    {
        // F9 = STC ; 40 = INC AX -- INC and DEC deliberately leave carry
        // alone, which is what makes them usable inside multi-word arithmetic
        var cpu = Run(2, 0xF9, 0x40);
        Assert.True(cpu.Flags.Carry);
        Assert.Equal(1, cpu.State.Ax);
    }

    [Fact]
    public void SoftwareInterruptDispatchesInRealMode()
    {
        var (cpu, bus) = Setup(0xCD, 0x21);        // INT 0x21
        using (bus)
        {
            // Vector 0x21 -> 0F00:0100
            bus.WriteWord(0x21 * 4, 0x0100);
            bus.WriteWord(0x21 * 4 + 2, 0x0F00);
            cpu.Flags.Interrupt = true;

            cpu.Step();

            Assert.Equal(0x0F00, cpu.State.Cs.Selector);
            Assert.Equal(0x0100u, cpu.State.Eip);
            Assert.False(cpu.Flags.Interrupt);     // interrupts disabled on entry
        }
    }

    [Fact]
    public void IretRestoresTheContextAndFlags()
    {
        // INT 0x21 with an IRET in the handler: execution must resume at
        // exactly the instruction after the INT, with the flags restored.
        var (cpu, bus) = Setup(0xCD, 0x21);
        using (bus)
        {
            bus.WriteWord(0x21 * 4, 0x0100);
            bus.WriteWord(0x21 * 4 + 2, 0x0000);
            bus.WriteByte(0x0100, 0xCF);           // IRET
            cpu.Flags.Interrupt = true;

            cpu.Step();                            // INT
            cpu.Step();                            // IRET

            Assert.Equal(CodeBase + 2, cpu.State.Eip);
            Assert.Equal(0, cpu.State.Cs.Selector);
            Assert.True(cpu.Flags.Interrupt);      // IRET restored them
        }
    }

    [Fact]
    public void SummationLoopProducesTheExpectedTotal()
    {
        // Sums 1..10 with a real loop, checking that decoding, flags and
        // branches work together rather than only in isolation.
        //   31 C0        XOR AX,AX
        //   B9 0A 00     MOV CX,10
        //   01 C8        ADD AX,CX      <- loop body
        //   49           DEC CX
        //   75 FB        JNZ -5
        var cpu = Run(50,
            0x31, 0xC0,
            0xB9, 0x0A, 0x00,
            0x01, 0xC8,
            0x49,
            0x75, 0xFB);

        Assert.Equal(55, cpu.State.Ax);    // 1+2+...+10
        Assert.Equal(0, cpu.State.Cx);
    }

    [Fact]
    public void NearJumpBackwardUsesASignedDisplacement()
    {
        // E9 with a negative 16-bit displacement: 0xFFFA is -6, not +65530.
        // Without correct sign extension execution shoots off into the weeds
        // and the failure surfaces a very long way from where it started.
        //   90 90 90     three NOPs
        //   E9 FA FF     JMP -6  -> back to the first NOP
        var (cpu, bus) = Setup(0x90, 0x90, 0x90, 0xE9, 0xFA, 0xFF);
        using (bus)
        {
            for (int i = 0; i < 4; i++) cpu.Step();
            Assert.Equal(CodeBase, cpu.State.Eip);
        }
    }

    [Fact]
    public void NearCallBackwardUsesASignedDisplacement()
    {
        //   90           NOP
        //   E8 FC FF     CALL -4 -> calls the leading NOP
        var (cpu, bus) = Setup(0x90, 0xE8, 0xFC, 0xFF);
        using (bus)
        {
            cpu.Step();                       // NOP
            cpu.Step();                       // CALL
            Assert.Equal(CodeBase, cpu.State.Eip);
            Assert.Equal(0x7FFEu, cpu.State.Esp);          // return address pushed
            Assert.Equal(CodeBase + 4, bus.ReadWord(0x7FFE));
        }
    }

    [Fact]
    public void NearConditionalJumpBackwardIsTaken()
    {
        // 0F 84 (near JZ) with a negative displacement.
        //   31 C0        XOR AX,AX     (ZF=1)
        //   0F 84 FA FF  JZ -6         -> back to the XOR
        var (cpu, bus) = Setup(0x31, 0xC0, 0x0F, 0x84, 0xFA, 0xFF);
        using (bus)
        {
            cpu.Step();
            cpu.Step();
            Assert.Equal(CodeBase, cpu.State.Eip);
        }
    }

    [Fact]
    public void InvalidOpcodeRaisesException6()
    {
        var (cpu, bus) = Setup(0x0F, 0xFF);        // 0x0F 0xFF is not a 386 opcode
        using (bus)
        {
            bus.WriteWord(6 * 4, 0x0200);          // vector 6: invalid opcode
            bus.WriteWord(6 * 4 + 2, 0x0000);
            cpu.Step();
            Assert.Equal(0x0200u, cpu.State.Eip);  // the handler was entered
        }
    }
}
