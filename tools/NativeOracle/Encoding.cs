using Aurora386.Core.Cpu;

namespace NativeOracle;

/// <summary>
/// Operand width, named the way the SDM names it rather than by bit count,
/// because a family's byte encoding (opcode base, 0x66 prefix, immediate
/// length) all key off this rather than off the number 8/16/32 directly.
/// </summary>
public enum Width
{
    Byte = 8,
    Word = 16,
    Dword = 32,
}

/// <summary>
/// Builds the raw bytes of one instruction, and the pieces every family
/// shares: which register holds the buffer address, which registers are
/// therefore off limits, and the ModR/M byte itself.
///
/// A memory operand is always [base+disp32] with mod=10: that one encoding
/// is legal whichever register the base is (mod=00,rm=101 would instead mean
/// "no base, disp32 only" when the base happens to be EBP, and rm=100 would
/// need a SIB byte when the base happens to be ESP -- both are avoided here
/// by construction, not handled). Addressing-mode variety (disp8, disp0,
/// SIB) is already exercised by the SingleStepTests vectors that
/// tools/HardwareSuite runs; this oracle is about arithmetic and data-flow
/// semantics against real silicon, not about decoding.
/// </summary>
public static class InstructionEncoding
{
    /// <summary>
    /// The seven general registers the host stub loads and stores. ESP is
    /// never one of them: it is the real stack pointer of the thread running
    /// the stub, and the harness must not touch it.
    /// </summary>
    public static readonly Reg[] AddressableRegisters =
    [
        Reg.Eax, Reg.Ecx, Reg.Edx, Reg.Ebx, Reg.Ebp, Reg.Esi, Reg.Edi,
    ];

    /// <summary>0x66: flips the default 32-bit operand size to 16 bits.</summary>
    public const byte OperandSizePrefix = 0x66;

    /// <summary>A small, fixed offset into the shared buffer, well clear of its ends.</summary>
    public static int RandomDisplacement(Random rng) => rng.Next(0, 512);

    /// <summary>One register drawn from <see cref="AddressableRegisters"/>, excluding any given.</summary>
    public static Reg PickRegister(Random rng, params ReadOnlySpan<Reg> exclude)
    {
        Reg candidate;
        do
        {
            candidate = AddressableRegisters[rng.Next(AddressableRegisters.Length)];
        }
        while (exclude.Contains(candidate));

        return candidate;
    }

    /// <summary>mod=11: both operands are registers.</summary>
    public static byte ModRmRegister(int regField, Reg rm) => (byte)(0xC0 | (regField << 3) | (int)rm);

    /// <summary>mod=11 with an explicit 3-bit r/m already known to be a register.</summary>
    public static byte ModRmRegister(int regField, int rm) => (byte)(0xC0 | (regField << 3) | rm);

    /// <summary>
    /// mod=10, [base+disp32]: a memory operand through <paramref name="baseRegister"/>.
    /// The displacement is appended separately by the caller (little-endian).
    /// </summary>
    public static byte ModRmMemory(int regField, Reg baseRegister) => (byte)(0x80 | (regField << 3) | (int)baseRegister);

    public static void AppendDisplacement(List<byte> bytes, int displacement) => bytes.AddRange(BitConverter.GetBytes(displacement));

    public static void AppendImmediate(List<byte> bytes, Width width, long immediate)
    {
        switch (width)
        {
            case Width.Byte:
                bytes.Add((byte)immediate);
                break;
            case Width.Word:
                bytes.AddRange(BitConverter.GetBytes((ushort)immediate));
                break;
            case Width.Dword:
                bytes.AddRange(BitConverter.GetBytes((uint)immediate));
                break;
        }
    }

    /// <summary>Prepends 0x66 when the width is 16 bits; a no-op for byte and dword forms.</summary>
    public static void AppendOperandSizePrefix(List<byte> bytes, Width width)
    {
        if (width == Width.Word)
        {
            bytes.Add(OperandSizePrefix);
        }
    }
}
