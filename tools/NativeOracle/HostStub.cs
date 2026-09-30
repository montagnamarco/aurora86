namespace NativeOracle;

/// <summary>
/// Hand-assembles the Windows x64 machine code that runs one test
/// instruction on the real processor.
///
/// The whole call is straight-line: save the callee-saved registers the
/// Win64 ABI promises the caller back, move the incoming state pointer
/// (RCX, the ABI's first argument) into R15 -- the one register the test
/// instruction can never reach, because none of the templates carry a REX
/// prefix -- splice in the flags and the seven general registers from the
/// block, run the test bytes, splice them back out, and return. There is
/// never a jump: the test instruction falls through into the epilogue like
/// any other instruction would, which is exactly the constraint the
/// families are generated under (no branches, no CALL, no far transfers).
/// </summary>
public static class HostStub
{
    /// <summary>
    /// The arithmetic flags plus DF: the only EFLAGS bits the harness moves
    /// between the host and the test's initial/final state. Bit numbers from
    /// Aurora86.Core.Cpu.Flags (CF=0, PF=2, AF=4, ZF=6, SF=7, DF=10, OF=11).
    /// Every other bit -- IF, TF, IOPL, NT, RF, VM, AC, ID -- is left exactly
    /// as the host's own thread had it, both because the harness has no
    /// business changing them and because a ring-3 POPF cannot change most of
    /// them anyway.
    /// </summary>
    public const uint FlagsMask = 0x0000_0CD5;

    private const int R15 = 15;
    private const int Rax = 0, Rcx = 1, Rdx = 2, Rbx = 3, Rbp = 5, Rsi = 6, Rdi = 7;

    /// <summary>REX prefix byte: W (64-bit operand), R (reg field extension), X (SIB index extension), B (rm/base/opcode-reg extension).</summary>
    private static byte Rex(bool w, bool r, bool x, bool b)
        => (byte)(0x40 | (w ? 8 : 0) | (r ? 4 : 0) | (x ? 2 : 0) | (b ? 1 : 0));

    private static void Push64(List<byte> code, int register)
    {
        if (register >= 8)
        {
            code.Add(Rex(false, false, false, true));
        }
        code.Add((byte)(0x50 + (register & 7)));
    }

    private static void Pop64(List<byte> code, int register)
    {
        if (register >= 8)
        {
            code.Add(Rex(false, false, false, true));
        }
        code.Add((byte)(0x58 + (register & 7)));
    }

    /// <summary>`mov [r15+offset], reg32` (opcode 0x89) or `mov reg32, [r15+offset]` (0x8B).</summary>
    private static void MoveStateMemory(List<byte> code, byte opcode, int register3Bits, int offset)
    {
        code.Add(Rex(false, false, false, true));   // B=1: the base is R15.
        code.Add(opcode);
        code.Add((byte)(0x80 | (register3Bits << 3) | 7));   // mod=10, rm=R15's low 3 bits (111).
        code.AddRange(BitConverter.GetBytes(offset));
    }

    private static void LoadFromState(List<byte> code, int register3Bits, int offset) => MoveStateMemory(code, 0x8B, register3Bits, offset);
    private static void StoreToState(List<byte> code, int register3Bits, int offset) => MoveStateMemory(code, 0x89, register3Bits, offset);

    /// <summary>Group 15, `0F AE`: FXRSTOR (reg field /1) or FXSAVE (reg field /0) on `[r15+offset]`.</summary>
    private static void FxsaveOrRestore(List<byte> code, bool restore, int offset)
    {
        code.Add(Rex(false, false, false, true));
        code.Add(0x0F);
        code.Add(0xAE);
        code.Add((byte)(0x80 | ((restore ? 1 : 0) << 3) | 7));
        code.AddRange(BitConverter.GetBytes(offset));
    }

    /// <summary>
    /// Builds the full call: prologue, the caller's raw instruction bytes,
    /// epilogue. <paramref name="useExtendedState"/> is the switch a later
    /// family flips to also FXRSTOR/FXSAVE the reserved image around the
    /// test instruction (see <see cref="HostState"/>); no current family
    /// needs it, so it defaults to off and the FXSAVE path below is dead
    /// code today, kept only so the offsets are already proven correct.
    /// </summary>
    public static byte[] BuildInvocation(ReadOnlySpan<byte> testInstruction, bool useExtendedState = false)
    {
        var code = new List<byte>(64 + testInstruction.Length);

        // --- prologue: preserve every callee-saved register the Win64 ABI
        // promises the .NET caller back, in case a later family's test bytes
        // ever reach one of them (none of today's do, since no template
        // carries a REX prefix).
        Push64(code, Rbx);
        Push64(code, Rbp);
        Push64(code, Rdi);
        Push64(code, Rsi);
        Push64(code, 12);
        Push64(code, 13);
        Push64(code, 14);
        Push64(code, R15);

        // R15 <- RCX (the incoming HostState*): mov r/m64, r64, reg=RCX, rm=R15.
        code.Add(Rex(true, false, false, true));
        code.Add(0x89);
        code.Add((byte)(0xC0 | (Rcx << 3) | (R15 & 7)));

        // Splice CF/PF/AF/ZF/SF/DF/OF from the state block into the host's
        // current flags, leaving IF/TF/IOPL/... exactly as they were: the
        // instruction under test only ever reads or writes the arithmetic
        // flags and DF, and the thread's own IF or TF is none of its business.
        code.Add(0x9C);                                    // pushfq
        Pop64(code, Rax);                                  // pop rax
        code.Add(0x25);                                    // and eax, imm32
        code.AddRange(BitConverter.GetBytes(~FlagsMask));
        LoadFromState(code, Rdx, HostState.EflagsOffset);   // mov edx, [r15+Eflags]
        code.Add(0x81);                                     // and edx, imm32
        code.Add((byte)(0xC0 | (4 << 3) | Rdx));
        code.AddRange(BitConverter.GetBytes(FlagsMask));
        code.Add(0x09);                                     // or eax, edx
        code.Add((byte)(0xC0 | (Rdx << 3) | Rax));
        Push64(code, Rax);
        code.Add(0x9D);                                     // popfq

        if (useExtendedState)
        {
            FxsaveOrRestore(code, restore: true, HostState.FxsaveOffset);
        }

        // Load the seven general registers the test instruction is allowed
        // to touch. This happens last in the prologue, after RAX/RDX were
        // used as scratch above, so the test sees exactly the caller's
        // values and nothing left over from splicing the flags in.
        LoadFromState(code, Rax, HostState.EaxOffset);
        LoadFromState(code, Rcx, HostState.EcxOffset);
        LoadFromState(code, Rdx, HostState.EdxOffset);
        LoadFromState(code, Rbx, HostState.EbxOffset);
        LoadFromState(code, Rbp, HostState.EbpOffset);
        LoadFromState(code, Rsi, HostState.EsiOffset);
        LoadFromState(code, Rdi, HostState.EdiOffset);

        // --- the test instruction itself.
        code.AddRange(testInstruction.ToArray());

        // --- epilogue: capture the result before touching anything else.
        StoreToState(code, Rax, HostState.EaxOffset);
        StoreToState(code, Rcx, HostState.EcxOffset);
        StoreToState(code, Rdx, HostState.EdxOffset);
        StoreToState(code, Rbx, HostState.EbxOffset);
        StoreToState(code, Rbp, HostState.EbpOffset);
        StoreToState(code, Rsi, HostState.EsiOffset);
        StoreToState(code, Rdi, HostState.EdiOffset);

        if (useExtendedState)
        {
            FxsaveOrRestore(code, restore: false, HostState.FxsaveOffset);
        }

        code.Add(0x9C);                                     // pushfq
        Pop64(code, Rax);
        StoreToState(code, Rax, HostState.EflagsOffset);     // mov [r15+Eflags], eax (low 32 of the popped value)

        // The Win64 ABI expects DF clear on return, whatever the test left it as.
        code.Add(0xFC);                                      // cld

        Pop64(code, R15);
        Pop64(code, 14);
        Pop64(code, 13);
        Pop64(code, 12);
        Pop64(code, Rsi);
        Pop64(code, Rdi);
        Pop64(code, Rbp);
        Pop64(code, Rbx);
        code.Add(0xC3);                                      // ret

        return code.ToArray();
    }
}
