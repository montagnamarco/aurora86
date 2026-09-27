// The Pentium Pro/Pentium II's (P6) own instructions: CMOVcc, RDPMC,
// SYSENTER/SYSEXIT, and the integer-unit half of FXSAVE/FXRSTOR (the x87
// register-file half lives in Fpu/X87Unit.cs, reached the same way the
// ordinary FSAVE/FRSTOR escape handlers in Cpu.Fpu.cs already reach it).
// UD2 and the reserved-NOP encodings are one-liners and stay in
// Cpu.TwoByte.cs's dispatch switch, next to the opcodes they sit between.
// Dispatched from that same switch; sources are plans/specs/p6.md (the
// hardware-researcher's report and the coordinator's decisions on it) --
// Intel SDM Vol.2A (CMOVcc, RDPMC, FXSAVE/FXRSTOR), Vol.2B (SYSENTER/
// SYSEXIT) and Geoff Chappell's km/cpu/sep.htm (the "TF is not cleared"
// finding SYSENTER's own comment cites).

using Aurora386.Core.Cpu.Fpu;

namespace Aurora386.Core.Cpu;

public sealed partial class Cpu
{
    // === CMOVcc (0F 40-4F) ====================================================

    /// <summary>
    /// CMOVcc: the r/m source is always read -- a memory operand's segment
    /// or page fault happens whether or not the move actually goes ahead --
    /// and only the destination register, never any flag, changes when the
    /// condition is false. <paramref name="condition"/> is the low nibble of
    /// the opcode, the same code <see cref="TestCondition"/> already serves
    /// Jcc and SETcc with.
    /// </summary>
    private void ExecuteCmovcc(byte condition)
    {
        RequireFeature(CpuFeature.Cmov);

        var m = DecodeModRm();
        uint value = ReadRm(m);

        if (TestCondition(condition))
        {
            WriteRegOperand(m, value);
        }
    }

    // === RDPMC (0F 33) =========================================================

    /// <summary>
    /// RDPMC: ring 0 may always read either counter; anywhere else needs
    /// CR4.PCE (checked through the raw bit rather than a Cpl helper, since
    /// virtual-8086 code is ring 3 here exactly as everywhere else that
    /// tests <see cref="CpuState.Cpl"/>). ECX outside 0/1 is <c>#GP(0)</c> --
    /// there are only two counters, PERFCTR0 and PERFCTR1 (MSR C1h/C2h),
    /// both inert like every performance-monitoring register this emulator
    /// carries. EDX's upper bits read zero because
    /// <see cref="ModelSpecificRegisters.PerfCounter0"/>/
    /// <see cref="ModelSpecificRegisters.PerfCounter1"/> are already masked
    /// to 40 bits.
    /// </summary>
    private void ExecuteRdpmc()
    {
        RequireFeature(CpuFeature.Pmc);

        if (State.Cpl != 0 && (State.Cr4 & CpuState.Cr4Pce) == 0)
        {
            throw new CpuException(13, 0);
        }

        if (State.Ecx > 1)
        {
            throw new CpuException(13, 0);
        }

        // The P6 counts in PERFCTR0/1 (C1h, C2h); the Pentium with MMX in the
        // same two 40-bit counters it has always had, CTR0/CTR1 (12h, 13h).
        bool familySix = State.Model >= CpuModel.PentiumII;
        bool firstCounter = State.Ecx == 0;
        ulong counter;

        if (familySix)
        {
            counter = firstCounter ? Msrs.PerfCounter0 : Msrs.PerfCounter1;
        }
        else
        {
            counter = firstCounter ? Msrs.Counter0 : Msrs.Counter1;
        }
        State.Eax = (uint)counter;
        State.Edx = (uint)(counter >> 32);
    }

    // === SYSENTER / SYSEXIT (0F 34 / 0F 35) ====================================

    /// <summary>
    /// SYSENTER: the fast system-call entry a ring-3 caller executes
    /// directly, without an INT/gate lookup. Both caches it loads are
    /// synthesised outright -- flat, 32-bit, DPL 0 -- rather than read from
    /// the GDT (plans/specs/p6.md item 4), which is exactly why the MSR
    /// holds only a selector's worth of bits and not a full descriptor:
    /// there is no descriptor to fetch. Chappell's km/cpu/sep.htm is the
    /// source for the one surprise, that TF survives SYSENTER even though
    /// IF, RF and VM do not.
    /// </summary>
    private void ExecuteSysenter()
    {
        RequireFeature(CpuFeature.Sep);

        if (!State.ProtectedMode || State.Virtual8086Mode)
        {
            throw new CpuException(13, 0);
        }

        ushort sysenterCs = (ushort)Msrs.SysenterCs;
        if ((sysenterCs & 0xFFFC) == 0)
        {
            throw new CpuException(13, 0);
        }

        ushort cs = (ushort)(sysenterCs & 0xFFFC);
        ushort ss = (ushort)(cs + 8);

        LoadSyntheticFlatSegment(ref State.Cs, cs, dpl: 0, writable: false);
        LoadSyntheticFlatSegment(ref State.Ss, ss, dpl: 0, writable: true);
        State.Cpl = 0;

        // VM is already false (checked above); IF and RF go; TF does not
        // (Chappell -- see the type summary).
        Flags.Interrupt = false;
        Flags.Stored &= ~Aurora386.Core.Cpu.Flags.Resume;

        State.Eip = (uint)Msrs.SysenterEip;
        State.Esp = (uint)Msrs.SysenterEsp;
    }

    /// <summary>
    /// SYSEXIT: the return half of <see cref="ExecuteSysenter"/>, callable
    /// only from the ring 0 handler SYSENTER (or an ordinary call) reached.
    /// Both caches land at DPL 3 this time, offset from SYSENTER_CS by fixed
    /// amounts that assume the four descriptors the BIOS Writer's Guide
    /// convention lays out consecutively: SYSENTER_CS (kernel code),
    /// SYSENTER_CS+8 (kernel stack), SYSENTER_CS+16 (user code),
    /// SYSENTER_CS+24 (user stack).
    /// </summary>
    private void ExecuteSysexit()
    {
        RequireFeature(CpuFeature.Sep);

        if (!State.ProtectedMode || State.Cpl != 0)
        {
            throw new CpuException(13, 0);
        }

        ushort sysenterCs = (ushort)Msrs.SysenterCs;
        if ((sysenterCs & 0xFFFC) == 0)
        {
            throw new CpuException(13, 0);
        }

        ushort cs = (ushort)((sysenterCs + 16) | 3);
        ushort ss = (ushort)((sysenterCs + 24) | 3);

        LoadSyntheticFlatSegment(ref State.Cs, cs, dpl: 3, writable: false);
        LoadSyntheticFlatSegment(ref State.Ss, ss, dpl: 3, writable: true);
        State.Cpl = 3;

        State.Eip = State.Edx;
        State.Esp = State.Ecx;
    }

    /// <summary>
    /// Installs a flat, 32-bit, page-granular segment cache directly,
    /// without reading any descriptor -- the whole point of SYSENTER/SYSEXIT
    /// being that neither one ever touches the GDT.
    /// </summary>
    private static void LoadSyntheticFlatSegment(ref SegmentCache cache, ushort selector, byte dpl, bool writable)
    {
        cache.Selector = selector;
        cache.Base = 0;
        cache.Limit = 0xFFFFFFFF;
        cache.Big = true;
        cache.Readable = true;
        cache.Writable = writable;
        cache.ExpandDown = false;
        cache.Dpl = dpl;
    }

    // === FXSAVE / FXRSTOR (0F AE /0, /1) =======================================
    //
    // The 512-byte image's field offsets, named the way Intel's own FXSAVE
    // reference page lays them out (SDM Vol.2A; plans/specs/p6.md item 9).
    // Aurora has no SSE behind this silicon yet (Pentium II predates it), so
    // the XMM area and MXCSR are the fixed values a part without SSE is
    // documented to show -- real state from the day SSE arrives (P4.1).

    private const int FxsaveImageSize = 512;
    private const int FxsaveControlWordOffset = 0;
    private const int FxsaveStatusWordOffset = 2;
    private const int FxsaveAbridgedTagOffset = 4;      // + 1 reserved byte at 5
    private const int FxsaveOpcodeOffset = 6;
    private const int FxsaveInstructionPointerOffset = 8;
    private const int FxsaveInstructionSelectorOffset = 12;   // + 2 reserved bytes at 14
    private const int FxsaveDataPointerOffset = 16;
    private const int FxsaveDataSelectorOffset = 20;          // + 2 reserved bytes at 22
    private const int FxsaveMxcsrOffset = 24;
    private const int FxsaveMxcsrMaskOffset = 28;
    private const int FxsaveRegisterAreaOffset = 32;
    private const int FxsaveRegisterStride = 16;              // 10 bytes used, 6 reserved
    private const int FxsaveRegisterCount = 8;
    private const int FxsaveXmmAreaOffset = 160;
    private const int FxsaveXmmCount = 8;                     // legacy (non-64-bit) mode: XMM0-7 only
    private const int FxsaveReservedOffset = FxsaveXmmAreaOffset + FxsaveXmmCount * FxsaveRegisterStride;   // 288
    private const int FxsaveSoftwareAreaOffset = 464;

    /// <summary>The power-on MXCSR (SDM Vol.1 §11.6.4), written even without SSE behind it -- software that reads FXSAVE's image before checking CPUID.SSE would otherwise find a plausible-looking but wrong value.</summary>
    private const uint MxcsrPowerOnDefault = 0x1F80;

    /// <summary>
    /// FXSAVE: memory only (a register operand is <c>#UD</c> on this
    /// silicon, unlike the SSE-era encoding that later reuses mod=11 for
    /// XSAVEOPT), gated by CPUID.FXSR rather than CR4.OSFXSR, and requiring
    /// 16-byte alignment on pain of <c>#GP(0)</c> -- checked in that order,
    /// after the <c>#NM</c> that CR0.EM and CR0.TS both raise
    /// (plans/specs/p6.md item 9).
    /// </summary>
    private void ExecuteFxsave(in ModRm m)
    {
        if (m.IsRegister)
        {
            throw new CpuException(6);
        }

        // CR0.EM and CR0.TS both give #NM for FXSAVE and FXRSTOR, the x87
        // rule; only the later SSE instructions take #UD for EM (SDM FXSAVE
        // and FXRSTOR, protected-mode exceptions).
        const uint deviceNotAvailableBits = CpuState.Cr0Emulation | CpuState.Cr0TaskSwitched;

        if ((State.Cr0 & deviceNotAvailableBits) != 0)
        {
            FaultNow(7, 0);
            return;
        }

        if ((LinearAddress(m.Segment, m.Offset) & 0xF) != 0)
        {
            throw new CpuException(13, 0);
        }

        SegReg segment = m.Segment;
        uint address = m.Offset;

        ValidateFxsaveArea(segment, address, write: true);

        WriteMem16(segment, address + FxsaveControlWordOffset, Fpu.ControlWord);
        WriteMem16(segment, address + FxsaveStatusWordOffset, Fpu.StatusWord);
        // The abridged tag occupies only the low byte; the reserved byte
        // above it is the high byte of this same word, so one write covers
        // both.
        WriteMem16(segment, address + FxsaveAbridgedTagOffset, Fpu.AbridgedTagWord);

        // FOP/FIP/FCS/FDP/FDS: zero, exactly as FSAVE's own StoreEnvironment
        // writes them (Cpu.Fpu.cs) -- nothing here ever records the last
        // non-control instruction's pointer.
        WriteMem16(segment, address + FxsaveOpcodeOffset, 0);
        WriteMem32(segment, address + FxsaveInstructionPointerOffset, 0);
        WriteMem32(segment, address + FxsaveInstructionSelectorOffset, 0);
        WriteMem32(segment, address + FxsaveDataPointerOffset, 0);
        WriteMem32(segment, address + FxsaveDataSelectorOffset, 0);

        // MXCSR/MXCSR_MASK: the fixed values a part without SSE shows (see
        // the type summary on MxcsrPowerOnDefault). MXCSR_MASK 0 is the
        // documented "assume nothing beyond the default" signal.
        WriteMem32(segment, address + FxsaveMxcsrOffset, MxcsrPowerOnDefault);
        WriteMem32(segment, address + FxsaveMxcsrMaskOffset, 0);

        // ST/MM0-7, in stack order (ST(0) first) exactly like FSAVE's own
        // register area -- ten bytes used out of each sixteen-byte slot.
        for (int i = 0; i < FxsaveRegisterCount; i++)
        {
            uint registerAddress = address + FxsaveRegisterAreaOffset + (uint)(i * FxsaveRegisterStride);
            WriteRaw(segment, registerAddress, Fpu.GetRaw(i));
            WriteMem32(segment, registerAddress + 10, 0);
            WriteMem16(segment, registerAddress + 14, 0);
        }

        // XMM0-7: no SSE state exists behind this silicon, so the area is
        // simply zero (plans/specs/p6.md's coordinator decision).
        for (int i = 0; i < FxsaveXmmCount; i++)
        {
            uint xmmAddress = address + FxsaveXmmAreaOffset + (uint)(i * FxsaveRegisterStride);
            WriteMem32(segment, xmmAddress, 0);
            WriteMem32(segment, xmmAddress + 4, 0);
            WriteMem32(segment, xmmAddress + 8, 0);
            WriteMem32(segment, xmmAddress + 12, 0);
        }

        // The reserved tail up to byte 463 is written zero for determinism.
        // Bytes 464-511 are the software-available area, which the processor
        // never writes (SDM FXSAVE), so they are left as the guest had them.
        for (uint offset = FxsaveReservedOffset; offset < FxsaveSoftwareAreaOffset; offset += 4)
        {
            WriteMem32(segment, address + offset, 0);
        }
    }

    /// <summary>
    /// The 512-byte operand is one memory operand: it is checked against the
    /// segment limit as a whole, and both pages it can touch -- a 16-byte
    /// aligned image crosses at most one page boundary -- are translated, for
    /// writing when <paramref name="write"/> says so, before anything is read
    /// or written. A fault then leaves memory and the FPU as they were.
    /// </summary>
    private void ValidateFxsaveArea(SegReg segment, uint offset, bool write)
    {
        CheckLimit(segment, offset, FxsaveImageSize);

        uint linear = LinearAddress(segment, offset);
        uint lastLinear = linear + FxsaveImageSize - 1;

        TranslateLinear(linear, write);
        TranslateLinear(lastLinear, write);
    }

    /// <summary>
    /// FXRSTOR: the reverse of <see cref="ExecuteFxsave"/>. The abridged tag
    /// is expanded back into a full two-bit tag word -- empty stays empty,
    /// anything else becomes "valid" and lets <see cref="X87Unit.RestoreState"/>
    /// re-derive the real tag from the register's own bits the next time
    /// anything asks, exactly as it already does for ordinary FRSTOR
    /// (plans/specs/p6.md item 9: "FXRSTOR rebuilds the 2-bit tags from the
    /// register contents"). MXCSR/MXCSR_MASK are read and discarded: there
    /// is no SSE state here for them to govern yet.
    /// </summary>
    private void ExecuteFxrstor(in ModRm m)
    {
        if (m.IsRegister)
        {
            throw new CpuException(6);
        }

        // CR0.EM and CR0.TS both give #NM for FXSAVE and FXRSTOR, the x87
        // rule; only the later SSE instructions take #UD for EM (SDM FXSAVE
        // and FXRSTOR, protected-mode exceptions).
        const uint deviceNotAvailableBits = CpuState.Cr0Emulation | CpuState.Cr0TaskSwitched;

        if ((State.Cr0 & deviceNotAvailableBits) != 0)
        {
            FaultNow(7, 0);
            return;
        }

        if ((LinearAddress(m.Segment, m.Offset) & 0xF) != 0)
        {
            throw new CpuException(13, 0);
        }

        SegReg segment = m.Segment;
        uint address = m.Offset;

        ValidateFxsaveArea(segment, address, write: false);

        ushort control = ReadMem16(segment, address + FxsaveControlWordOffset);
        ushort status = ReadMem16(segment, address + FxsaveStatusWordOffset);
        byte abridgedTag = (byte)ReadMem16(segment, address + FxsaveAbridgedTagOffset);

        int top = (status >> 11) & 7;
        var registers = new Raw80[FxsaveRegisterCount];
        for (int i = 0; i < FxsaveRegisterCount; i++)
        {
            uint registerAddress = address + FxsaveRegisterAreaOffset + (uint)(i * FxsaveRegisterStride);
            registers[(top + i) & 7] = ReadRaw(segment, registerAddress);
        }

        const int emptyTag = 3;
        ushort tags = 0;
        for (int physical = 0; physical < FxsaveRegisterCount; physical++)
        {
            bool notEmpty = ((abridgedTag >> physical) & 1) != 0;
            if (!notEmpty)
            {
                tags |= (ushort)(emptyTag << (physical * 2));
            }
        }

        Fpu.RestoreState(control, status, tags, registers);
    }
}
