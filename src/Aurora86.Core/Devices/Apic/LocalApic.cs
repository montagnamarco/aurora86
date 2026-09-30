using System.Numerics;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Memory;
using Aurora86.Core.Timing;

namespace Aurora86.Core.Devices.Apic;

/// <summary>
/// The processor-local APIC (xAPIC; x2APIC is out of scope, coordinator
/// decision A10 in plans/specs/apic.md). One instance sits in front of the
/// 8259 pair every existing machine already had, wrapping rather than
/// replacing it.
///
/// <b>Coordinator ruling (independent review, item 7, revising decision
/// A1):</b> this unit's own interrupts -- its timer, its error LVT, a
/// self-targeted ICR send, and anything an I/O APIC delivers into its IRR --
/// are posted whenever the unit is present, hardware-enabled and
/// software-enabled (SVR bit 8), <i>regardless of the IMCR</i>. The legacy
/// 8259 pair reaches the CPU two different ways depending on the IMCR: with
/// IMCR=0 it drives the CPU directly, exactly as it always has, whatever
/// LINT0's LVT holds (the "PIC mode" wiring: the 8259's INTR output is not
/// even connected to this unit's own logic in that mode); with IMCR=1 it
/// only reaches the CPU through LINT0, and only when LINT0 is unmasked and
/// programmed for ExtINT delivery (the "virtual wire" path). When both this
/// unit's own IRR and the legacy path have something pending at once, the
/// own-IRR vector goes first if its class beats PPR -- exactly the ordinary
/// priority rule already applies to any other IRR vector, extended to treat
/// "the 8259 has something to say" as the lowest-priority source there is.
///
/// With IMCR=0 and every register at its power-on value (software-disabled,
/// LINT0 masked) nothing here ever changes what the 8259 alone would have
/// delivered: <see cref="HasPendingInterrupt"/> and <see
/// cref="GetPendingVector"/> reduce to the wrapped 8259's own answer, which
/// is what keeps every machine built before this package byte-identical --
/// nothing before P4.4 ever touched the IMCR, SVR or any LVT.
///
/// Once posted, delivery works exactly as the SDM's APIC chapter describes:
/// 256-bit interrupt/in-service/trigger-mode bitmaps, a task-priority
/// register, EOI that clears the highest in-service bit and (for a
/// level-triggered vector) tells every I/O APIC to clear its Remote IRR, and
/// a one-shot/periodic timer driven by the same <see cref="Timing.Scheduler"/>
/// the PIIX4 PM timer already uses.
///
/// <b>Software disable (independent review, item 12, spec §1.3):</b> while
/// SVR bit 8 is clear, every LVT register reads with its mask bit forced to
/// 1 and a write can never clear that bit in storage either -- so a guest
/// that unmasks LINT0 for ExtINT delivery before enabling SVR gets nothing
/// for it, and the ordinary "SVR enable, then unmask LINT0" sequence is the
/// only one that works. The IMCR=0 direct path is entirely unaffected: it
/// does not go through this unit's LVTs at all.
///
/// IPIs to another logical CPU, and SMI/INIT/STARTUP delivery beyond storing
/// whatever was written to the ICR, are out of scope (decision A10): this
/// package models one CPU. So is delivering an actual non-maskable interrupt
/// to the processor core for LVT LINT1 or an ICR NMI send -- <see
/// cref="Cpu.Cpu"/> has no NMI entry point at all yet (no code path forces
/// vector 2 the way a real NMI pin does), and no test case in this package's
/// contract exercises one, so LINT1 and ICR-NMI are accepted and stored
/// (readable back, no <c>#GP</c>) without being wired to anything that would
/// actually interrupt the guest. That is a real gap for the SMM/NMI work a
/// later package would need, not a silent pretence that it works.
/// </summary>
public sealed class LocalApic : IResettableInterruptSource, IMemoryDevice, ITaskPriority
{
    // ---- MMIO register offsets (Intel SDM vol.3, APIC chapter; 82093AA's
    // sibling xAPIC table). Sixteen bytes apart, thirty-two significant bits
    // each -- see plans/specs/apic.md §1.2 for the full register table this
    // mirrors.
    private const uint IdOffset = 0x020;
    private const uint VersionOffset = 0x030;
    private const uint TprOffset = 0x080;
    private const uint AprOffset = 0x090;
    private const uint PprOffset = 0x0A0;
    private const uint EoiOffset = 0x0B0;
    private const uint RrdOffset = 0x0C0;
    private const uint LdrOffset = 0x0D0;
    private const uint DfrOffset = 0x0E0;
    private const uint SvrOffset = 0x0F0;
    private const uint IsrBase = 0x100;
    private const uint TmrBase = 0x180;
    private const uint IrrBase = 0x200;
    private const uint EsrOffset = 0x280;
    private const uint IcrLowOffset = 0x300;
    private const uint IcrHighOffset = 0x310;
    private const uint LvtTimerOffset = 0x320;
    private const uint LvtThermalOffset = 0x330;
    private const uint LvtPerfMonOffset = 0x340;
    private const uint LvtLint0Offset = 0x350;
    private const uint LvtLint1Offset = 0x360;
    private const uint LvtErrorOffset = 0x370;
    private const uint InitialCountOffset = 0x380;
    private const uint CurrentCountOffset = 0x390;
    private const uint DivideConfigOffset = 0x3E0;

    /// <summary>The MMIO page is always this size, wherever APIC_BASE puts it.</summary>
    public const uint PageSize = 0x1000;

    // ---- register field layout -------------------------------------------

    private const uint IdFieldMask = 0xFF000000;
    private const uint VersionValuePentiumMmx = 0x00030011;   // 4 LVT entries (no PerfMon)
    private const uint VersionValueP6 = 0x00040011;           // 5 LVT entries
    private const uint VersionValueP4Plus = 0x00050014;       // 6 LVT entries, xAPIC version 14h
    private const uint SvrResetValue = 0x000000FF;
    private const uint SvrSoftwareEnableBit = 1u << 8;

    /// <summary>SVR bits 3:0 are hardwired to 1 on the silicon this reset value describes (plans/specs/apic.md §1.2); a write can move every other bit but never these four.</summary>
    private const uint SvrHardwiredLowNibble = 0xF;

    private const uint LvtResetValue = 0x00010000;
    private const uint LvtMaskBit = 1u << 16;
    private const uint LvtTimerPeriodicBit = 1u << 17;
    private const uint LvtDeliveryModeMask = 0x700;
    private const uint LvtDeliveryModeExtInt = 0x700;   // bits 10:8 = 111
    private const uint LvtVectorMask = 0xFF;

    private const uint IcrVectorMask = 0xFF;
    private const uint IcrDeliveryModeMask = 0x700;
    private const uint IcrDeliveryModeFixed = 0x000;
    private const uint IcrDeliveryModeNmi = 0x400;
    private const uint IcrDeliveryStatusBit = 1u << 12;   // RO; always reads 0 (decision A6: delivery is synchronous)
    private const uint IcrShorthandMask = 3u << 18;
    private const uint IcrShorthandNone = 0u << 18;
    private const uint IcrShorthandSelf = 1u << 18;
    private const uint IcrShorthandAllIncludingSelf = 2u << 18;
    private const uint IcrLowWritableMask = 0x000CCFFF;   // vector, mode, dest mode, level, trigger, shorthand

    private const int LvtTimerIndex = 0;
    private const int LvtThermalIndex = 1;
    private const int LvtPerfMonIndex = 2;
    private const int LvtLint0Index = 3;
    private const int LvtLint1Index = 4;
    private const int LvtErrorIndex = 5;
    private const int LvtCount = 6;

    /// <summary>Divide Config Register: bits {3,1,0} name divisors 2,4,8,16,32,64,128,1, in that order (plans/specs/apic.md §1.2).</summary>
    private static readonly int[] DivideValuesByCode = [2, 4, 8, 16, 32, 64, 128, 1];

    // ---- dependencies -------------------------------------------------------

    private readonly IInterruptSource _pic;
    private readonly Cpu.Cpu _cpu;
    private readonly PhysicalMemoryBus _memory;
    private readonly Scheduler _scheduler;
    private readonly Func<long> _cyclesNow;

    // ---- register storage -----------------------------------------------

    private uint _id;
    private uint _tpr;

    /// <summary>
    /// CR8's view of the TPR (<see cref="ITaskPriority"/>): the priority
    /// class in bits 7:4. A write through CR8 sets bits 7:4 and clears the
    /// sub-class bits 3:0, as the SDM's CR8-to-TPR mapping describes.
    /// </summary>
    public int TaskPriorityClass
    {
        get => (int)((_tpr >> 4) & 0xF);
        set => _tpr = (uint)((value & 0xF) << 4);
    }
    private uint _ldr;
    private uint _dfr = 0xFFFFFFFF;
    private uint _svr = SvrResetValue;
    private readonly uint[] _isr = new uint[8];
    private readonly uint[] _tmr = new uint[8];
    private readonly uint[] _irr = new uint[8];
    private uint _esr;
    private uint _icrLow;
    private uint _icrHigh;
    private readonly uint[] _lvt = new uint[LvtCount];
    private uint _initialCount;
    private uint _dcr;

    // ---- timer scheduling -------------------------------------------------

    private ScheduledEvent? _timerEvent;
    private long _timerStartCycle;

    // ---- IMCR and hardware state -------------------------------------------

    /// <summary>
    /// Set by the chipset's IMCR (ports 22h/23h, i430TX/PIIX4 only): true
    /// once the guest has written 1, meaning the legacy 8259 pair no longer
    /// drives the CPU directly by default -- see <see cref="LegacyPending"/>
    /// for the full rule, which also has to consult whether this unit is
    /// itself present and enabled (independent review, item N7) before it
    /// can rely on this flag at all. A board with no IMCR at all (i430FX)
    /// never touches this, so it stays false for the machine's whole life
    /// and the legacy pair always reaches the CPU directly -- the
    /// byte-identical behaviour coordinator decision A1 asks for. This unit's
    /// own interrupts (its timer, an I/O APIC's Fixed-mode deliveries) are
    /// entirely independent of this flag either way.
    /// </summary>
    public bool ImcrApicMode { get; set; }

    private bool _hardwareEnabled;
    private uint? _mappedBase;

    /// <summary>Raised when EOI clears a level-triggered (TMR) vector, so a wired I/O APIC can clear the matching Remote IRR bit -- a direct call standing in for the APIC bus (plans/specs/apic.md §1.3).</summary>
    public event Action<byte>? LevelTriggeredEoi;

    public LocalApic(IInterruptSource pic, Cpu.Cpu cpu, PhysicalMemoryBus memory, Scheduler scheduler, Func<long> cyclesNow)
    {
        _pic = pic;
        _cpu = cpu;
        _memory = memory;
        _scheduler = scheduler;
        _cyclesNow = cyclesNow;
    }

    // ---- IInterruptSource: what Cpu.CheckInterrupts asks ------------------

    /// <summary>
    /// True when this unit's own IRR has a vector above PPR (posted whenever
    /// present, hardware- and software-enabled, independently of the IMCR --
    /// item 7's ruling), or when the legacy 8259 path is currently live and
    /// has something (see <see cref="LegacyPending"/> for which path that
    /// is). At the reset state (software-disabled, LINT0 masked, IMCR=0)
    /// this always reduces to the 8259's own answer.
    /// </summary>
    public bool HasPendingInterrupt => PeekOwnVector() >= 0 || LegacyPending();

    /// <summary>
    /// The vector to service: this unit's own IRR first if it has something
    /// above PPR (item 7: "LAPIC vectors above PPR first"), otherwise the
    /// legacy path's own vector.
    /// </summary>
    public int GetPendingVector()
    {
        int own = DeliverOwnVector();
        if (own >= 0)
        {
            return own;
        }

        return LegacyPending() ? _pic.GetPendingVector() : -1;
    }

    /// <summary>
    /// Whether the legacy 8259 pair currently has a path to the CPU at all,
    /// and whether it has something pending on it.
    ///
    /// IMCR=0 wires it straight to the CPU, bypassing this unit's LVTs
    /// entirely -- the "PIC mode" board wiring, and the branch that keeps
    /// every existing machine byte-identical, since IMCR starts at 0 and
    /// nothing before this package ever changed it. A hardware-disabled (or
    /// absent) unit gets the same direct wiring regardless of what the IMCR
    /// holds (independent review, item N7): with the whole local APIC
    /// powered off there is no LINT0 logic left to route through, so LINT0
    /// reverts to being a plain INTR pin exactly as it is in PIC mode -- a
    /// guest that hardware-disables its own APIC after switching to
    /// symmetric mode is not a sequence any cited source describes, but it
    /// must not lose the timer tick either.
    ///
    /// Otherwise (IMCR=1, unit present and enabled) the legacy pair reaches
    /// the CPU only through an ExtINT-programmed, unmasked path: LINT0 (the
    /// "virtual wire via the local APIC") or an I/O APIC redirection entry
    /// (<see cref="SetIoApicExtIntActive"/>, "virtual wire via the I/O
    /// APIC") -- and per item 12, LINT0 can only ever have been unmasked
    /// while SVR was software-enabled (see <see cref="IsLvtMasked"/>).
    /// </summary>
    private bool LegacyPending()
    {
        if (!ImcrApicMode || !ApicPresent || !_hardwareEnabled)
        {
            return _pic.HasPendingInterrupt;
        }

        bool lint0ExtInt = (_lvt[LvtLint0Index] & LvtDeliveryModeMask) == LvtDeliveryModeExtInt
            && !IsLvtMasked(LvtLint0Index);
        return (lint0ExtInt || _ioApicExtIntActive) && _pic.HasPendingInterrupt;
    }

    /// <summary>
    /// Whether an I/O APIC redirection entry is currently unmasked and
    /// programmed for ExtINT delivery -- "virtual wire via the I/O APIC"
    /// (plans/specs/apic.md §1.4; independent review, item N2). Pushed by
    /// <c>IoApic</c> whenever its own redirection table changes, rather than
    /// asked for on demand, so this class does not need a reference back to
    /// every I/O APIC that might exist.
    ///
    /// Deliberately kept out of this unit's own IRR/ISR/PPR machinery: an
    /// ExtINT entry's own vector field is meaningless (the 8259 supplies the
    /// real one at acceptance, in <see cref="GetPendingVector"/>, through
    /// <see cref="LegacyPending"/> above -- never at the edge that sets this
    /// flag, and never when this unit itself has nothing to accept it with),
    /// so it is exempt from TPR/PPR and needs no LAPIC-side EOI, only the
    /// 8259's own -- exactly like LINT0's own ExtINT path.
    /// </summary>
    private bool _ioApicExtIntActive;

    public void SetIoApicExtIntActive(bool active) => _ioApicExtIntActive = active;

    private bool ApicPresent => _cpu.State.Features.Has(CpuFeature.Apic);

    /// <summary>
    /// Whether an LVT entry is masked for delivery purposes: its own stored
    /// mask bit, or forced masked while SVR is software-disabled (item 12) --
    /// a live view of the current SVR state, independent of what was last
    /// written, exactly as spec §1.3 describes ("all LVT entries read as
    /// masked" while disabled).
    /// </summary>
    private bool IsLvtMasked(int lvtIndex)
        => (_lvt[lvtIndex] & LvtMaskBit) != 0 || (_svr & SvrSoftwareEnableBit) == 0;

    /// <summary>
    /// The highest IRR vector whose class beats PPR, or -1. A read-only peek:
    /// unlike <see cref="DeliverOwnVector"/> it never moves anything from IRR
    /// to ISR, which is what lets <see cref="HasPendingInterrupt"/> answer
    /// without committing to deliver.
    /// </summary>
    private int PeekOwnVector()
    {
        // Coordinator ruling (item 7): posted only when the unit exists,
        // is hardware-enabled, and is software-enabled. A model with no
        // on-die APIC, or one whose MSR has hardware-disabled it, never
        // reaches this path at all on a real machine -- its MMIO page
        // simply is not mapped, so SVR could never have been written in
        // the first place; this check is what keeps that true for a bare
        // instance a test drives directly, without going through memory.
        if (!ApicPresent || !_hardwareEnabled)
        {
            return -1;
        }

        // Software-disabled (SVR bit 8 = 0): fixed and lowest-priority
        // interrupts stay latched in IRR but are never posted
        // (plans/specs/apic.md §1.3).
        if ((_svr & SvrSoftwareEnableBit) == 0)
        {
            return -1;
        }

        int candidate = HighestSetBit(_irr);
        if (candidate < 0)
        {
            return -1;
        }

        byte candidateClass = (byte)(candidate & 0xF0);
        byte pprClass = (byte)(ComputePpr() & 0xF0);
        return candidateClass > pprClass ? candidate : -1;
    }

    /// <summary>Moves the winning IRR bit to ISR and returns its vector, or -1 when nothing outranks PPR.</summary>
    private int DeliverOwnVector()
    {
        int vector = PeekOwnVector();
        if (vector < 0)
        {
            return -1;
        }

        ClearBit(_irr, vector);
        SetBit(_isr, vector);
        return vector;
    }

    /// <summary>SDM 10.8.6.1: PPR is TPR itself when TPR's class is at least the highest in-service class, otherwise that class with sub-class zero.</summary>
    private byte ComputePpr()
    {
        int isrVector = HighestSetBit(_isr);
        byte isrClass = isrVector < 0 ? (byte)0 : (byte)(isrVector & 0xF0);
        byte tprClass = (byte)(_tpr & 0xF0);
        return tprClass >= isrClass ? (byte)(_tpr & 0xFF) : isrClass;
    }

    // ---- delivery entry points used by the I/O APIC and by self-targeted ICR sends ----

    /// <summary>
    /// Sets this vector's IRR bit (and, for a level source, its TMR bit so
    /// EOI knows to notify the I/O APIC). Called directly by <see
    /// cref="IoApic"/> and by <see cref="WriteIcrLow"/> for a self/all-
    /// including-self fixed send -- there is no serial APIC bus to model
    /// (plans/specs/apic.md §1.3), so delivery is simply this method call.
    /// </summary>
    public void DeliverFixed(byte vector, bool levelTriggered)
    {
        SetBit(_irr, vector);
        if (levelTriggered) SetBit(_tmr, vector); else ClearBit(_tmr, vector);
    }

    // ---- MMIO register access (IMemoryDevice) -----------------------------

    public byte ReadByte(uint offset) => (byte)(ReadDword(offset & ~3u) >> (int)((offset & 3) * 8));

    public void WriteByte(uint offset, byte value)
    {
        uint aligned = offset & ~3u;
        int shift = (int)((offset & 3) * 8);
        uint current = ReadDword(aligned);
        uint merged = (current & ~(0xFFu << shift)) | ((uint)value << shift);
        WriteDword(aligned, merged);
    }

    public uint ReadDword(uint offset)
    {
        if (offset == IdOffset)
        {
            return _id;
        }
        if (offset == VersionOffset)
        {
            return ComputeVersionRegister();
        }
        if (offset == TprOffset)
        {
            return _tpr & 0xFF;
        }
        if (offset == AprOffset)
        {
            return 0;
        }
        if (offset == PprOffset)
        {
            return ComputePpr();
        }
        if (offset == RrdOffset)
        {
            return 0;
        }
        if (offset == LdrOffset)
        {
            return _ldr;
        }
        if (offset == DfrOffset)
        {
            return _dfr;
        }
        if (offset == SvrOffset)
        {
            return _svr;
        }
        if (offset == EsrOffset)
        {
            return _esr;
        }
        if (offset == IcrLowOffset)
        {
            return _icrLow & ~IcrDeliveryStatusBit;   // A6: always synchronous
        }
        if (offset == IcrHighOffset)
        {
            return _icrHigh;
        }
        if (offset == InitialCountOffset)
        {
            return _initialCount;
        }
        if (offset == CurrentCountOffset)
        {
            return CurrentCount;
        }
        if (offset == DivideConfigOffset)
        {
            return _dcr;
        }
        if (TryLvtIndex(offset, out int lvtIndex))
        {
            return ReadLvt(lvtIndex);
        }
        if (TryBitmapWord(offset, IsrBase, out int isrWord))
        {
            return _isr[isrWord];
        }
        if (TryBitmapWord(offset, TmrBase, out int tmrWord))
        {
            return _tmr[tmrWord];
        }
        if (TryBitmapWord(offset, IrrBase, out int irrWord))
        {
            return _irr[irrWord];
        }
        return 0;
    }

    public void WriteDword(uint offset, uint value)
    {
        if (offset == IdOffset) { _id = value & IdFieldMask; return; }
        if (offset == TprOffset) { _tpr = value & 0xFF; return; }
        if (offset == EoiOffset) { HandleEoi(); return; }
        if (offset == LdrOffset) { _ldr = value; return; }
        if (offset == DfrOffset) { _dfr = value; return; }
        if (offset == SvrOffset) { WriteSvr(value); return; }
        if (offset == EsrOffset) { _esr = 0; return; }   // write-then-read: nothing here ever latches a real error
        if (offset == IcrLowOffset) { WriteIcrLow(value); return; }
        if (offset == IcrHighOffset) { _icrHigh = value; return; }
        if (offset == InitialCountOffset) { WriteInitialCount(value); return; }
        if (offset == DivideConfigOffset) { WriteDivideConfig(value); return; }
        if (TryLvtIndex(offset, out int lvtIndex)) { WriteLvt(lvtIndex, value); return; }
        // ISR/TMR/IRR and every other offset are read-only or unimplemented.
    }

    /// <summary>
    /// Independent review, item N6: on a 1-to-0 transition of SVR's own
    /// software-enable bit, every stored LVT gets its mask bit OR'd in on
    /// the spot (SDM: software-disabling the unit masks every LVT entry).
    /// Without this, an LVT that was unmasked before the disable would still
    /// read that way the instant SVR is re-enabled, even though nothing
    /// could have delivered through it in between -- <see cref="ReadLvt"/>/
    /// <see cref="WriteLvt"/> only force the bit while still disabled, which
    /// is not the same guarantee as this one-time latch at the transition.
    /// </summary>
    private void WriteSvr(uint value)
    {
        bool wasEnabled = (_svr & SvrSoftwareEnableBit) != 0;
        _svr = (value & ~SvrHardwiredLowNibble) | SvrHardwiredLowNibble;
        bool nowEnabled = (_svr & SvrSoftwareEnableBit) != 0;

        if (wasEnabled && !nowEnabled)
        {
            for (int i = 0; i < _lvt.Length; i++) _lvt[i] |= LvtMaskBit;
        }
    }

    /// <summary>Item 12: while software-disabled, an LVT reads with its mask bit forced to 1, live, regardless of what was last written.</summary>
    private uint ReadLvt(int lvtIndex)
    {
        uint value = _lvt[lvtIndex];
        return (_svr & SvrSoftwareEnableBit) == 0 ? value | LvtMaskBit : value;
    }

    /// <summary>
    /// Item 12: while software-disabled, a write can never clear the mask
    /// bit in storage either -- so a guest that unmasks LINT0 (or any other
    /// LVT) before enabling SVR gets nothing for it even after SVR is later
    /// enabled, matching "writes cannot unmask them" literally rather than
    /// only as a read-time display fiction.
    /// </summary>
    private void WriteLvt(int lvtIndex, uint value)
    {
        _lvt[lvtIndex] = (_svr & SvrSoftwareEnableBit) == 0 ? value | LvtMaskBit : value;
    }

    private static bool TryLvtIndex(uint offset, out int index)
    {
        if (offset == LvtTimerOffset) { index = LvtTimerIndex; return true; }
        if (offset == LvtThermalOffset) { index = LvtThermalIndex; return true; }
        if (offset == LvtPerfMonOffset) { index = LvtPerfMonIndex; return true; }
        if (offset == LvtLint0Offset) { index = LvtLint0Index; return true; }
        if (offset == LvtLint1Offset) { index = LvtLint1Index; return true; }
        if (offset == LvtErrorOffset) { index = LvtErrorIndex; return true; }
        index = 0;
        return false;
    }

    private static bool TryBitmapWord(uint offset, uint baseOffset, out int word)
    {
        if (offset >= baseOffset && offset < baseOffset + 8 * 0x10 && (offset & 0xF) == 0)
        {
            word = (int)((offset - baseOffset) >> 4);
            return true;
        }

        word = 0;
        return false;
    }

    private uint ComputeVersionRegister() => VersionRegisterFor(_cpu.State.Model);

    /// <summary>
    /// The model-dependent Version register (coordinator decision A8): the
    /// low byte is the xAPIC version, bits 23:16 are the highest LVT index
    /// (one less than the LVT entry count). Public so the MP table's own
    /// processor entry (<c>BiosRom.Mp.cs</c>) can report the same version a
    /// guest reading this register would see, without duplicating the table.
    /// </summary>
    public static uint VersionRegisterFor(CpuModel model) => model switch
    {
        CpuModel.PentiumMmx => VersionValuePentiumMmx,
        CpuModel.PentiumII or CpuModel.PentiumIII => VersionValueP6,
        _ => VersionValueP4Plus,
    };

    private void HandleEoi()
    {
        int vector = HighestSetBit(_isr);
        if (vector < 0)
        {
            return;
        }

        ClearBit(_isr, vector);
        if (GetBit(_tmr, vector))
        {
            LevelTriggeredEoi?.Invoke((byte)vector);
        }
    }

    /// <summary>
    /// Writing the low ICR dword sends. Only self and all-including-self
    /// shorthands do anything (decision A10: no other logical CPU exists in
    /// this package), and only Fixed and NMI delivery modes act on it; every
    /// other combination is still stored and read back so a guest probing
    /// the register never sees an unexplained value, but SMI/INIT/STARTUP
    /// and a destination-field send have no target to reach yet.
    /// </summary>
    private void WriteIcrLow(uint value)
    {
        _icrLow = value & IcrLowWritableMask;

        uint shorthand = _icrLow & IcrShorthandMask;
        bool targetsThisCpu = shorthand is IcrShorthandSelf or IcrShorthandAllIncludingSelf;
        if (!targetsThisCpu)
        {
            return;
        }

        uint mode = _icrLow & IcrDeliveryModeMask;
        byte vector = (byte)(_icrLow & IcrVectorMask);
        if (mode == IcrDeliveryModeFixed)
        {
            DeliverFixed(vector, levelTriggered: false);
        }

        // NMI (mode == IcrDeliveryModeNmi) has no CPU-side entry point yet;
        // see the type summary's note on LINT1/NMI. SMI/INIT/STARTUP are
        // accepted (stored above) and otherwise inert, per decision A10.
    }

    private void WriteInitialCount(uint value)
    {
        _initialCount = value;
        ArmTimerForTicks(_initialCount);
    }

    /// <summary>
    /// Written mid-count, the new divisor applies from this moment on
    /// without losing progress or restarting the period (independent
    /// review, "Low" item): the ticks remaining under the old divisor are
    /// read first, the divisor changes, and the timer re-arms for exactly
    /// that many ticks at the new rate.
    /// </summary>
    private void WriteDivideConfig(uint value)
    {
        uint remainingTicks = CurrentCount;
        bool wasActive = TimerActive;
        _dcr = value & 0xB;
        if (wasActive)
        {
            ArmTimerForTicks(remainingTicks);
        }
    }

    /// <summary>
    /// True while a timer event is genuinely still due: not merely
    /// non-null, but still recognised by the scheduler. <c>Machine</c>'s own
    /// reset path (a guest resetting through the keyboard controller or port
    /// 92h) clears every pending event without telling each owner
    /// individually, so a stale handle here must never be mistaken for a
    /// live one -- see <see cref="Capture"/>'s own note on the same hazard.
    /// </summary>
    private bool TimerActive => _timerEvent is { } pending && _scheduler.IsPending(pending);

    /// <summary>The ticks remaining as of the last (re)arm; see <see cref="CurrentCount"/>.</summary>
    private uint _countAtArm;

    /// <summary>Initial Count minus however many ticks have elapsed since the last (re)load, at the divisor's rate; never negative, zero once a one-shot has expired or the timer is not running at all.</summary>
    private uint CurrentCount
    {
        get
        {
            if (!TimerActive)
            {
                return 0;
            }

            int divide = DivideValueFromDcr(_dcr);
            long elapsedCycles = Math.Max(0, _cyclesNow() - _timerStartCycle);
            long elapsedTicks = elapsedCycles / divide;
            long remaining = _countAtArm - elapsedTicks;
            return remaining > 0 ? (uint)remaining : 0;
        }
    }

    /// <summary>(Re)arms the scheduler event for the full Initial Count -- an ordinary write to it, or a periodic reload.</summary>
    private void ArmTimer() => ArmTimerForTicks(_initialCount);

    /// <summary>
    /// (Re)arms the scheduler event for <paramref name="ticks"/> at the
    /// current divisor, starting now. A count of zero stops the timer,
    /// matching real hardware: nothing is due, and <see
    /// cref="CurrentCount"/> reads zero until the next write here.
    /// </summary>
    private void ArmTimerForTicks(uint ticks)
    {
        if (_timerEvent is { } pending)
        {
            _scheduler.Cancel(pending);
        }
        _timerEvent = null;
        _countAtArm = ticks;

        if (ticks == 0)
        {
            return;
        }

        int divide = DivideValueFromDcr(_dcr);
        _timerStartCycle = _cyclesNow();
        long cyclesUntilDue = (long)ticks * divide;
        _timerEvent = _scheduler.ScheduleAfter(_timerStartCycle, cyclesUntilDue, "lapic-timer", FireTimer);
    }

    /// <summary>
    /// The timer's own vector, unmasked or not: masking stops delivery only,
    /// the count keeps running underneath it (plans/specs/apic.md §1.5,
    /// confirmed against the same "masked device keeps its own state"
    /// pattern this codebase already uses for edge/level lines elsewhere).
    /// The mask check is <see cref="IsLvtMasked"/>, not the raw stored bit:
    /// software-disabled (item 12) holds this back exactly as it does LINT0.
    /// Periodic mode reloads from Initial Count and reschedules; one-shot
    /// simply lets Current Count sit at zero until the next write.
    /// </summary>
    private void FireTimer(long dueCycle)
    {
        uint timerLvt = _lvt[LvtTimerIndex];
        if (!IsLvtMasked(LvtTimerIndex))
        {
            DeliverFixed((byte)(timerLvt & LvtVectorMask), levelTriggered: false);
        }

        bool periodic = (timerLvt & LvtTimerPeriodicBit) != 0;
        if (!periodic)
        {
            _timerEvent = null;
            _countAtArm = 0;
            return;
        }

        int divide = DivideValueFromDcr(_dcr);
        _timerStartCycle = dueCycle;
        _countAtArm = _initialCount;
        long cyclesUntilDue = (long)_initialCount * divide;
        _timerEvent = _scheduler.ScheduleAfter(dueCycle, cyclesUntilDue, "lapic-timer", FireTimer);
    }

    private static int DivideValueFromDcr(uint dcr)
    {
        // Bits {3,1,0} form the code, in that significance order -- bit 3 is
        // its top bit, bit 1 its middle bit, bit 0 its bottom bit. Bit 2 of
        // the register plays no part at all (plans/specs/apic.md §1.2).
        int highBit = (int)((dcr >> 3) & 1);
        int middleBit = (int)((dcr >> 1) & 1);
        int lowBit = (int)(dcr & 1);
        int code = (highBit << 2) | (middleBit << 1) | lowBit;
        return DivideValuesByCode[code];
    }

    // ---- IA32_APIC_BASE wiring ---------------------------------------------

    /// <summary>Bits this class cares about in the raw APIC_BASE MSR value; see <see cref="ModelSpecificRegisters"/>'s own constants for the full field layout.</summary>
    private const ulong ApicBaseAddressMask = 0xFFFFFF000UL;
    private const ulong ApicBaseEnableBit = 1UL << 11;

    /// <summary>
    /// Called once at construction (with the CPU's current APIC_BASE, before
    /// any write has happened) and again on every <see
    /// cref="ModelSpecificRegisters.ApicBaseChanged"/>: moves or removes the
    /// MMIO page, and applies the reset register values on a 0-to-1
    /// hardware-enable transition (coordinator decision A7). A model without
    /// <see cref="CpuFeature.Apic"/> never maps the page even though the MSR
    /// still carries its power-on value with the enable bit set -- RDMSR/
    /// WRMSR on this index already <c>#GP</c>s for such a model, so nothing
    /// legitimate could have changed the value anyway.
    /// </summary>
    public void OnApicBaseChanged(ulong apicBaseValue)
    {
        bool wasEnabled = _hardwareEnabled;
        UpdateMapping(apicBaseValue);
        if (_hardwareEnabled && !wasEnabled)
        {
            ResetRegisters();
        }
    }

    /// <summary>
    /// Moves or removes the MMIO page and updates <see
    /// cref="_hardwareEnabled"/>, without the register-reset side effect a
    /// live 0-to-1 transition gets: <see cref="Restore"/> calls this alone,
    /// because a snapshot's saved registers must survive re-deriving the
    /// mapping from the CPU's already-restored APIC_BASE, not be wiped by it.
    /// </summary>
    private void UpdateMapping(ulong apicBaseValue)
    {
        _hardwareEnabled = (apicBaseValue & ApicBaseEnableBit) != 0;

        // Whichever mechanism last held the page is released first: the
        // dense per-page path (Unmap) if a previous relocation landed below
        // the addressable ceiling, the high-window one otherwise. Both are
        // harmless no-ops the first time, before anything has ever been
        // mapped.
        if (_mappedBase is { } previousBase)
        {
            if (previousBase < _memory.AddressableBytes)
            {
                _memory.Unmap(previousBase, PageSize);
            }
            else _memory.UnregisterHighWindow(this);
        }

        _mappedBase = null;

        if (_hardwareEnabled && ApicPresent)
        {
            uint newBase = (uint)(apicBaseValue & ApicBaseAddressMask);

            // Independent review, item N8a: APIC_BASE's own reset value
            // (FEE00000h) and every relocation a real guest actually makes
            // land above the addressable ceiling, where a high window
            // (PhysicalMemoryBus.RegisterHighWindow) is what answers for it.
            // A relocation below the ceiling instead goes through the
            // ordinary per-page path (TryMapDevice), with that method's own
            // "never over RAM, ROM or another device" rule -- exactly the
            // same refusal a PCI BAR programmed over memory it does not own
            // already gets elsewhere in this codebase, not a new one invented
            // here. One placement this does not reach at all: inside the top
            // megabyte's high alias (0xFFFF0000 and up), Translate() rewrites
            // the address to the BIOS ROM before either mapping mechanism
            // ever sees it, so a guest relocating its LAPIC there finds ROM
            // instead -- a real gap, and not one any cited source suggests a
            // guest OS ever does, so it is documented rather than chased.
            if (newBase < _memory.AddressableBytes)
            {
                if (_memory.TryMapDevice(newBase, PageSize, this))
                {
                    _mappedBase = newBase;
                }
            }
            else
            {
                _memory.RegisterHighWindow(newBase, PageSize, this);
                _mappedBase = newBase;
            }
        }
    }

    /// <summary>
    /// The reset register values plans/specs/apic.md §1.2 lists: applied on
    /// a 0-to-1 hardware re-enable (decision A7, from <see
    /// cref="OnApicBaseChanged"/>) and unconditionally on a processor RESET
    /// (SDM 10.4.7, from <see cref="ResetOnCpuReset"/>). Current Count reads
    /// 0 afterwards because the timer itself is stopped, not merely zeroed.
    /// </summary>
    private void ResetRegisters()
    {
        _id = 0;
        _tpr = 0;
        _ldr = 0;
        _dfr = 0xFFFFFFFF;
        _svr = SvrResetValue;
        Array.Clear(_isr);
        Array.Clear(_tmr);
        Array.Clear(_irr);
        _esr = 0;
        _icrLow = 0;
        _icrHigh = 0;
        Array.Fill(_lvt, LvtResetValue);
        _initialCount = 0;
        _dcr = 0;
        StopTimer();
    }

    private void StopTimer()
    {
        if (_timerEvent is { } pending)
        {
            _scheduler.Cancel(pending);
        }
        _timerEvent = null;
        _countAtArm = 0;
    }

    /// <summary>
    /// Every processor RESET (SDM 10.4.7), regardless of what APIC_BASE's
    /// hardware-enable bit is doing: <see cref="Cpu.Cpu.Reset"/> calls this
    /// unconditionally through <see cref="IResettableInterruptSource"/>,
    /// which is why the registers go back to power-on values even when the
    /// unit was already enabled before and after the reset (a 0-to-1
    /// transition, <see cref="OnApicBaseChanged"/>'s own trigger, would
    /// otherwise never fire for that case).
    /// </summary>
    public void ResetOnCpuReset() => ResetRegisters();

    // ---- bitmap helpers -----------------------------------------------------

    private static bool GetBit(uint[] bitmap, int vector) => (bitmap[vector >> 5] & (1u << (vector & 31))) != 0;
    private static void SetBit(uint[] bitmap, int vector) => bitmap[vector >> 5] |= 1u << (vector & 31);
    private static void ClearBit(uint[] bitmap, int vector) => bitmap[vector >> 5] &= ~(1u << (vector & 31));

    private static int HighestSetBit(uint[] bitmap256)
    {
        for (int word = 7; word >= 0; word--)
        {
            if (bitmap256[word] == 0)
            {
                continue;
            }
            int bitInWord = 31 - BitOperations.LeadingZeroCount(bitmap256[word]);
            return word * 32 + bitInWord;
        }

        return -1;
    }

    // ---- snapshot -------------------------------------------------------------

    /// <summary>
    /// Every register and the timer's phase. IMCR mode is not among them: it
    /// is chipset state, captured and restored through Machine's own "IMCR"
    /// section and re-applied to <see cref="ImcrApicMode"/> from there, so a
    /// board with no IMCR device (i430FX) never has to carry a byte for one.
    /// Not committed anywhere yet (independent review, item 11), so its
    /// layout is free to include <see cref="_countAtArm"/> directly rather
    /// than re-derive it from a cycle count, which a mid-flight divisor
    /// change (item "Low") would otherwise make imprecise.
    /// </summary>
    public const int SnapshotLength = 4 + 4 + 4 + 4 + 4 + 8 * 4 + 8 * 4 + 8 * 4 + 4 + 4 + 4 + LvtCount * 4 + 4 + 4 + 8 + 4;

    public byte[] Capture()
    {
        using var stream = new MemoryStream(SnapshotLength);
        using var writer = new BinaryWriter(stream);
        writer.Write(_id);
        writer.Write(_tpr);
        writer.Write(_ldr);
        writer.Write(_dfr);
        writer.Write(_svr);
        foreach (uint word in _isr) writer.Write(word);
        foreach (uint word in _tmr) writer.Write(word);
        foreach (uint word in _irr) writer.Write(word);
        writer.Write(_esr);
        writer.Write(_icrLow);
        writer.Write(_icrHigh);
        foreach (uint entry in _lvt) writer.Write(entry);
        writer.Write(_initialCount);
        writer.Write(_dcr);

        // The cycles remaining until the timer is due, not its absolute due
        // cycle: Cpu.Cycles is not itself part of a machine snapshot (see
        // Devices.Chipset.Piix4PowerManagement's own timer for the same
        // rule), so the receiving machine's clock could read anything at
        // all by the time Restore runs, and only a distance measured from
        // "now" survives that rebasing intact. TimerActive, not merely
        // "is _timerEvent non-null": a reset elsewhere (Machine.Run.cs's
        // Scheduler.Clear on a guest reset) can leave a stale, inactive
        // handle here that this device was never told about (independent
        // review, item 9) -- serialising its long-dead DueCycle would
        // resurrect a timer that should have stayed cancelled.
        writer.Write(TimerActive ? _timerEvent!.DueCycle - _cyclesNow() : -1L);

        // The ticks remaining right now, not _countAtArm's own value (the
        // count as of the *last* arm, which capture may happen long after):
        // CurrentCount already folds in the cycles elapsed since then.
        writer.Write(TimerActive ? CurrentCount : 0u);
        return stream.ToArray();
    }

    public void Restore(ReadOnlySpan<byte> state)
    {
        using var reader = new BinaryReader(new MemoryStream(state.ToArray()));
        _id = reader.ReadUInt32();
        _tpr = reader.ReadUInt32();
        _ldr = reader.ReadUInt32();
        _dfr = reader.ReadUInt32();
        _svr = reader.ReadUInt32();
        for (int i = 0; i < _isr.Length; i++) _isr[i] = reader.ReadUInt32();
        for (int i = 0; i < _tmr.Length; i++) _tmr[i] = reader.ReadUInt32();
        for (int i = 0; i < _irr.Length; i++) _irr[i] = reader.ReadUInt32();
        _esr = reader.ReadUInt32();
        _icrLow = reader.ReadUInt32();
        _icrHigh = reader.ReadUInt32();
        for (int i = 0; i < _lvt.Length; i++) _lvt[i] = reader.ReadUInt32();
        _initialCount = reader.ReadUInt32();
        _dcr = reader.ReadUInt32();
        long remainingCycles = reader.ReadInt64();
        uint countAtArm = reader.ReadUInt32();

        // Rebased from the receiving machine's own clock, not the file's:
        // see the note on Capture's own write of this field.
        long now = _cyclesNow();
        if (_timerEvent is { } pending)
        {
            _scheduler.Cancel(pending);
        }
        _timerEvent = remainingCycles < 0
            ? null
            : _scheduler.ScheduleAfter(now, remainingCycles, "lapic-timer", FireTimer);
        _countAtArm = _timerEvent is null ? 0 : countAtArm;
        _timerStartCycle = now;

        // Re-derive the mapping/enable state from whatever APIC_BASE the CPU
        // section already restored, rather than trusting a stored duplicate
        // of it that could disagree. UpdateMapping alone, not
        // OnApicBaseChanged: the registers above are already the snapshot's,
        // and must not be overwritten by a spurious 0-to-1 reset.
        UpdateMapping(_cpu.Msrs.ApicBaseRaw);
    }
}
