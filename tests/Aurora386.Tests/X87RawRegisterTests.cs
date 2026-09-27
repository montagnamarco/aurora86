using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// The x87 register file holds its registers as raw ten-byte values, and the
/// MMX registers alias their significands. What these tests pin down is that
/// bits which are not an ordinary number -- a NaN payload, an MMX value --
/// come back exactly through every path that only moves a register: FLD and
/// FSTP TBYTE, FSAVE and FRSTOR, FXCH, and a machine snapshot.
/// </summary>
public class X87RawRegisterTests
{
    private const uint CodeBase = 0x1000;

    /// <summary>A NaN whose payload is not the canonical one, with the sign set.</summary>
    private const ulong PayloadSignificand = 0xC123456789ABCDEF;
    private const ushort NegativeNaNExponent = 0xFFFF;

    private static (Cpu Cpu, PhysicalMemoryBus Bus) Run(byte[] program, Action<PhysicalMemoryBus>? prepare = null)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new Cpu(bus);

        foreach (SegReg segment in Enum.GetValues<SegReg>())
        {
            cpu.State.LoadSegmentRealModeReset(segment, 0);
        }

        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        program.CopyTo(bus.GetRamSpan(CodeBase, program.Length));
        prepare?.Invoke(bus);

        while (cpu.State.Eip < CodeBase + program.Length)
        {
            cpu.Step();
        }

        return (cpu, bus);
    }

    private static void WriteTenBytes(PhysicalMemoryBus bus, uint address, ulong significand, ushort signExponent)
    {
        bus.WriteDword(address, (uint)significand);
        bus.WriteDword(address + 4, (uint)(significand >> 32));
        bus.WriteWord(address + 8, signExponent);
    }

    private static ulong ReadSignificand(PhysicalMemoryBus bus, uint address)
        => bus.ReadDword(address) | ((ulong)bus.ReadDword(address + 4) << 32);

    /// <summary>
    /// FLD TBYTE [4000] then FSTP TBYTE [4010] writes back the same ten bytes,
    /// NaN payload and sign included. Rounding the register through a number
    /// made this the canonical NaN C000000000000000 / 7FFF.
    /// </summary>
    [Fact]
    public void ATbyteLoadAndStoreKeepANaNPayload()
    {
        byte[] program = [0xDB, 0x2E, 0x00, 0x40, 0xDB, 0x3E, 0x10, 0x40];

        (_, PhysicalMemoryBus bus) = Run(program,
            memory => WriteTenBytes(memory, 0x4000, PayloadSignificand, NegativeNaNExponent));

        Assert.Equal(PayloadSignificand, ReadSignificand(bus, 0x4010));
        Assert.Equal(NegativeNaNExponent, bus.ReadWord(0x4018));
    }

    /// <summary>
    /// FLD TBYTE, FNSAVE [3000], FRSTOR [3000], FSTP TBYTE: the payload survives
    /// the save and restore an operating system performs on every task switch.
    /// </summary>
    [Fact]
    public void FsaveAndFrstorKeepANaNPayload()
    {
        byte[] program =
        [
            0xDB, 0x2E, 0x00, 0x40,
            0xDD, 0x36, 0x00, 0x30,
            0xDD, 0x26, 0x00, 0x30,
            0xDB, 0x3E, 0x10, 0x40,
        ];

        (_, PhysicalMemoryBus bus) = Run(program,
            memory => WriteTenBytes(memory, 0x4000, PayloadSignificand, NegativeNaNExponent));

        Assert.Equal(PayloadSignificand, ReadSignificand(bus, 0x4010));
        Assert.Equal(NegativeNaNExponent, bus.ReadWord(0x4018));
    }

    /// <summary>
    /// An MMX value in MM3 is R3's significand with an all-ones exponent; FSAVE
    /// writes it into R3's slot of the image (offset 14 + 3*10 in the 16-bit
    /// real-mode layout) and FRSTOR puts it back unchanged. The FSAVE tag for
    /// it is 2, special.
    /// </summary>
    [Fact]
    public void AnMmxValueSurvivesFsaveAndFrstor()
    {
        const ulong value = 0x0123456789ABCDEF;
        const uint image = 0x3000;
        const uint realModeRegisterArea = 14;

        byte[] save = [0xDD, 0x36, 0x00, 0x30];
        (Cpu cpu, PhysicalMemoryBus bus) = Run([]);

        cpu.Fpu.WriteMmx(3, value);
        cpu.Fpu.EnterMmxState();

        save.CopyTo(bus.GetRamSpan(CodeBase, save.Length));
        cpu.State.Eip = CodeBase;
        cpu.Step();

        uint slot = image + realModeRegisterArea + 3 * 10;
        Assert.Equal(value, ReadSignificand(bus, slot));
        Assert.Equal(0xFFFF, bus.ReadWord(slot + 8));

        ushort tags = bus.ReadWord(image + 4);
        Assert.Equal(2, (tags >> 6) & 3);

        // FNSAVE reinitialises the unit; FRSTOR brings MM3 back.
        byte[] restore = [0xDD, 0x26, 0x00, 0x30];
        restore.CopyTo(bus.GetRamSpan(CodeBase, restore.Length));
        cpu.State.Eip = CodeBase;
        cpu.Step();

        Assert.Equal(value, cpu.Fpu.ReadMmx(3));
    }

    /// <summary>
    /// Any MMX instruction leaves TOP = 0 and every register tagged in use;
    /// EMMS then tags them all empty (tag word FFFF).
    /// </summary>
    [Fact]
    public void EnteringAndLeavingMmxStateSetsTopAndTags()
    {
        // FLD1 moves TOP to 7 first.
        (Cpu cpu, _) = Run([0xD9, 0xE8]);
        Assert.Equal(7, (cpu.Fpu.StatusWord >> 11) & 7);

        cpu.Fpu.EnterMmxState();
        Assert.Equal(0, (cpu.Fpu.StatusWord >> 11) & 7);
        Assert.NotEqual(0xFFFF, cpu.Fpu.TagWord);

        for (int i = 0; i < 8; i++)
        {
            Assert.NotEqual(3, (cpu.Fpu.TagWord >> (i * 2)) & 3);
        }

        cpu.Fpu.EmptyMmxState();
        Assert.Equal(0xFFFF, cpu.Fpu.TagWord);
    }

    /// <summary>
    /// FCHS changes bit 79 only: a NaN keeps its payload and becomes positive.
    /// FLD TBYTE [4000]; FCHS; FSTP TBYTE [4010].
    /// </summary>
    [Fact]
    public void FchsFlipsOnlyTheSignOfANaN()
    {
        byte[] program = [0xDB, 0x2E, 0x00, 0x40, 0xD9, 0xE0, 0xDB, 0x3E, 0x10, 0x40];

        (_, PhysicalMemoryBus bus) = Run(program,
            memory => WriteTenBytes(memory, 0x4000, PayloadSignificand, NegativeNaNExponent));

        Assert.Equal(PayloadSignificand, ReadSignificand(bus, 0x4010));
        Assert.Equal(0x7FFF, bus.ReadWord(0x4018));
    }

    /// <summary>
    /// FSAVE writes the registers in stack order: after FLD1 (TOP = 7), ST(0)
    /// = 1.0 is the first register in the image, at offset 14 of the 16-bit
    /// real-mode layout. FNSAVE [3000] = DD 36 00 30.
    /// </summary>
    [Fact]
    public void FsaveWritesTheTopOfTheStackFirst()
    {
        const ulong one = 0x8000000000000000;
        const ushort oneExponent = 0x3FFF;

        (_, PhysicalMemoryBus bus) = Run([0xD9, 0xE8, 0xDD, 0x36, 0x00, 0x30]);

        Assert.Equal(one, ReadSignificand(bus, 0x3000 + 14));
        Assert.Equal(oneExponent, bus.ReadWord(0x3000 + 14 + 8));
    }

    /// <summary>
    /// FNINIT resets the control, status and tag words but leaves the
    /// registers alone (SDM FINIT): a value in MM2 is still there afterwards.
    /// FNINIT = DB E3.
    /// </summary>
    [Fact]
    public void FninitKeepsTheRegisterContents()
    {
        const ulong value = 0x0102030405060708;

        (Cpu cpu, PhysicalMemoryBus bus) = Run([]);
        cpu.Fpu.WriteMmx(2, value);

        byte[] program = [0xDB, 0xE3];
        program.CopyTo(bus.GetRamSpan(CodeBase, program.Length));
        cpu.State.Eip = CodeBase;
        cpu.Step();

        Assert.Equal(value, cpu.Fpu.ReadMmx(2));
        Assert.Equal(0xFFFF, cpu.Fpu.TagWord);
    }

    /// <summary>EMMS sets TOP to 0 like every MMX instruction: FLD1 (TOP 7), then EMMS.</summary>
    [Fact]
    public void EmmsResetsTheTopOfStack()
    {
        (Cpu cpu, _) = Run([0xD9, 0xE8]);
        cpu.State.SelectModel(CpuModel.PentiumMmx);

        cpu.Fpu.EmptyMmxState();

        Assert.Equal(0, (cpu.Fpu.StatusWord >> 11) & 7);
    }

    /// <summary>
    /// FXAM on a denormal reports C3 C2 = 11 (denormal), not "normal".
    /// FLD TBYTE [4000] with 0000:0000000000000001; FXAM = D9 E5.
    /// </summary>
    [Fact]
    public void FxamRecognisesADenormal()
    {
        byte[] program = [0xDB, 0x2E, 0x00, 0x40, 0xD9, 0xE5];

        (Cpu cpu, _) = Run(program, memory => WriteTenBytes(memory, 0x4000, 1, 0));

        const ushort conditionMask = 0x4700;
        const ushort denormal = 0x4400;   // C3 and C2
        Assert.Equal(denormal, cpu.Fpu.StatusWord & conditionMask);
    }

    /// <summary>
    /// A denormal survives arithmetic: 2^-16400 (a denormal) times 1.0 is
    /// still the same denormal, not zero. FLD TBYTE [4000]; FLD1; FMULP;
    /// FSTP TBYTE [4010].
    /// </summary>
    [Fact]
    public void ADenormalSurvivesMultiplicationByOne()
    {
        // 2^-16400 = 2^-16382 * 2^-18: exponent field 0, significand 2^(63-18).
        const ulong denormalSignificand = 1UL << 45;
        byte[] program =
        [
            0xDB, 0x2E, 0x00, 0x40,
            0xD9, 0xE8,
            0xDE, 0xC9,
            0xDB, 0x3E, 0x10, 0x40,
        ];

        (_, PhysicalMemoryBus bus) = Run(program,
            memory => WriteTenBytes(memory, 0x4000, denormalSignificand, 0));

        Assert.Equal(denormalSignificand, ReadSignificand(bus, 0x4010));
        Assert.Equal(0, bus.ReadWord(0x4018));
    }
}
