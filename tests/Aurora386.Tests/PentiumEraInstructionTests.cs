using Aurora386.Core.Cpu;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// Two instructions that a compiler emits into code targeting older hardware:
/// the conditional move and the multi-byte NOP.
///
/// They belong together because they arrived together, on the Pentium Pro of
/// 1995, and because they share the same trap. Neither is required by a
/// program that declares itself a 386 program, so both turn up unannounced in
/// freshly built code -- CMOVcc because the compiler prefers it to a branch,
/// and the multi-byte NOP because the assembler uses it to pad a loop onto an
/// aligned address. An emulator that does not recognise them raises #UD, and
/// a #UD before a handler is installed is a triple fault: the machine resets,
/// and what a person sees is a bootloader restarting for ever with no message.
/// That is precisely how the NOP was found here, in Limine's 32-bit stage.
/// </summary>
public class PentiumEraInstructionTests
{
    private const uint CodeBase = 0x1000;
    private const uint FaultHandler = 0x0500;

    /// <summary>
    /// A processor of the given model with the code at a known address, in
    /// real mode with 32-bit operands available through the 0x66 prefix.
    /// </summary>
    private static (Cpu cpu, PhysicalMemoryBus bus) Setup(CpuModel model, params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus, new IoBus());
        cpu.State.SelectModel(model);
        foreach (SegReg s in Enum.GetValues<SegReg>()) cpu.State.LoadSegmentRealMode(s, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));

        // Point the invalid-opcode vector somewhere recognisable, so a fault
        // is observed as a fault rather than as a reset.
        bus.WriteWord(6 * 4, (ushort)FaultHandler);
        bus.WriteWord(6 * 4 + 2, 0x0000);

        return (cpu, bus);
    }

    // -----------------------------------------------------------------------
    // The multi-byte NOP (0F 1F /0)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The encoding actually met in Limine's 32-bit stage, byte for byte as it
    /// was read out of guest memory at the fault: five 0x66 prefixes, a CS
    /// override, then 0F 1F 84 00 with a four-byte displacement. Fourteen
    /// bytes of padding in one instruction.
    ///
    /// The 0x67 in front is this test's own, not Limine's: the code there runs
    /// in a 32-bit segment where 32-bit addressing is the default, and in real
    /// mode the prefix is what selects it. Without it the same ModR/M byte
    /// means [SI+disp16] and the instruction is eleven bytes long rather than
    /// fourteen -- which is correct 16-bit decoding, and the reason the
    /// distinction is worth stating here rather than discovering later.
    /// </summary>
    [Fact]
    public void TheEncodingThatFaultedIsOneInstructionThatDoesNothing()
    {
        var (cpu, _) = Setup(CpuModel.Pentium,
            0x67,
            0x66, 0x66, 0x66, 0x66, 0x66, 0x2E, 0x0F, 0x1F, 0x84, 0x00,
            0x00, 0x00, 0x00, 0x00);

        cpu.State.Eax = 0xDEADBEEF;
        cpu.State.Ebx = 0x12345678;

        cpu.Step();

        // One step consumed the whole thing: the next instruction begins
        // immediately after the fourteenth byte of the instruction proper.
        // Getting the length wrong is the failure that matters, because the
        // processor would then decode the middle of this instruction as the
        // next one.
        Assert.Equal(CodeBase + 1 + 14, cpu.State.Eip);

        // And it changed nothing at all.
        Assert.Equal(0xDEADBEEFu, cpu.State.Eax);
        Assert.Equal(0x12345678u, cpu.State.Ebx);
    }

    /// <summary>
    /// The lengths an assembler chooses from. Each is the same instruction
    /// with a different ModR/M, and each must be consumed whole.
    /// </summary>
    [Theory]
    // 0F 1F C0            -- register form, three bytes
    [InlineData(3, new byte[] { 0x0F, 0x1F, 0xC0 })]
    // 0F 1F 00            -- [EAX], three bytes
    [InlineData(3, new byte[] { 0x0F, 0x1F, 0x00 })]
    // 0F 1F 40 00         -- [EAX+disp8], four bytes
    [InlineData(4, new byte[] { 0x0F, 0x1F, 0x40, 0x00 })]
    // 0F 1F 44 00 00      -- SIB with disp8, five bytes
    [InlineData(5, new byte[] { 0x0F, 0x1F, 0x44, 0x00, 0x00 })]
    // 0F 1F 80 00000000   -- [EAX+disp32], seven bytes
    [InlineData(7, new byte[] { 0x0F, 0x1F, 0x80, 0x00, 0x00, 0x00, 0x00 })]
    // 0F 1F 84 00 00000000 -- SIB with disp32, eight bytes
    [InlineData(8, new byte[] { 0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    public void EveryPaddingLengthIsConsumedWhole(int length, byte[] code)
    {
        // 0x67 selects 32-bit addressing in real mode, which is what these
        // ModR/M forms above are written for.
        var full = new byte[code.Length + 1];
        full[0] = 0x67;
        code.CopyTo(full, 1);

        var (cpu, _) = Setup(CpuModel.Pentium, full);

        cpu.Step();

        Assert.Equal(CodeBase + 1 + (uint)length, cpu.State.Eip);
    }

    /// <summary>
    /// A memory operand's address is decoded but never read. The difference is
    /// observable: a NOP that dereferenced its operand would touch memory-
    /// mapped device registers when the padding happened to name one.
    /// </summary>
    [Fact]
    public void TheMemoryOperandIsNeverRead()
    {
        // 67 0F 1F 80 00 00 0F 00 -- NOP [EAX+0xF0000]
        var (cpu, bus) = Setup(CpuModel.Pentium,
            0x67, 0x0F, 0x1F, 0x80, 0x00, 0x00, 0x0F, 0x00);

        cpu.State.Eax = 0;
        bus.WriteByte(0x000F0000, 0x5A);

        cpu.Step();

        Assert.Equal(CodeBase + 8, cpu.State.Eip);
        Assert.Equal(0x5A, bus.ReadByte(0x000F0000));
    }

    /// <summary>
    /// Only reg == 0 is the NOP. The other values of that field are reserved,
    /// and one of them is the prefetch hint group, which we do not implement:
    /// answering #UD is how software learns the difference.
    /// </summary>
    [Fact]
    public void AReservedRegFieldIsNotTheNop()
    {
        // 0F 1F C8 -- reg field 1, not 0.
        var (cpu, _) = Setup(CpuModel.Pentium, 0x0F, 0x1F, 0xC8);

        cpu.Step();

        Assert.Equal(FaultHandler, cpu.State.Eip);
    }

    [Theory]
    [InlineData(CpuModel.Intel386)]
    [InlineData(CpuModel.Intel486)]
    public void TheMultiByteNopDoesNotExistBeforeThePentium(CpuModel model)
    {
        var (cpu, _) = Setup(model, 0x0F, 0x1F, 0xC0);

        cpu.Step();

        Assert.Equal(FaultHandler, cpu.State.Eip);
    }

    // -----------------------------------------------------------------------
    // CMOVcc (0F 40-4F)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The move happens when the condition holds. CMOVZ with the zero flag
    /// set: 66 0F 44 C3 is CMOVZ EAX,EBX.
    /// </summary>
    [Fact]
    public void AConditionalMoveThatHoldsWritesTheSource()
    {
        var (cpu, _) = Setup(CpuModel.Pentium, 0x66, 0x0F, 0x44, 0xC3);

        cpu.State.Eax = 0x11111111;
        cpu.State.Ebx = 0x22222222;
        cpu.Flags.Zero = true;

        cpu.Step();

        Assert.Equal(0x22222222u, cpu.State.Eax);
    }

    [Fact]
    public void AConditionalMoveThatFailsLeavesTheDestinationAlone()
    {
        var (cpu, _) = Setup(CpuModel.Pentium, 0x66, 0x0F, 0x44, 0xC3);

        cpu.State.Eax = 0x11111111;
        cpu.State.Ebx = 0x22222222;
        cpu.Flags.Zero = false;

        cpu.Step();

        Assert.Equal(0x11111111u, cpu.State.Eax);
        Assert.Equal(CodeBase + 4, cpu.State.Eip);
    }

    /// <summary>
    /// The subtlety that makes this instruction easy to get wrong. A 16-bit
    /// CMOVcc whose condition FAILS must leave the whole 32-bit register
    /// untouched -- high half included. An implementation that wrote the
    /// destination unconditionally, or that wrote zero through a 32-bit path,
    /// would destroy the upper half, and the corruption would surface far
    /// from here.
    /// </summary>
    [Fact]
    public void ASixteenBitMoveThatFailsPreservesTheWholeRegister()
    {
        // 0F 44 C3 with no 0x66 prefix: 16-bit operands in real mode.
        var (cpu, _) = Setup(CpuModel.Pentium, 0x0F, 0x44, 0xC3);

        cpu.State.Eax = 0xAAAABBBB;
        cpu.State.Ebx = 0xCCCCDDDD;
        cpu.Flags.Zero = false;

        cpu.Step();

        Assert.Equal(0xAAAABBBBu, cpu.State.Eax);
    }

    /// <summary>
    /// And when a 16-bit move succeeds it writes only the low half, leaving
    /// the high half of the destination as it was. This is x86, not x86-64,
    /// where a 32-bit write zeroes the upper bits.
    /// </summary>
    [Fact]
    public void ASixteenBitMoveThatHoldsWritesOnlyTheLowHalf()
    {
        var (cpu, _) = Setup(CpuModel.Pentium, 0x0F, 0x44, 0xC3);

        cpu.State.Eax = 0xAAAABBBB;
        cpu.State.Ebx = 0xCCCCDDDD;
        cpu.Flags.Zero = true;

        cpu.Step();

        Assert.Equal(0xAAAADDDDu, cpu.State.Eax);
    }

    /// <summary>
    /// The condition is encoded in the low four bits exactly as it is for Jcc
    /// and SETcc. Walking the pairs proves the shared evaluator is indexed
    /// correctly rather than by accident on one opcode.
    /// </summary>
    [Theory]
    [InlineData(0x42, true)]    // CMOVC  -- carry set
    [InlineData(0x43, false)]   // CMOVNC -- carry clear
    public void TheConditionIsTakenFromTheLowFourBitsOfTheOpcode(byte opcode, bool expectMove)
    {
        var (cpu, _) = Setup(CpuModel.Pentium, 0x66, 0x0F, opcode, 0xC3);

        cpu.State.Eax = 0x11111111;
        cpu.State.Ebx = 0x22222222;
        cpu.Flags.Carry = true;

        cpu.Step();

        Assert.Equal(expectMove ? 0x22222222u : 0x11111111u, cpu.State.Eax);
    }

    /// <summary>
    /// A memory source is read whether or not the move is performed, because
    /// the address is computed either way. What must not happen is the write.
    /// </summary>
    [Fact]
    public void AFailedMoveFromMemoryStillLeavesTheDestinationAlone()
    {
        // 67 66 0F 44 05 00 20 00 00 -- CMOVZ EAX,[0x2000]
        var (cpu, bus) = Setup(CpuModel.Pentium,
            0x67, 0x66, 0x0F, 0x44, 0x05, 0x00, 0x20, 0x00, 0x00);

        bus.WriteDword(0x2000, 0x99999999);
        cpu.State.Eax = 0x11111111;
        cpu.Flags.Zero = false;

        cpu.Step();

        Assert.Equal(0x11111111u, cpu.State.Eax);
        Assert.Equal(CodeBase + 9, cpu.State.Eip);
    }

    [Theory]
    [InlineData(CpuModel.Intel386)]
    [InlineData(CpuModel.Intel486)]
    public void ConditionalMovesDoNotExistBeforeThePentium(CpuModel model)
    {
        var (cpu, _) = Setup(model, 0x66, 0x0F, 0x44, 0xC3);

        cpu.Step();

        Assert.Equal(FaultHandler, cpu.State.Eip);
    }
}
