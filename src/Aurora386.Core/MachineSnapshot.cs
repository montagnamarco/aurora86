using System.Text;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Cards;

namespace Aurora386.Core;

/// <summary>
/// A copy of the machine's state, taken at an instant and put back later.
///
/// This exists for the sake of investigation rather than for the user. Reaching
/// an interesting moment in a guest can take twenty minutes of emulated boot
/// and installation; a defect that needs twenty attempts to corner is a day's
/// work at that rate and a few minutes with a snapshot taken just before it.
///
/// Captures CPU, FPU and RAM together with video, interrupt, timer, keyboard,
/// PCI, USB, DOS integration, PIIX4 DMA and expansion card state. Coverage is
/// still incomplete: in-flight PIO/ATAPI transfers and the serial mouse UART
/// are not captured, nor is the
/// software coprocessor a 386 without a 387 uses (<c>Machine.Fpu</c>); the FPU
/// section holds the 486's on-die unit. Disk
/// contents are external state and must be paired with the snapshot by the
/// caller; restoring RAM alone does not undo guest writes to an image.
///
/// The state is held as sections, each with a four-letter name, a version and
/// a length, and a file is those sections one after another behind a magic
/// word. A section's owner decides what its versions mean; the container only
/// guarantees that a reader skips a section it does not know and that a
/// missing section is noticed rather than misread. Files written before the
/// sections existed have no magic word and are read by
/// <see cref="ReadLegacy"/>, which cuts their fields into the same sections.
/// </summary>
public sealed class MachineSnapshot
{
    // ---- section names ------------------------------------------------------

    private const string RamSection = "RAM ";
    private const string CpuSection = "CPU ";
    private const string FpuSection = "FPU ";

    /// <summary>
    /// The model-specific registers. Version 1 is the Pentium's set, version 2
    /// family six's; each has a fixed length. Files before CPU version 3
    /// carried the Pentium's set inside the CPU section instead.
    /// </summary>
    private const string MsrSection = "MSR ";

    /// <summary>
    /// The SSE registers: XMM0-7 and MXCSR (version 1). A file without it
    /// restores them at their power-on values.
    /// </summary>
    private const string SseSection = "SSE ";

    /// <summary>
    /// The A20 gate. It belongs in a snapshot even though it is a chipset
    /// latch rather than processor state: with it closed every address above
    /// the first megabyte silently wraps, so a snapshot restored with the
    /// wrong A20 reads different memory than the one it captured -- the page
    /// tables most of all, and then nothing resolves.
    /// </summary>
    private const string A20Section = "A20 ";

    /// <summary>
    /// The video card: its memory and every register that decides what is on
    /// screen.
    ///
    /// Without these a restored machine has the guest's pixels and the BIOS's
    /// idea of the mode, which agree about nothing -- and the guest, having
    /// no reason to redraw a picture it believes is already there, never puts
    /// it right. It is the difference between a snapshot that resumes and one
    /// that resumes blind.
    /// </summary>
    private const string VideoMemorySection = "VMEM";
    private const string VideoRegistersSection = "VREG";

    /// <summary>
    /// The chips that decide whether the guest is ever interrupted again.
    ///
    /// A snapshot without them restores the interrupt controllers to their
    /// power-on state, which is every line masked, and the timer to its
    /// power-on divisor. The guest then sits in whatever idle loop it was in,
    /// waiting for a tick that cannot arrive -- and the symptom, a picture
    /// half drawn and a machine that ignores the keyboard, looks exactly like
    /// a fault in whatever the guest was drawing at the time. This cost a
    /// good deal of time to find, so it is worth the forty bytes.
    /// </summary>
    private const string InterruptControllersSection = "PIC ";
    private const string TimerSection = "PIT ";
    private const string KeyboardSection = "KBD ";

    /// <summary>
    /// The 8237 DMA controller pair: every channel's address, count, page,
    /// mode, mask and terminal-count state, plus both controllers' own
    /// byte-pointer flip-flops. Motherboard silicon rather than part of any
    /// one card (see <see cref="Devices.Cards.MachineBus"/>'s own summary),
    /// so it keeps a fixed section here rather than under a card's
    /// <c>SnapshotId</c>. Restored before the cards -- a card mid-transfer
    /// reads the controller's address and count registers the moment it
    /// resumes, so those must already be in place.
    /// </summary>
    private const string DmaSection = "DMA ";

    /// <summary>The redirector's attachment to DOS; see <see cref="Machine.CaptureDosState"/>.</summary>
    private const string DosSection = "DOS ";

    /// <summary>
    /// PCI configuration space. Version 0 is what files written before the
    /// IDE timing registers existed hold: zero in IDETIM, although both
    /// channels decoded their ports. Version 1 has the registers as they were.
    /// </summary>
    private const string PciSection = "PCI ";
    private const int PciVersionWithIdeTiming = 1;

    /// <summary>
    /// Where USB's state lived before it became a card: the container's own
    /// section, always present (even empty, with no USB fitted) in a file
    /// written before F1.4c. A file with this section and no "UHCI" one --
    /// every file older than this build -- has it renamed onto the card's own
    /// section name by <see cref="MigrateLegacyUsbSection"/>, so <see
    /// cref="RestoreCards"/> and <see cref="Validate"/> need to know only the
    /// one name from here on.
    /// </summary>
    private const string LegacyUsbSection = "USB ";

    /// <summary>
    /// Whether the guest loaded its own CD-ROM driver, one byte. Older files
    /// did not record it, and the absence of the section keeps it unknown
    /// rather than claiming that their guest had no native CD-ROM driver.
    /// </summary>
    private const string CompactDiscSection = "CDRM";

    // Motherboard state is separate from the expansion-card registry. The
    // chipset identity prevents restoring compatible PCI keys onto different silicon.
    private const string ChipsetSection = "CHIP";
    private const string BusMasterIdeSection = "BMID";
    private const string Piix4PowerSection = "PIXP";

    // Expansion cards each keep their own section, named by their
    // IExpansionCard.SnapshotId rather than a name fixed here: the container
    // does not know what cards exist, only that a section it does not
    // recognise is kept and skipped. A card fitted to the machine but absent
    // from the file -- an older snapshot, taken before the card was added --
    // restores to its own power-on state rather than being left as whatever
    // it happened to hold; see RestoreCards below.

    /// <summary>
    /// The first bytes of a sectioned file. A file from before the sections
    /// begins with the length of its RAM as a 32-bit integer; "AURA" read that
    /// way is 0x41525541, which is not a whole number of 4 KB pages and so is
    /// never a RAM size, and the two formats cannot be mistaken for each other.
    /// </summary>
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("AURASNAP");

    // ---- state --------------------------------------------------------------

    /// <summary>One named, versioned piece of the machine's state.</summary>
    private readonly record struct Section(int Version, byte[] Payload);

    private readonly Dictionary<string, Section> _sections = new();

    // ---- capture and restore ------------------------------------------------

    private MachineSnapshot(Machine machine)
    {
        Add(RamSection, 0, machine.Memory.CaptureRam());
        Add(CpuSection, Cpu.Cpu.SnapshotVersion, Serialise(machine.Cpu.WriteSnapshot));
        Add(FpuSection, Cpu.Fpu.X87Unit.SnapshotVersion, Serialise(machine.Cpu.Fpu.WriteSnapshot));
        Add(MsrSection, machine.Cpu.Msrs.SnapshotVersion, Serialise(machine.Cpu.Msrs.WriteSnapshot));
        Add(SseSection, Cpu.Sse.SseUnit.SnapshotVersion, Serialise(machine.Cpu.Sse.WriteSnapshot));
        Add(A20Section, 0, [machine.Memory.A20Enabled ? (byte)1 : (byte)0]);

        Add(VideoMemorySection, 0, machine.VideoMemory.CaptureMemory());
        Add(VideoRegistersSection, 0, machine.VideoRegisters.CaptureRegisters());

        Add(InterruptControllersSection, 0, machine.Interrupts.Capture());
        Add(TimerSection, 0, machine.Timer.Capture());
        Add(KeyboardSection, 0, machine.Keyboard.Capture());
        Add(DmaSection, Devices.System.Dma8237.SnapshotVersion, machine.Dma.Capture());
        Add(DosSection, 0, machine.CaptureDosState());
        Add(PciSection, PciVersionWithIdeTiming, machine.Pci.Capture());
        Add(CompactDiscSection, 0, [machine.GuestHasItsOwnCompactDiscExtensions ? (byte)1 : (byte)0]);
        Add(ChipsetSection, 0, [(byte)machine.Chipset]);
        if (machine.BusMasterIde is { } busMasterIde)
        {
            Add(BusMasterIdeSection, Devices.Storage.BusMasterIdeController.SnapshotVersion,
                busMasterIde.Capture());
        }

        if (machine.Piix4Power is { } piix4Power)
        {
            Add(Piix4PowerSection, 1, piix4Power.Capture());
        }

        foreach (IExpansionCard card in machine.Cards)
        {
            Add(card.SnapshotId, card.SnapshotVersion, card.Capture());
        }
    }

    private MachineSnapshot()
    {
    }

    /// <summary>Takes a snapshot of the machine as it stands.</summary>
    public static MachineSnapshot Capture(Machine machine) => new(machine);

    /// <summary>Puts this state back into a machine.</summary>
    public void RestoreTo(Machine machine)
    {
        // Checked before anything else is touched: a snapshot whose card
        // sections or PCI topology do not match this machine is refused
        // whole, rather than partly restored and left inconsistent -- see
        // ValidateAgainstMachine's own summary.
        ValidateAgainstMachine(machine);

        // Remove the host CD mount before restoring RAM: unmounting clears
        // DOS drive flags, which may already belong to the guest's driver.
        if (_sections.TryGetValue(CompactDiscSection, out Section compactDisc))
        {
            machine.RestoreCompactDiscOwnership(compactDisc.Payload[0] != 0);
        }

        machine.Memory.A20Enabled = _sections[A20Section].Payload[0] != 0;
        machine.Memory.RestoreRam(_sections[RamSection].Payload);

        Section processor = _sections[CpuSection];
        Deserialise(processor, machine.Cpu.ReadSnapshot);

        // After the processor section, which for an older file has already
        // restored or reset the MSRs itself.
        if (_sections.TryGetValue(SseSection, out Section streamingSimd))
        {
            Deserialise(streamingSimd, machine.Cpu.Sse.ReadSnapshot);

            // The receiving processor keeps its model: a Pentium III cannot
            // inherit the Pentium 4's DAZ bit from the saved MXCSR.
            machine.Cpu.Sse.Mxcsr &= Cpu.Sse.SseUnit.MxcsrMaskFor(machine.Cpu.State.Model);
        }
        else
        {
            machine.Cpu.Sse.Reset();
        }

        if (_sections.TryGetValue(MsrSection, out Section modelSpecificRegisters))
        {
            Deserialise(modelSpecificRegisters, machine.Cpu.Msrs.ReadSnapshot);
        }
        else if (processor.Version >= 3)
        {
            // From version 3 on the MSRs live only in their own section; a file
            // that lacks it restores them at power-on values rather than
            // keeping whatever the receiving machine held.
            machine.Cpu.Msrs.Reset();
        }

        // A file from before the coprocessor was captured leaves the unit as
        // it is, which is what restoring such a file always did.
        if (_sections.TryGetValue(FpuSection, out Section coprocessor))
        {
            Deserialise(coprocessor, machine.Cpu.Fpu.ReadSnapshot);
        }

        if (Payload(VideoMemorySection) is { Length: > 0 } videoMemory)
        {
            machine.VideoMemory.RestoreMemory(videoMemory);
        }

        if (Payload(VideoRegistersSection) is { Length: > 0 } videoRegisters)
        {
            machine.VideoRegisters.RestoreRegisters(videoRegisters);
        }

        // The keyboard controller owns A20, so it is restored after the bus:
        // the other order would leave the line as the snapshot's own field
        // said rather than as the controller had it.
        if (Payload(InterruptControllersSection) is { Length: > 0 } interruptControllers)
        {
            machine.Interrupts.Restore(interruptControllers);
        }

        if (Payload(TimerSection) is { Length: > 0 } timer)
        {
            machine.Timer.Restore(timer);
        }

        if (Payload(KeyboardSection) is { Length: > 0 } keyboard)
        {
            machine.Keyboard.Restore(keyboard);
        }

        // After the memory, because attaching marks the CDS entries in it.
        machine.RestoreDosState(Payload(DosSection) ?? []);
        RestorePci(machine);

        // PCI decode is restored before the register blocks. Neither device
        // resumes a transfer merely because a snapshot contained Start=1.
        if (machine.BusMasterIde is { } busMasterIde)
        {
            busMasterIde.Restore(_sections[BusMasterIdeSection].Payload);
        }

        if (machine.Piix4Power is { } piix4Power)
        {
            piix4Power.Restore(_sections[Piix4PowerSection].Payload);
        }

        // Before the cards: a card mid-transfer (the sound card, streaming
        // over channel 1 or 5) reads the controller's address and count
        // registers the moment it next advances, so those must already
        // reflect the snapshot by the time RestoreCards below reaches it. A
        // file older than this section leaves the receiving machine's DMA
        // state as it was, the same tolerance every other optional section
        // gets.
        if (Payload(DmaSection) is { Length: > 0 } dma)
        {
            machine.Dma.Restore(dma);
        }

        RestoreCards(machine);

        // Paging translations cached from the old contents of memory describe
        // an address space that no longer exists.
        machine.Cpu.FlushTlb();
    }

    // ---- files --------------------------------------------------------------

    /// <summary>Writes a snapshot to a file.</summary>
    public void Save(string path)
    {
        using var file = new BinaryWriter(File.Create(path));

        file.Write(Magic);

        foreach ((string name, Section section) in _sections)
        {
            file.Write(Encoding.ASCII.GetBytes(name));
            file.Write((ushort)section.Version);
            file.Write(section.Payload.Length);
            file.Write(section.Payload);
        }
    }

    /// <summary>Reads a snapshot back from a file.</summary>
    public static MachineSnapshot Load(string path)
    {
        using var file = new BinaryReader(File.OpenRead(path));

        byte[] start = file.ReadBytes(Magic.Length);
        bool sectioned = start.AsSpan().SequenceEqual(Magic);

        if (!sectioned)
        {
            file.BaseStream.Position = 0;
            return ReadLegacy(file);
        }

        var snapshot = new MachineSnapshot();

        while (file.BaseStream.Position < file.BaseStream.Length)
        {
            string name = Encoding.ASCII.GetString(file.ReadBytes(4));
            int version = file.ReadUInt16();
            int length = file.ReadInt32();

            // Checked against what is left before anything is allocated, so
            // that a corrupt length is an error rather than an attempt to
            // allocate two gigabytes.
            long remaining = file.BaseStream.Length - file.BaseStream.Position;

            if (length < 0 || length > remaining)
            {
                throw new InvalidDataException($"Snapshot section '{name}' is truncated.");
            }

            snapshot.Add(name, version, file.ReadBytes(length));
        }

        snapshot.MigrateLegacyUsbSection();
        snapshot.Validate();

        return snapshot;
    }

    /// <summary>
    /// The newest version of each section this build can read. A file from a
    /// later build that changed a layout is refused as a whole, when it is
    /// loaded, rather than half restored: by the time a section reader
    /// noticed, RAM would already hold the file's contents and the registers
    /// the machine's own.
    /// </summary>
    private static readonly Dictionary<string, int> NewestVersions = new()
    {
        [RamSection] = 0,
        [CpuSection] = Cpu.Cpu.SnapshotVersion,
        [FpuSection] = Cpu.Fpu.X87Unit.SnapshotVersion,
        [MsrSection] = 2,
        [SseSection] = Cpu.Sse.SseUnit.SnapshotVersion,
        [A20Section] = 0,
        [VideoMemorySection] = 0,
        [VideoRegistersSection] = 0,
        [InterruptControllersSection] = 0,
        [TimerSection] = 0,
        [KeyboardSection] = 0,
        [DmaSection] = Devices.System.Dma8237.SnapshotVersion,
        [DosSection] = 0,
        [PciSection] = PciVersionWithIdeTiming,
        [CompactDiscSection] = 0,
        [ChipsetSection] = 0,
        [BusMasterIdeSection] = Devices.Storage.BusMasterIdeController.SnapshotVersion,
        [Piix4PowerSection] = 1,
    };

    /// <summary>
    /// Payload sizes that are fixed by the version, for the sections read
    /// field by field. A short payload would otherwise fail inside
    /// <see cref="RestoreTo"/>, after the machine had been partly overwritten.
    /// </summary>
    private static int? ExpectedLength(string name, int version) => (name, version) switch
    {
        (CpuSection, 0) => LegacyProcessorLength,
        (CpuSection, 1) => LegacyProcessorLength + 8 * 4 + 2 * 4 + 1 + 1,
        (CpuSection, 2) => LegacyProcessorLength + 8 * 4 + 2 * 4 + 1 + 1
                           + 4 + 8 + Cpu.ModelSpecificRegisters.SnapshotLength,
        (CpuSection, 3) => LegacyProcessorLength + 8 * 4 + 2 * 4 + 1 + 1
                           + 4 + 8 + 4 * 8,
        (MsrSection, 1) => Cpu.ModelSpecificRegisters.SnapshotLength,
        (MsrSection, 2) => Cpu.ModelSpecificRegisters.FamilySixSnapshotLength,
        (SseSection, 1) => Cpu.Sse.SseUnit.SnapshotLength,
        (FpuSection, 1) => 2 + 2 + 1 + 8 * (1 + 8 + 4 + 1 + 1 + 1),
        (FpuSection, 2) => Cpu.Fpu.X87Unit.SnapshotLength,
        (A20Section, _) => 1,
        (CompactDiscSection, _) => 1,
        (ChipsetSection, _) => 1,
        (DmaSection, 1) => Devices.System.Dma8237.SnapshotLength,
        _ => null,
    };

    /// <summary>
    /// Refuses a snapshot this build cannot restore completely: a required
    /// section missing, a known section newer than this build, or a fixed-size
    /// section of the wrong size. Sections with unknown names are kept and
    /// ignored.
    /// </summary>
    private void Validate()
    {
        RequireSection(RamSection);
        RequireSection(CpuSection);
        RequireSection(A20Section);

        foreach ((string name, Section section) in _sections)
        {
            if (NewestVersions.TryGetValue(name, out int newest))
            {
                if (name == MsrSection && section.Version < 1)
                {
                    throw new InvalidDataException("Snapshot section 'MSR ' has no version 0.");
                }

                if (section.Version > newest)
                {
                    throw new InvalidDataException(
                        $"Snapshot section '{name}' is version {section.Version}; this build reads up to {newest}.");
                }

                if (ExpectedLength(name, section.Version) is int expected && section.Payload.Length != expected)
                {
                    throw new InvalidDataException(
                        $"Snapshot section '{name}' holds {section.Payload.Length} bytes, not {expected}.");
                }

                if (name == BusMasterIdeSection)
                {
                    if (section.Version != Devices.Storage.BusMasterIdeController.SnapshotVersion)
                    {
                        throw new InvalidDataException("Unsupported bus-master IDE section version.");
                    }

                    Devices.Storage.BusMasterIdeController.ValidateSnapshot(section.Payload);
                }

                if (name == Piix4PowerSection)
                {
                    if (section.Version != 1)
                    {
                        throw new InvalidDataException("Unsupported PIIX4 power-management section version.");
                    }

                    Devices.Chipset.Piix4PowerManagement.ValidateSnapshot(section.Payload);
                }

                continue;
            }

            // An expansion card's own section: not one of the container's
            // fixed names above, but not necessarily unknown either. Before
            // this check a card section was skipped exactly like a name this
            // build has genuinely never heard of, which meant a file written
            // by a newer build -- a card snapshot layout changed underneath
            // it -- restored silently wrong data into RAM the machine already
            // held by the time anything noticed. See CardCatalog's own
            // summary for why a card is keyed by its SnapshotId here, the
            // same way MachineSnapshot's own sections are.
            if (CardCatalog.NewestSnapshotVersions.TryGetValue(name, out int cardNewest))
            {
                if (section.Version > cardNewest)
                {
                    throw new InvalidDataException(
                        $"Snapshot section '{name}' is version {section.Version}; this build reads up to {cardNewest}.");
                }

                if (CardCatalog.FixedSnapshotLengths.TryGetValue(name, out int cardExpected)
                    && section.Payload.Length != cardExpected)
                {
                    throw new InvalidDataException(
                        $"Snapshot section '{name}' holds {section.Payload.Length} bytes, not {cardExpected}.");
                }

                // Reject an invalid embedded core or page-3 state before any
                // machine RAM is restored, not halfway through card restoration.
                if (name == Rtl8029Card.SnapshotSectionId)
                {
                    Devices.Network.Rtl8029.ValidateSnapshot(section.Payload, section.Version);
                }

                if (name == Mpu401Card.SnapshotSectionId)
                {
                    Devices.Audio.Mpu401Uart.ValidateSnapshot(section.Payload, section.Version);
                }
            }

            // A name neither the container nor any known card recognises is
            // kept and skipped, as before: most likely a section written by a
            // later build for something that does not exist here yet.
        }
    }

    /// <summary>
    /// Reads a file written before snapshots had sections.
    ///
    /// That format was one stream of fields, extended over time by appending:
    /// RAM, the processor, A20, then the video card, then the interrupt
    /// controllers, timer and keyboard, then DOS, PCI and USB, then the CD-ROM
    /// ownership and whether IDE timing was recorded. A file that stops early
    /// was written before the later groups existed, and those sections are
    /// simply absent. The processor fields are exactly version 0 of the CPU
    /// section, so they are copied across without being interpreted here.
    /// </summary>
    private static MachineSnapshot ReadLegacy(BinaryReader file)
    {
        var snapshot = new MachineSnapshot();

        snapshot.Add(RamSection, 0, file.ReadBytes(file.ReadInt32()));
        snapshot.Add(CpuSection, 0, file.ReadBytes(LegacyProcessorLength));
        snapshot.Add(A20Section, 0, [file.ReadByte()]);

        if (!AtEnd(file))
        {
            snapshot.Add(VideoMemorySection, 0, ReadCounted(file));
            snapshot.Add(VideoRegistersSection, 0, ReadCounted(file));
        }

        if (!AtEnd(file))
        {
            snapshot.Add(InterruptControllersSection, 0, ReadCounted(file));
            snapshot.Add(TimerSection, 0, ReadCounted(file));
            snapshot.Add(KeyboardSection, 0, ReadCounted(file));
        }

        // Each of these three was appended on its own, so each may be the
        // last thing in the file.
        foreach (string name in new[] { DosSection, PciSection, LegacyUsbSection })
        {
            if (!AtEnd(file))
            {
                snapshot.Add(name, 0, ReadCounted(file));
            }
        }

        if (!AtEnd(file))
        {
            snapshot.Add(CompactDiscSection, 0, [file.ReadByte()]);
        }

        bool hasIdeTimingRegisters = !AtEnd(file) && file.ReadBoolean();

        if (hasIdeTimingRegisters && snapshot._sections.TryGetValue(PciSection, out Section pci))
        {
            snapshot._sections[PciSection] = pci with { Version = PciVersionWithIdeTiming };
        }

        // A file cut short inside the processor block reads fewer bytes than
        // the block holds; this is where that is caught, before any restore.
        snapshot.MigrateLegacyUsbSection();
        snapshot.Validate();

        return snapshot;
    }

    /// <summary>
    /// The size of the processor fields in a file from before sections:
    /// eight registers, six segment caches of fifteen bytes, the lazy flags,
    /// then EIP, the control registers, the descriptor tables, the task
    /// register, EFLAGS' other bits, CPL, halted and V86.
    /// </summary>
    private const int LegacyProcessorLength =
        8 * 4
        + 6 * (2 + 4 + 4 + 1 + 1 + 1 + 1 + 1)
        + (1 + 4 + 4 + 4 + 4 + 1)
        + 4 * 4
        + (4 + 2) + (4 + 2)
        + (2 + 4 + 4) + (2 + 4 + 4)
        + 4 + 1 + 1 + 1;

    // ---- helpers ------------------------------------------------------------

    private void Add(string name, int version, byte[] payload)
        => _sections[name] = new Section(version, payload);

    private byte[]? Payload(string name)
        => _sections.TryGetValue(name, out Section section) ? section.Payload : null;

    private void RequireSection(string name)
    {
        if (!_sections.ContainsKey(name))
        {
            throw new InvalidDataException($"Snapshot has no '{name}' section.");
        }
    }

    /// <summary>
    /// Refuses a snapshot whose expansion cards or PCI topology do not match
    /// this machine, before <see cref="RestoreTo"/> has touched A20, RAM or
    /// anything else -- so a mismatch leaves the machine exactly as it was,
    /// rather than partly restored.
    ///
    /// Three shapes of mismatch are caught. A section named after a card's
    /// <c>SnapshotId</c> (see <see cref="CardCatalog.NewestSnapshotVersions"/>)
    /// that the machine has no matching card for: the file was taken from a
    /// machine with a card -- USB fitted at run time, say -- this one lacks.
    /// A card the machine does have, with no section for it, in a file that
    /// is otherwise from the card era: a genuine mismatch, not tolerated the
    /// way a file older than the card sections is. And a PCI function list
    /// that differs in count or address from the machine's own, which <see
    /// cref="Devices.Pci.PciBus.Restore"/> would otherwise only notice after
    /// RAM had already been overwritten.
    ///
    /// A file older than the card sections -- one with none of them at all --
    /// keeps today's tolerance: every card on such a machine restores to its
    /// own power-on state via <see cref="RestoreCards"/>, exactly as it did
    /// before this check existed.
    /// </summary>
    private void ValidateAgainstMachine(Machine machine)
    {
        // Files predating motherboard selection always describe i430FX.
        byte chipset = _sections.TryGetValue(ChipsetSection, out Section motherboard)
            ? motherboard.Payload[0]
            : (byte)Devices.Chipset.Chipset.I430Fx;
        if (chipset != (byte)machine.Chipset)
        {
            throw new InvalidDataException("Snapshot chipset differs from the machine's.");
        }

        bool hasPiix4 = machine.Chipset == Devices.Chipset.Chipset.I430Tx;
        if (_sections.ContainsKey(BusMasterIdeSection) != hasPiix4 ||
            _sections.ContainsKey(Piix4PowerSection) != hasPiix4)
        {
            throw new InvalidDataException("Snapshot is missing or has unexpected PIIX4 device state.");
        }

        if (machine.BusMasterIde is { } busMasterIde)
        {
            busMasterIde.ValidateSnapshotAgainstDrives(_sections[BusMasterIdeSection].Payload);
        }

        if (_sections.TryGetValue(PciSection, out Section pci) && pci.Payload.Length > 0)
        {
            IReadOnlyList<ushort> fileFunctions = Devices.Pci.PciBus.ReadFunctionKeys(pci.Payload);
            var machineFunctions = new HashSet<ushort>(machine.Pci.Functions.Select(function => function.Address));

            if (fileFunctions.Count != machineFunctions.Count || fileFunctions.Any(key => !machineFunctions.Contains(key)))
            {
                throw new InvalidDataException(
                    "Snapshot's PCI topology differs from the machine's; refusing to restore.");
            }
        }

        // A section is only "there" the same way RestoreCards treats one: a
        // non-empty payload. A section key with zero bytes is what an old
        // container-level "USB " section becomes for a file that had no USB
        // card at all -- MigrateLegacyUsbSection renames it onto "UHCI"
        // regardless -- and must not be read as "this file is from the card
        // era" or "this file names a UHCI card".
        bool HasRealSection(string id) => _sections.TryGetValue(id, out Section section) && section.Payload.Length > 0;

        // "From the card era": the file genuinely captured at least one of
        // the cards it keeps a section for, so a card the machine has but
        // the file does not mention is a real mismatch rather than simply a
        // file older than any of them.
        bool fileHasCardSections = CardCatalog.NewestSnapshotVersions.Keys.Any(HasRealSection);

        foreach (IExpansionCard card in machine.Cards)
        {
            if (fileHasCardSections && !HasRealSection(card.SnapshotId))
            {
                throw new InvalidDataException(
                    $"Snapshot has no '{card.SnapshotId}' section for the {card.Kind} card fitted to this machine.");
            }

            if (card is Mpu401Card midi && HasRealSection(card.SnapshotId))
            {
                Section midiState = _sections[card.SnapshotId];
                midi.Device.ValidateSnapshotForDevice(midiState.Payload, midiState.Version);
            }
        }

        var machineCardIds = new HashSet<string>(machine.Cards.Select(card => card.SnapshotId));
        foreach (string cardId in CardCatalog.NewestSnapshotVersions.Keys)
        {
            if (HasRealSection(cardId) && !machineCardIds.Contains(cardId))
            {
                throw new InvalidDataException(
                    $"Snapshot has a '{cardId}' section, but this machine has no matching card fitted.");
            }
        }
    }

    /// <summary>
    /// Puts PCI configuration back and repairs what files of version 0 got
    /// wrong: they stored zero in the unimplemented IDETIM registers while
    /// both channels actually decoded their ports.
    /// </summary>
    private void RestorePci(Machine machine)
    {
        _sections.TryGetValue(PciSection, out Section pci);
        machine.Pci.Restore(pci.Payload ?? []);

        if (pci.Version < PciVersionWithIdeTiming)
        {
            // IDETIM of the PIIX IDE function (device 1, function 1), both
            // channels with their decode enable bit set.
            machine.Pci.WriteConfiguration(0x0009, 0x40, 4, 0x80008000);
        }
    }

    /// <summary>
    /// Renames a file's "USB " section, if it has one, onto the "UHCI" name
    /// <see cref="UhciCard"/> keeps its own state under, so <see
    /// cref="RestoreCards"/> and <see cref="Validate"/> need only ever look
    /// for the one name. Called right after a file's sections are read, both
    /// for the sectioned format and for <see cref="ReadLegacy"/>, before
    /// either validates or restores anything.
    ///
    /// The two names' payloads are byte-for-byte the same format -- the two
    /// controllers' state, length-prefixed, one after the other -- because
    /// USB moving from the container into a card changed only where its
    /// bytes are kept, not their shape. A file that somehow already has a
    /// "UHCI" section (none does yet) keeps it and the legacy section is
    /// simply dropped, rather than overwriting a newer capture with an older
    /// one.
    /// </summary>
    private void MigrateLegacyUsbSection()
    {
        if (_sections.Remove(LegacyUsbSection, out Section legacy) && !_sections.ContainsKey(UhciCard.SnapshotSectionId))
        {
            _sections[UhciCard.SnapshotSectionId] = legacy;
        }
    }

    /// <summary>
    /// Restores every card fitted from the section named after its own <see
    /// cref="IExpansionCard.SnapshotId"/>, or resets it when the file has
    /// none -- a file from before the card existed, before this package
    /// started capturing it, or (for USB) an old file whose section <see
    /// cref="MigrateLegacyUsbSection"/> did not find because the machine has
    /// no USB card fitted to look for one under either name.
    /// </summary>
    private void RestoreCards(Machine machine)
    {
        foreach (IExpansionCard card in machine.Cards)
        {
            if (_sections.TryGetValue(card.SnapshotId, out Section section) && section.Payload.Length > 0)
            {
                card.Restore(section.Payload, section.Version);
            }
            else
            {
                card.Reset();
            }
        }
    }

    private static byte[] Serialise(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        write(writer);
        writer.Flush();

        return stream.ToArray();
    }

    private static void Deserialise(Section section, Action<BinaryReader, int> read)
    {
        using var reader = new BinaryReader(new MemoryStream(section.Payload));
        read(reader, section.Version);
    }

    private static bool AtEnd(BinaryReader file)
        => file.BaseStream.Position >= file.BaseStream.Length;

    private static byte[] ReadCounted(BinaryReader file)
        => file.ReadBytes(file.ReadInt32());
}
