using Aurora86.Core.Cpu;

namespace NativeOracle.Families;

/// <summary>
/// BSF/BSR (0F BC/BD) and the bit-test group BT/BTS/BTR/BTC, each in its
/// register-bit-offset form (0F A3/AB/B3/BB) and its immediate form (0F BA
/// /4-/7). The register form's bit offset is never masked when the operand
/// is memory -- a real BT can address a different dword of memory than the
/// one named, at base+4*(offset&gt;&gt;5) -- so that form only ever picks a
/// register operand here; the immediate form's offset is always masked by
/// its own encoding (an imm8, itself only 5 or 4 bits wide) and is free to
/// use memory.
/// </summary>
public static class BitFamilies
{
    private static readonly Width[] WordOrDword = [Width.Word, Width.Dword];

    public static IEnumerable<Family> All()
    {
        yield return new Family("BSF", context => GenerateBitScan(context, "BSF", forward: true));
        yield return new Family("BSR", context => GenerateBitScan(context, "BSR", forward: false));
        yield return new Family("BT", context => GenerateBitTest(context, "BT", registerOpcode: 0xA3, immediateReg: 4));
        yield return new Family("BTS", context => GenerateBitTest(context, "BTS", registerOpcode: 0xAB, immediateReg: 5));
        yield return new Family("BTR", context => GenerateBitTest(context, "BTR", registerOpcode: 0xB3, immediateReg: 6));
        yield return new Family("BTC", context => GenerateBitTest(context, "BTC", registerOpcode: 0xBB, immediateReg: 7));
    }

    /// <summary>0F BC (BSF) / BD (BSR): Gv, Ev. One case in eight uses a zero source, per the contract, comparing only ZF.</summary>
    private static TestCase GenerateBitScan(FamilyContext context, string name, bool forward)
    {
        Random rng = context.Rng;
        Width width = WordOrDword[rng.Next(WordOrDword.Length)];
        bool memory = rng.Next(2) == 0;
        bool zeroSource = rng.Next(8) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        code.Add(0x0F);
        code.Add(forward ? (byte)0xBC : (byte)0xBD);

        uint mask = width == Width.Word ? 0xFFFFu : 0xFFFFFFFFu;
        uint sourceValue = zeroSource ? 0 : (rng.NextUInt32() | 1) & mask;   // bit 0 set: never zero, no retry needed.

        Reg destination;
        string operand;
        if (memory)
        {
            Reg baseRegister = InstructionEncoding.PickRegister(rng);
            destination = InstructionEncoding.PickRegister(rng, baseRegister);
            int displacement = InstructionEncoding.RandomDisplacement(rng);
            gpr[(int)baseRegister] = context.BufferAddress;
            code.Add(InstructionEncoding.ModRmMemory((int)destination, baseRegister));
            InstructionEncoding.AppendDisplacement(code, displacement);
            operand = $"[{baseRegister}+{displacement}] ({width})";

            byte[] buffer = context.FreshBuffer();
            byte[] valueBytes = BitConverter.GetBytes(sourceValue);
            Array.Copy(valueBytes, 0, buffer, displacement, (int)width / 8);
            return BuildBitScan(name, destination, operand, code, gpr, rng, buffer, zeroSource);
        }

        Reg source = InstructionEncoding.PickRegister(rng);
        destination = InstructionEncoding.PickRegister(rng, source);
        gpr[(int)source] = width == Width.Word ? (gpr[(int)source] & 0xFFFF0000u) | sourceValue : sourceValue;
        code.Add(InstructionEncoding.ModRmRegister((int)destination, source));
        operand = Operands.Name(source, width);
        return BuildBitScan(name, destination, operand, code, gpr, rng, context.FreshBuffer(), zeroSource);
    }

    private static TestCase BuildBitScan(string name, Reg destination, string operand, List<byte> code, uint[] gpr, Random rng, byte[] buffer, bool zeroSource) => new()
    {
        Family = name,
        Description = $"{name} {destination}, {operand}" + (zeroSource ? " (zero source)" : ""),
        Instruction = code.ToArray(),
        InitialGpr = gpr,
        InitialEflags = rng.NextUInt32(),
        InitialBuffer = buffer,
        DefinedFlagsMask = FlagsSpec.BitScan,
        IgnoredRegisters = zeroSource ? [destination] : [],
    };

    private static TestCase GenerateBitTest(FamilyContext context, string name, byte registerOpcode, int immediateReg)
    {
        Random rng = context.Rng;
        Width width = WordOrDword[rng.Next(WordOrDword.Length)];
        bool useImmediateForm = rng.Next(2) == 0;

        uint[] gpr = context.FreshGpr();
        var code = new List<byte>();
        InstructionEncoding.AppendOperandSizePrefix(code, width);
        code.Add(0x0F);

        string operand;
        if (useImmediateForm)
        {
            code.Add(0xBA);
            bool memory = rng.Next(2) == 0;
            int maxBit = width == Width.Word ? 15 : 31;
            byte bitOffset = (byte)rng.Next(0, maxBit + 1);

            if (memory)
            {
                Reg baseRegister = InstructionEncoding.PickRegister(rng);
                int displacement = InstructionEncoding.RandomDisplacement(rng);
                gpr[(int)baseRegister] = context.BufferAddress;
                code.Add(InstructionEncoding.ModRmMemory(immediateReg, baseRegister));
                InstructionEncoding.AppendDisplacement(code, displacement);
                code.Add(bitOffset);
                operand = $"[{baseRegister}+{displacement}] ({width}), {bitOffset}";
            }
            else
            {
                Reg destination = InstructionEncoding.PickRegister(rng);
                code.Add(InstructionEncoding.ModRmRegister(immediateReg, destination));
                code.Add(bitOffset);
                operand = $"{Operands.Name(destination, width)}, {bitOffset}";
            }
        }
        else
        {
            code.Add(registerOpcode);
            // Register r/m only: with memory, the bit offset addresses a
            // different dword entirely (base+4*(offset>>5)), which is a
            // decoding concern this oracle leaves to tools/HardwareSuite.
            Reg destination = InstructionEncoding.PickRegister(rng);
            Reg bitOffsetRegister = InstructionEncoding.PickRegister(rng, destination);
            code.Add(InstructionEncoding.ModRmRegister((int)bitOffsetRegister, destination));
            operand = $"{Operands.Name(destination, width)}, {Operands.Name(bitOffsetRegister, width)}";
        }

        return new TestCase
        {
            Family = name,
            Description = $"{name} {operand}",
            Instruction = code.ToArray(),
            InitialGpr = gpr,
            InitialEflags = rng.NextUInt32(),
            InitialBuffer = context.FreshBuffer(),
            DefinedFlagsMask = FlagsSpec.BitTest,
        };
    }
}
