using Aurora86.Core.Cpu;
using Aurora86.Core.Cpu.Sse;
using Aurora86.Core.Memory;

namespace NativeOracle;

/// <summary>
/// Runs one test instruction on Aurora's own interpreter: a bare
/// <see cref="Cpu"/> over a <see cref="PhysicalMemoryBus"/> in 32-bit flat
/// protected mode, with no BIOS and no descriptor tables in memory -- the
/// segment caches are loaded directly, the way tools/HardwareSuite does for
/// the SingleStepTests vectors, because nothing here ever reloads a segment
/// from a selector.
///
/// A fresh <see cref="Cpu"/> and bus are built for every case rather than
/// reused, so a test that unexpectedly faults -- which, with an empty IDT,
/// escalates to a double and then a triple fault and resets the processor,
/// see Cpu.Execute.cs's <c>Escalate</c> -- can never leave stray state behind
/// for the next case.
/// </summary>
public sealed class EmulatorRunner
{
    private const uint RamSize = 16 * 1024 * 1024;

    /// <summary>Selector values are arbitrary here: nothing ever reloads a segment from the GDT.</summary>
    private const ushort FlatCodeSelector = 0x08;
    private const ushort FlatDataSelector = 0x10;

    private const uint CodeAddress = 0x0001_0000;

    public CpuModel Model { get; }

    public EmulatorRunner(CpuModel model)
    {
        Model = model;
    }

    /// <summary>
    /// Runs one instruction and returns the resulting registers, EFLAGS, the
    /// buffer's final contents, -- when the emulator raised a fault the host
    /// did not -- its vector, the eight MMX registers together with the x87
    /// status word (so an MMX family can check TOP and the tag word too),
    /// and the eight XMM registers together with MXCSR (so an SSE family can
    /// check both). <paramref name="initialMmx"/> is null for every family
    /// that is not about MMX, and <paramref name="initialXmm"/> null for
    /// every family that is not about SSE: the corresponding register file
    /// is then simply left at Reset()'s default and the caller ignores the
    /// matching results. An SSE family also selects
    /// <see cref="CpuModel.PentiumIII"/> and sets CR4.OSFXSR itself
    /// (directly, the way this whole method already pokes CR0/CPL rather
    /// than executing a MOV CRn) -- without it every SSE opcode would be
    /// <c>#UD</c> before ever reaching the instruction under test.
    /// </summary>
    public (uint[] Gpr, uint Eflags, byte[] Buffer, byte? Fault, ulong[] Mmx, ushort Fsw, ushort TagWord, XmmValue[] Xmm, uint Mxcsr) Run(
        ReadOnlySpan<byte> instruction, uint[] initialGpr, uint initialEflags,
        uint bufferAddress, ReadOnlySpan<byte> bufferContents,
        ulong[]? initialMmx = null, XmmValue[]? initialXmm = null, uint? initialMxcsr = null)
    {
        using var memory = new PhysicalMemoryBus(RamSize, RamSize);
        var cpu = new Cpu(memory);
        CpuState state = cpu.State;

        state.SelectModel(Model);
        state.Cr0 |= CpuState.Cr0ProtectionEnable;
        state.CplRaw = 0;

        LoadFlatSegment(state, SegReg.Es, FlatDataSelector, code: false);
        LoadFlatSegment(state, SegReg.Cs, FlatCodeSelector, code: true);
        LoadFlatSegment(state, SegReg.Ss, FlatDataSelector, code: false);
        LoadFlatSegment(state, SegReg.Ds, FlatDataSelector, code: false);
        LoadFlatSegment(state, SegReg.Fs, FlatDataSelector, code: false);
        LoadFlatSegment(state, SegReg.Gs, FlatDataSelector, code: false);

        state.Eax = initialGpr[(int)Reg.Eax];
        state.Ecx = initialGpr[(int)Reg.Ecx];
        state.Edx = initialGpr[(int)Reg.Edx];
        state.Ebx = initialGpr[(int)Reg.Ebx];
        state.Ebp = initialGpr[(int)Reg.Ebp];
        state.Esi = initialGpr[(int)Reg.Esi];
        state.Edi = initialGpr[(int)Reg.Edi];
        // Esp is deliberately left at whatever Reset() gave it: no template
        // ever names ESP as an operand, and no family generates a memory
        // operand that could land on it.
        state.Eip = CodeAddress;
        cpu.Flags.SetValue(initialEflags);
        state.Halted = false;

        if (initialMmx is not null)
        {
            // TOP <- 0, every tag valid: the state any MMX instruction but
            // EMMS itself leaves, and what the host side's FXRSTOR of an
            // all-ones FTW puts the real chip into (see HostRunner's
            // PrepareFxsaveDefaults). A random case is exercising the
            // instruction as it runs in an already-MMX-using program, not
            // the one-time transition out of x87 use.
            for (int i = 0; i < 8; i++) cpu.Fpu.WriteMmx(i, initialMmx[i]);
            cpu.Fpu.EnterMmxState();
        }

        if (initialXmm is not null)
        {
            state.Cr4 |= CpuState.Cr4OsFxsr;
            for (int i = 0; i < 8; i++) cpu.Sse.Xmm(i) = initialXmm[i];
            cpu.Sse.Mxcsr = initialMxcsr ?? SseUnit.MxcsrPowerOnDefault;
        }

        for (int i = 0; i < instruction.Length; i++)
        {
            memory.WriteByte(CodeAddress + (uint)i, instruction[i]);
        }
        for (int i = 0; i < bufferContents.Length; i++)
        {
            memory.WriteByte(bufferAddress + (uint)i, bufferContents[i]);
        }

        byte faultVector = 255;
        cpu.ProcessorException += (vector, _, _, _) => { if (faultVector == 255) faultVector = vector; };

        cpu.Step();

        var resultGpr = new uint[8];
        resultGpr[(int)Reg.Eax] = state.Eax;
        resultGpr[(int)Reg.Ecx] = state.Ecx;
        resultGpr[(int)Reg.Edx] = state.Edx;
        resultGpr[(int)Reg.Ebx] = state.Ebx;
        resultGpr[(int)Reg.Ebp] = state.Ebp;
        resultGpr[(int)Reg.Esi] = state.Esi;
        resultGpr[(int)Reg.Edi] = state.Edi;

        var resultBuffer = new byte[bufferContents.Length];
        for (int i = 0; i < resultBuffer.Length; i++)
        {
            resultBuffer[i] = memory.ReadByte(bufferAddress + (uint)i);
        }

        var resultMmx = new ulong[8];
        for (int i = 0; i < 8; i++) resultMmx[i] = cpu.Fpu.ReadMmx(i);

        var resultXmm = new XmmValue[8];
        for (int i = 0; i < 8; i++) resultXmm[i] = cpu.Sse.Xmm(i);

        return (resultGpr, cpu.Flags.Value, resultBuffer, faultVector == 255 ? null : faultVector, resultMmx, cpu.Fpu.StatusWord, cpu.Fpu.TagWord, resultXmm, cpu.Sse.Mxcsr);
    }

    /// <summary>A flat, 4 GB, big (32-bit) segment: base 0, limit 0xFFFFFFFF, present, DPL 0.</summary>
    private static void LoadFlatSegment(CpuState state, SegReg register, ushort selector, bool code)
    {
        ref SegmentCache cache = ref state.Segments[(int)register];
        cache.Selector = selector;
        cache.Base = 0;
        cache.Limit = 0xFFFFFFFF;
        cache.Big = true;
        cache.Readable = true;
        cache.Writable = !code;
        cache.ExpandDown = false;
        cache.Dpl = 0;
    }
}
