namespace Aurora386.Core.Cpu;

/// <summary>
/// The processor's part of a machine snapshot.
///
/// The integer unit writes its own state here rather than having the snapshot
/// reach into <see cref="CpuState"/> field by field, so that a change to the
/// register file changes one file and one version number. The coprocessor has
/// a section of its own, written by <see cref="Fpu.X87Unit.WriteSnapshot"/>.
///
/// Version 0 is the layout every snapshot had before sections existed, byte
/// for byte, which is how an old file is read: its processor fields are handed
/// to <see cref="ReadSnapshot"/> as a version-0 section. Version 1 appends what
/// that layout forgot -- the debug and test registers, the interrupt shadow
/// after STI or MOV SS, and the incoming carry of a pending ADC or SBB. Version
/// 2 appended the Pentium's system state: CR4, the time-stamp counter as the
/// guest reads it, and the Pentium's model-specific registers. Version 3 keeps
/// CR4 and the TSC, moves the MSRs to a section of their own ("MSR ", whose
/// length depends on the processor family) and appends the four PAE
/// page-directory-pointer registers, which the processor loads from memory
/// only at particular moments and which cannot be recomputed on restore.
/// </summary>
public sealed partial class Cpu
{
    /// <summary>The layout <see cref="WriteSnapshot"/> produces.</summary>
    public const int SnapshotVersion = 6;

    /// <summary>The eight registers every 16- and 32-bit processor has; the version-0 fields hold their low halves.</summary>
    private const int LegacyRegisterCount = 8;

    /// <summary>The registers only 64-bit code reaches, R8 to R15.</summary>
    private const int ExtendedRegisterCount = 8;

    /// <summary>Writes the integer unit's state for a machine snapshot.</summary>
    public void WriteSnapshot(BinaryWriter writer)
    {
        WriteVersionZeroFields(writer);

        // Version 1: the debug registers hold the breakpoints a debugger has
        // armed, and the shadow decides whether an interrupt may be taken
        // before the next instruction. Losing either changes what the guest
        // does next.
        foreach (uint value in State.Dr)
        {
            writer.Write(value);
        }

        foreach (uint value in State.Tr)
        {
            writer.Write(value);
        }

        writer.Write(_interruptShadow);

        // The incoming carry cannot be recomputed from the operands and the
        // result, so a pending ADC or SBB restored without it would report
        // the wrong carry to the next instruction that reads one.
        writer.Write(Flags.CarryIn);

        // Version 2. The TSC is stored as the value the guest would read, not
        // as the cycle count behind it: the machine it is restored into has a
        // cycle count of its own, and what must carry on without a jump is the
        // counter the guest sees.
        writer.Write(State.Cr4);
        writer.Write(Tsc);

        // Version 3: the PDPTE registers. They are what the processor loaded
        // at the last MOV CR3 under PAE, not what the table in memory says
        // now, and the two may differ.
        foreach (ulong entry in Pdpte)
        {
            writer.Write(entry);
        }

        // Version 4, long mode (plans/long-mode.md stage A): the upper halves
        // of the eight legacy registers, R8-R15 whole, the upper half of RIP,
        // EFER, and each segment's L bit. The version-0 fields keep the low
        // halves, so an older reader of this layout never sees a difference.
        for (int i = 0; i < LegacyRegisterCount; i++)
        {
            writer.Write((uint)(State.Gpr[i] >> 32));
        }

        for (int i = 0; i < ExtendedRegisterCount; i++)
        {
            writer.Write(State.Gpr[LegacyRegisterCount + i]);
        }

        writer.Write((uint)(State.Rip >> 32));
        writer.Write(State.Efer);
        foreach (SegmentCache segment in State.Segments)
        {
            writer.Write(segment.Long);
        }

        // Version 5, 64-bit segment bases: each segment's upper base half
        // (only FS and GS ever hold one) and IA32_KERNEL_GS_BASE.
        foreach (SegmentCache segment in State.Segments)
        {
            writer.Write(segment.BaseHigh);
        }

        writer.Write(State.KernelGsBase);

        // Version 6, long mode's system state: the upper halves of CR2 and of
        // the GDT, IDT, LDT and TSS bases.
        writer.Write(State.Cr2High);
        writer.Write(State.GdtBaseHigh);
        writer.Write(State.IdtBaseHigh);
        writer.Write(State.LdtBaseHigh);
        writer.Write(State.TssBaseHigh);
    }

    /// <summary>
    /// Reads back what <see cref="WriteSnapshot"/> wrote, or the processor
    /// fields of a file from before sections existed (version 0). Cached
    /// translations are dropped afterwards: they describe the address space
    /// the machine had before the restore.
    /// </summary>
    public void ReadSnapshot(BinaryReader reader, int version)
    {
        if (version > SnapshotVersion)
        {
            throw new InvalidDataException($"CPU snapshot version {version} is not supported.");
        }

        ReadVersionZeroFields(reader);

        if (version >= 1)
        {
            for (int i = 0; i < State.Dr.Length; i++)
            {
                State.Dr[i] = reader.ReadUInt32();
            }

            for (int i = 0; i < State.Tr.Length; i++)
            {
                State.Tr[i] = reader.ReadUInt32();
            }

            _interruptShadow = reader.ReadBoolean();
            Flags.CarryIn = reader.ReadBoolean();
        }
        else
        {
            // An old file never recorded these. Zero is what they hold after
            // RESET, and no shadow is the state between two ordinary
            // instructions, which is the overwhelmingly common case. The
            // carry-in stays clear, as the version-0 fields left it: old files
            // lost it and nothing can recover it now.
            Array.Clear(State.Dr);
            Array.Clear(State.Tr);
            _interruptShadow = false;
        }

        if (version >= 2)
        {
            // Held down to what this processor's CR4 can hold. The snapshot
            // does not record the model, and a Pentium's CR4.DE restored into
            // a machine set up as a 486 would make MOV DR4 fault on a chip
            // that has no CR4 at all.
            State.Cr4 = reader.ReadUInt32() & State.Cr4Writable;

            ulong timeStampCounter = reader.ReadUInt64();
            _tscOffset = unchecked((long)timeStampCounter - Cycles);

            if (version == 2)
            {
                // Version 2 carried the Pentium's MSRs inline.
                Msrs.ReadSnapshot(reader, version: 1);
            }
        }
        else
        {
            // Files from before the Pentium state existed come from a 386 or a
            // 486, which have no CR4 and no MSRs: their power-on values are
            // exactly what those processors hold, and they are set here rather
            // than left as the receiving machine had them, so that one file
            // restores the same state into any machine. The TSC is left
            // counting: those files never recorded one.
            State.Cr4 = 0;
            Msrs.Reset();
        }

        if (version >= 3)
        {
            for (int i = 0; i < Pdpte.Length; i++)
            {
                Pdpte[i] = reader.ReadUInt64();
            }
        }
        else
        {
            // Older files come from machines without PAE.
            Array.Clear(Pdpte);
        }

        if (version >= 4)
        {
            for (int i = 0; i < LegacyRegisterCount; i++)
            {
                State.Gpr[i] |= (ulong)reader.ReadUInt32() << 32;
            }

            for (int i = 0; i < ExtendedRegisterCount; i++)
            {
                State.Gpr[LegacyRegisterCount + i] = reader.ReadUInt64();
            }

            State.Rip |= (ulong)reader.ReadUInt32() << 32;
            // Masked to the bits this processor carries, as CR4 is above: a
            // file from a long-mode model restored into one without it must
            // not leave LME/LMA set where RDMSR cannot even reach them.
            State.Efer = reader.ReadUInt64() & Msrs.EferImplementedBits;
            for (int i = 0; i < State.Segments.Length; i++)
            {
                State.Segments[i].Long = reader.ReadBoolean();
            }
        }
        else
        {
            // Files from before long mode existed: nothing above 32 bits, no
            // R8-R15, EFER at its power-on zero, no 64-bit code segment.
            for (int i = LegacyRegisterCount; i < State.Gpr.Length; i++)
            {
                State.Gpr[i] = 0;
            }

            State.Efer = 0;
            for (int i = 0; i < State.Segments.Length; i++)
            {
                State.Segments[i].Long = false;
            }
        }

        if (version >= 5)
        {
            for (int i = 0; i < State.Segments.Length; i++)
            {
                State.Segments[i].BaseHigh = reader.ReadUInt32();
            }

            State.KernelGsBase = reader.ReadUInt64();
        }
        else
        {
            // Files from before 64-bit segment bases: every base fits in 32 bits.
            for (int i = 0; i < State.Segments.Length; i++)
            {
                State.Segments[i].BaseHigh = 0;
            }

            State.KernelGsBase = 0;
        }

        if (version >= 6)
        {
            State.Cr2High = reader.ReadUInt32();
            State.GdtBaseHigh = reader.ReadUInt32();
            State.IdtBaseHigh = reader.ReadUInt32();
            State.LdtBaseHigh = reader.ReadUInt32();
            State.TssBaseHigh = reader.ReadUInt32();
        }
        else
        {
            // Files from before 64-bit system state: every base fits in 32 bits.
            State.Cr2High = 0;
            State.GdtBaseHigh = 0;
            State.IdtBaseHigh = 0;
            State.LdtBaseHigh = 0;
            State.TssBaseHigh = 0;
        }

        FlushTlb();
    }

    /// <summary>
    /// The fields in the order the original, unsectioned snapshot wrote them.
    /// The order is frozen: old files are read through it.
    /// </summary>
    private void WriteVersionZeroFields(BinaryWriter writer)
    {
        for (int i = 0; i < LegacyRegisterCount; i++)
        {
            writer.Write(State.GetReg32(i));
        }

        foreach (SegmentCache segment in State.Segments)
        {
            writer.Write(segment.Selector);
            writer.Write(segment.Base);
            writer.Write(segment.Limit);
            writer.Write(segment.Big);
            writer.Write(segment.Readable);
            writer.Write(segment.Writable);
            writer.Write(segment.ExpandDown);
            writer.Write(segment.Dpl);
        }

        writer.Write((byte)Flags.Op);
        writer.Write((uint)Flags.Stored);
        writer.Write(Flags.Left);
        writer.Write(Flags.Right);
        writer.Write(Flags.Result);
        writer.Write(Flags.Size);

        writer.Write(State.Eip);
        writer.Write(State.Cr0);
        writer.Write(State.Cr2);
        writer.Write(State.Cr3);
        writer.Write(State.GdtBase);
        writer.Write(State.GdtLimit);
        writer.Write(State.IdtBase);
        writer.Write(State.IdtLimit);
        writer.Write(State.Ldtr);
        writer.Write(State.LdtBase);
        writer.Write(State.LdtLimit);
        writer.Write(State.TaskRegister);
        writer.Write(State.TssBase);
        writer.Write(State.TssLimit);
        writer.Write((uint)State.OtherFlags);
        writer.Write(State.CplRaw);
        writer.Write(State.Halted);
        writer.Write(State.Virtual8086Mode);
    }

    private void ReadVersionZeroFields(BinaryReader reader)
    {
        for (int i = 0; i < LegacyRegisterCount; i++)
        {
            State.Gpr[i] = reader.ReadUInt32();
        }

        for (int i = 0; i < State.Segments.Length; i++)
        {
            ref SegmentCache segment = ref State.Segments[i];

            segment.Selector = reader.ReadUInt16();
            segment.Base = reader.ReadUInt32();
            segment.Limit = reader.ReadUInt32();
            segment.Big = reader.ReadBoolean();
            segment.Readable = reader.ReadBoolean();
            segment.Writable = reader.ReadBoolean();
            segment.ExpandDown = reader.ReadBoolean();
            segment.Dpl = reader.ReadByte();
        }

        // The lazy flags go back as they were -- operation, operands and all
        // -- rather than as a computed EFLAGS value, because the next
        // instruction may be one that reads only some of them.
        LazyFlags flags = default;
        flags.Op = (FlagOp)reader.ReadByte();
        flags.Stored = (Flags)reader.ReadUInt32();
        flags.Left = reader.ReadUInt32();
        flags.Right = reader.ReadUInt32();
        flags.Result = reader.ReadUInt32();
        flags.Size = reader.ReadByte();
        Flags = flags;

        State.Eip = reader.ReadUInt32();
        State.Cr0 = reader.ReadUInt32();
        State.Cr2 = reader.ReadUInt32();
        State.Cr3 = reader.ReadUInt32();
        State.GdtBase = reader.ReadUInt32();
        State.GdtLimit = reader.ReadUInt16();
        State.IdtBase = reader.ReadUInt32();
        State.IdtLimit = reader.ReadUInt16();
        State.Ldtr = reader.ReadUInt16();
        State.LdtBase = reader.ReadUInt32();
        State.LdtLimit = reader.ReadUInt32();
        State.TaskRegister = reader.ReadUInt16();
        State.TssBase = reader.ReadUInt32();
        State.TssLimit = reader.ReadUInt32();
        State.OtherFlags = (Flags)reader.ReadUInt32();
        State.CplRaw = reader.ReadByte();
        State.Halted = reader.ReadBoolean();
        State.Virtual8086Mode = reader.ReadBoolean();
    }
}
