using System.Runtime.CompilerServices;

namespace Aurora386.Core.Cpu;

/// <summary>
/// The hidden part of a segment register. On x86, loading a selector copies
/// base, limit and access rights out of the descriptor into registers the
/// program cannot see; from then on every access uses those cached values, not
/// the selector. This is the mechanism behind "unreal mode" and behind the mode
/// switches Windows 3.1 performs in enhanced mode, so it has to be modelled
/// from the start even though in real mode the base is almost always just
/// selector * 16.
/// </summary>
public struct SegmentCache
{
    public ushort Selector;
    public uint Base;
    /// <summary>Last valid offset (not the size): 0xFFFF in real mode.</summary>
    public uint Limit;
    /// <summary>32-bit operands/addresses by default (D bit of the descriptor).</summary>
    public bool Big;
    public bool Writable;
    public bool Readable;
    /// <summary>Grows downwards (expand-down stack segment).</summary>
    public bool ExpandDown;
    /// <summary>Descriptor privilege level.</summary>
    public byte Dpl;
}

/// <summary>
/// Architectural state of the 386: registers, flags, segments, descriptor
/// tables and control registers.
/// </summary>
/// <summary>
/// The processor models this emulator can present. The difference that
/// matters to software is which instructions exist and which faults they
/// raise when they do not.
/// </summary>
public enum CpuModel
{
    Intel386,
    Intel486,
}

public sealed class CpuState
{
    /// <summary>
    /// The 32-bit general-purpose registers. They are kept as an array indexed
    /// by the opcode's own encoding, so the decoder can index straight in
    /// instead of switching on a register name. The EAX/AX/AH/AL overlap falls
    /// out of masking this single value: there are no separate copies that
    /// could drift out of sync.
    /// </summary>
    public readonly uint[] Gpr = new uint[8];

    public uint Eip;

    /// <summary>Segments in hardware order: ES CS SS DS FS GS.</summary>
    public readonly SegmentCache[] Segments = new SegmentCache[6];

    // --- flags ---------------------------------------------------------------
    // The arithmetic flags are computed lazily: storing the operands and
    // deriving the flags only when someone reads them avoids computing six
    // flags per instruction when most of them are never looked at. See
    // LazyFlags.
    public Flags OtherFlags = Flags.Reserved1;

    // --- control registers ----------------------------------------------------
    public uint Cr0;
    /// <summary>Faulting linear address (CR2), written by exception 14.</summary>
    public uint Cr2;
    /// <summary>Page directory base.</summary>
    public uint Cr3;

    // --- descriptor tables ----------------------------------------------------
    /// <summary>
    /// Which processor this is pretending to be.
    ///
    /// It is not decoration: a program probes for a 486 by executing one of
    /// its new instructions and catching the fault, and a machine that runs
    /// them regardless would be detected as a 486 while claiming to be a 386.
    /// </summary>
    public CpuModel Model = CpuModel.Intel486;

    public uint GdtBase;
    public ushort GdtLimit;
    public uint IdtBase;
    public ushort IdtLimit;
    public ushort Ldtr;

    /// <summary>
    /// Base and limit of the local descriptor table, taken from the descriptor
    /// when LLDT loads the selector. Caching them here saves re-reading the GDT
    /// on every segment load.
    /// </summary>
    public uint LdtBase;
    public uint LdtLimit;
    public ushort TaskRegister;

    /// <summary>
    /// Base and limit of the current task's TSS, cached when LTR loads the
    /// selector, exactly as LdtBase/LdtLimit are cached for the LDT. A stack
    /// switch on a privilege-raising interrupt has to read ESP0/SS0 out of the
    /// TSS, and it would be wasteful to walk the GDT again on every such
    /// interrupt.
    /// </summary>
    public uint TssBase;
    public uint TssLimit;

    /// <summary>
    /// Current privilege level: the ring the processor is executing in, 0 for
    /// the kernel and 3 for ordinary applications.
    ///
    /// Architecturally the CPL is not a register at all -- it *is* the low two
    /// bits of the CS selector, and the hardware simply reads them there. We
    /// keep it explicitly for one reason: virtual-8086 mode. In V86 the CS
    /// selector is a real-mode paragraph number whose low bits mean nothing,
    /// yet the processor still behaves as if it were running at ring 3, so
    /// there has to be somewhere to say so that is not the selector itself.
    /// <see cref="Cpl"/> keeps the two views reconciled.
    /// </summary>
    public byte CplRaw;

    /// <summary>
    /// The effective current privilege level. Reading the low bits of CS is
    /// right in protected mode; V86 code is always ring 3 no matter what its
    /// selectors look like; real mode has no protection at all, so everything
    /// runs with ring 0's authority.
    /// </summary>
    public byte Cpl
    {
        get
        {
            if (!ProtectedMode) return 0;
            if (Virtual8086Mode) return 3;
            return CplRaw;
        }
        set => CplRaw = (byte)(value & 3);
    }

    /// <summary>
    /// True when the VM flag (bit 17 of EFLAGS) is set and protection is on.
    ///
    /// Virtual-8086 mode exists to solve a problem the 286 could not: a
    /// protected-mode operating system that still has to run the enormous body
    /// of existing real-mode DOS software. In V86 the segment registers go back
    /// to behaving like real mode -- base = selector * 16, 64 KB limits -- so an
    /// unmodified DOS program computes the addresses it expects, but the
    /// translation happens *inside* protection: paging still applies, so the
    /// monitor can give each task its own private first megabyte, and every
    /// privileged instruction still traps, so the monitor can decide what a
    /// program is really allowed to do. EMM386 uses it to fabricate expanded
    /// memory out of paging, and Windows 3.1 in enhanced mode uses it to run
    /// several DOS boxes at once.
    /// </summary>
    public bool Virtual8086Mode { get; set; }

    /// <summary>Debug registers DR0-DR7.</summary>
    public readonly uint[] Dr = new uint[8];

    /// <summary>
    /// The test registers. Only TR6 and TR7 exist on a 386, held here as
    /// Tr[0] and Tr[1]; they drive the TLB test hardware, which this
    /// emulator's plain translation cache has no equivalent of.
    /// </summary>
    public readonly uint[] Tr = new uint[2];

    /// <summary>Halted by HLT: resumes on the next interrupt.</summary>
    public bool Halted;

    // --- CR0 bits -------------------------------------------------------------
    public const uint Cr0ProtectionEnable = 1u << 0;
    public const uint Cr0MonitorCoprocessor = 1u << 1;
    public const uint Cr0Emulation = 1u << 2;
    public const uint Cr0TaskSwitched = 1u << 3;
    public const uint Cr0ExtensionType = 1u << 4;
    public const uint Cr0Paging = 1u << 31;

    public bool ProtectedMode => (Cr0 & Cr0ProtectionEnable) != 0;
    public bool PagingEnabled => (Cr0 & Cr0Paging) != 0;

    // --- general-purpose register access --------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetReg32(int index) => Gpr[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetReg32(int index, uint value) => Gpr[index] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetReg16(int index) => (ushort)Gpr[index];

    /// <summary>
    /// Writes the low 16 bits while preserving the high ones: on x86 writing AX
    /// leaves the top half of EAX alone (unlike x86-64, where writing EAX zeroes
    /// the upper 32 bits of RAX).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetReg16(int index, ushort value)
        => Gpr[index] = (Gpr[index] & 0xFFFF0000u) | value;

    /// <summary>
    /// Reads an 8-bit register. Indices 0-3 are the low bytes of EAX..EBX, 4-7
    /// the high bytes of those same four registers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetReg8(int index)
        => index < 4 ? (byte)Gpr[index] : (byte)(Gpr[index - 4] >> 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetReg8(int index, byte value)
    {
        if (index < 4)
            Gpr[index] = (Gpr[index] & 0xFFFFFF00u) | value;
        else
            Gpr[index - 4] = (Gpr[index - 4] & 0xFFFF00FFu) | ((uint)value << 8);
    }

    // --- readable shorthands for the registers used most often -----------------

    public uint Eax { get => Gpr[0]; set => Gpr[0] = value; }
    public uint Ecx { get => Gpr[1]; set => Gpr[1] = value; }
    public uint Edx { get => Gpr[2]; set => Gpr[2] = value; }
    public uint Ebx { get => Gpr[3]; set => Gpr[3] = value; }
    public uint Esp { get => Gpr[4]; set => Gpr[4] = value; }
    public uint Ebp { get => Gpr[5]; set => Gpr[5] = value; }
    public uint Esi { get => Gpr[6]; set => Gpr[6] = value; }
    public uint Edi { get => Gpr[7]; set => Gpr[7] = value; }

    public ushort Ax { get => (ushort)Gpr[0]; set => SetReg16(0, value); }
    public ushort Cx { get => (ushort)Gpr[1]; set => SetReg16(1, value); }
    public ushort Dx { get => (ushort)Gpr[2]; set => SetReg16(2, value); }
    public ushort Bx { get => (ushort)Gpr[3]; set => SetReg16(3, value); }
    public ushort Sp { get => (ushort)Gpr[4]; set => SetReg16(4, value); }
    public ushort Bp { get => (ushort)Gpr[5]; set => SetReg16(5, value); }
    public ushort Si { get => (ushort)Gpr[6]; set => SetReg16(6, value); }
    public ushort Di { get => (ushort)Gpr[7]; set => SetReg16(7, value); }

    public ref SegmentCache Es => ref Segments[(int)SegReg.Es];
    public ref SegmentCache Cs => ref Segments[(int)SegReg.Cs];
    public ref SegmentCache Ss => ref Segments[(int)SegReg.Ss];
    public ref SegmentCache Ds => ref Segments[(int)SegReg.Ds];
    public ref SegmentCache Fs => ref Segments[(int)SegReg.Fs];
    public ref SegmentCache Gs => ref Segments[(int)SegReg.Gs];

    /// <summary>
    /// Loads a segment the way real mode does: base = selector * 16, and full
    /// access rights. In protected mode the load goes through the descriptor in
    /// the GDT/LDT instead.
    /// </summary>
    public void LoadSegmentRealMode(SegReg segment, ushort selector)
    {
        ref SegmentCache cache = ref Segments[(int)segment];
        cache.Selector = selector;
        cache.Base = (uint)selector << 4;

        // Note what is NOT reset here: the limit. A real-mode segment load
        // recomputes only the base -- selector * 16 -- and leaves the rest of
        // the hidden descriptor cache exactly as it was. Whatever limit was
        // loaded from a real descriptor survives the trip back to real mode.
        //
        // That single omission is the whole of "unreal mode" (also called big
        // real mode). A memory manager switches into protected mode for a few
        // instructions, loads a descriptor with a 4 GB limit into DS or ES,
        // switches back to real mode, and from then on addresses the entire
        // 4 GB space with ordinary real-mode instructions: the base is small
        // and recomputed from the selector, but the cached limit no longer
        // stops at 0xFFFF, so offsets above 64 KB no longer fault.
        //
        // HIMEM.SYS copies extended memory this way, and so do most DOS
        // extenders and games of the era. Resetting the limit to 0xFFFF here
        // would look harmless and would make every one of those writes fail
        // with #GP -- which is exactly why LoadSegmentRealModeReset exists as
        // a separate entry point for RESET.
        cache.Big = false;
        cache.Readable = true;
        cache.Writable = true;
        cache.ExpandDown = false;
        cache.Dpl = 0;
    }

    /// <summary>
    /// As above, but also forces the limit back to 64 KB: used by RESET and
    /// anywhere else a genuinely clean starting state is wanted.
    /// </summary>
    public void LoadSegmentRealModeReset(SegReg segment, ushort selector)
    {
        LoadSegmentRealMode(segment, selector);
        Segments[(int)segment].Limit = 0xFFFF;
    }

    /// <summary>
    /// The post-RESET state, straight out of the Intel manual: CS:IP =
    /// F000:FFF0, which combined with the hidden CS base of 0xFFFF0000 sends
    /// the very first fetch to 0xFFFFFFF0 -- the address at which the chipset
    /// aliases the BIOS ROM.
    /// </summary>
    public void Reset()
    {
        Array.Clear(Gpr);
        Array.Clear(Dr);
        Array.Clear(Tr);
        OtherFlags = Flags.Reserved1;
        Cr0 = 0;
        Cr2 = 0;
        Cr3 = 0;
        GdtBase = IdtBase = 0;
        GdtLimit = IdtLimit = 0xFFFF;
        Ldtr = TaskRegister = 0;
        LdtBase = LdtLimit = 0;
        TssBase = TssLimit = 0;
        CplRaw = 0;
        Virtual8086Mode = false;
        Halted = false;

        for (int i = 0; i < Segments.Length; i++)
            LoadSegmentRealModeReset((SegReg)i, 0);

        // CS gets a peculiar reset value: selector F000 but a base up at the
        // top of the address space, so the BIOS is reachable before any
        // descriptor has ever been loaded. The first far jump the BIOS makes
        // reloads CS normally and drops the base back down into the first
        // megabyte.
        Cs.Selector = 0xF000;
        Cs.Base = 0xFFFF0000;
        Cs.Limit = 0xFFFF;
        Eip = 0xFFF0;

        Edx = 0x00000308;   // 386 DX stepping D signature
    }
}
