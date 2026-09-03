using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Dos;

/// <summary>
/// Finds out where DOS keeps the structures a redirector has to write to.
///
/// A redirector on real hardware was a TSR: it loaded from CONFIG.SYS or
/// AUTOEXEC.BAT, asked DOS for the addresses it needed, hooked INT 2Fh and
/// stayed resident. We have no TSR to load -- our INT 2Fh handler is already
/// in the machine, written in C# -- but we still have to ask DOS the same two
/// questions, and there is no way to ask them except by making the calls from
/// inside the guest.
///
/// So this class does the one thing a TSR would have done and nothing more:
/// it borrows the CPU for a few dozen instructions, runs a short routine that
/// issues the two INT 21h calls, reads the answers out of the registers and
/// puts the machine back exactly as it found it. The routine lives in the
/// interrupt-vector table's unused tail, the region between 0400h and 0500h
/// that the BIOS data area does not fill and that every DOS leaves alone.
///
/// The two questions are:
///
///   INT 21h AX=5D06  returns DS:SI pointing at the Swappable Data Area.
///                    This one call is the only officially blessed part of
///                    the whole interface, published so that multitaskers
///                    could save and restore DOS's state.
///
///   INT 21h AH=52h   returns ES:BX pointing at the "list of lists", DOS's
///                    master table of internal pointers. The address of the
///                    Current Directory Structure array sits at offset 16h
///                    within it, and the number of entries in that array --
///                    LASTDRIVE -- at offset 21h. This one was never
///                    documented at all, and the offsets differ between DOS
///                    versions; 16h and 21h are correct from 3.1 onwards,
///                    which covers everything that can run on a 386.
///
/// The LASTDRIVE count is not a detail. DOS sizes the CDS array from the
/// LASTDRIVE line in CONFIG.SYS and defaults it to five, and the array is
/// followed immediately by other DOS structures. Writing an entry past the
/// end -- which is exactly what mounting a drive letter DOS has no room for
/// would do -- corrupts whatever lives there, and the damage surfaces much
/// later and somewhere else entirely.
///
/// Doing this before DOS has loaded finds nothing, which is why the attempt
/// is made lazily and repeated until it works.
/// </summary>
public sealed class RedirectorInstaller(Machine machine)
{
    private readonly Machine _machine = machine;

    /// <summary>
    /// Where the borrowed routine is planted. 0000:04F0 is inside the 256
    /// bytes above the BIOS data area that hold nothing on any PC: the print
    /// screen flag ends at 0500h and the region below it is padding.
    /// </summary>
    private const uint RoutineAddress = 0x04F0;

    /// <summary>Offset within the list of lists where the CDS array's address sits.</summary>
    private const uint ListOfListsCdsOffset = 0x16;

    /// <summary>Offset of the LASTDRIVE byte: how many entries the array has.</summary>
    private const uint ListOfListsLastDriveOffset = 0x21;

    /// <summary>Address of DOS's list of lists, for diagnostics.</summary>
    public uint ListOfListsAddress { get; private set; }

    /// <summary>
    /// Tries to learn the addresses. Returns true when it succeeded, which it
    /// will not do until DOS is up.
    /// </summary>
    public bool TryInstall(RedirectorServices redirector)
    {
        if (redirector.IsInstalled) return true;
        if (!_machine.Booted) return false;

        // A guest that is not in real mode cannot be interrupted this way:
        // the INT would go through the protected-mode descriptor tables, into
        // whatever the guest has installed there, with a stack we do not
        // control. Windows in enhanced mode is the case that matters, and the
        // right time to install is long before it starts.
        if (_machine.Cpu.State.ProtectedMode) return false;

        SavedContext saved = SaveContext();
        byte[] savedMemory = ReadRoutineArea();

        try
        {
            uint sda = CallForSwappableDataArea();
            if (sda == 0) return false;

            (uint cds, byte driveCount) = CallForCurrentDirectoryStructure();
            if (cds == 0) return false;

            redirector.AttachToDos(sda, cds, driveCount);
            return true;
        }
        finally
        {
            WriteRoutineArea(savedMemory);
            RestoreContext(saved);
        }
    }

    /// <summary>
    /// Runs INT 21h AX=5D06 in the guest and reads DS:SI back.
    /// </summary>
    private uint CallForSwappableDataArea()
    {
        //   B8 06 5D    MOV AX,5D06
        //   CD 21       INT 21h
        //   F4          HLT       -- the marker we stop at
        RunRoutine([0xB8, 0x06, 0x5D, 0xCD, 0x21, 0xF4]);

        CpuNs.CpuState state = _machine.Cpu.State;

        // Carry set means the call failed, and on a DOS too old to have the
        // function it will. The register pair is meaningless then.
        if (_machine.Cpu.Flags.Carry) return 0;

        uint address = ((uint)state.Ds.Selector << 4) + state.Si;

        // A pointer into the first 64 KB with nothing before it is not an
        // SDA: DOS itself is loaded well above the vector table, and a zero
        // segment means the call did nothing.
        return state.Ds.Selector == 0 ? 0 : address;
    }

    /// <summary>
    /// Runs INT 21h AH=52h and follows the list of lists to the CDS array,
    /// returning its address and how many entries it holds.
    /// </summary>
    private (uint address, byte driveCount) CallForCurrentDirectoryStructure()
    {
        //   B4 52       MOV AH,52
        //   CD 21       INT 21h
        //   F4          HLT
        RunRoutine([0xB4, 0x52, 0xCD, 0x21, 0xF4]);

        CpuNs.CpuState state = _machine.Cpu.State;
        if (state.Es.Selector == 0 && state.Bx == 0) return (0, 0);

        uint listOfLists = ((uint)state.Es.Selector << 4) + state.Bx;
        ListOfListsAddress = listOfLists;

        ushort offset = _machine.Memory.ReadWord(listOfLists + ListOfListsCdsOffset);
        ushort segment = _machine.Memory.ReadWord(listOfLists + ListOfListsCdsOffset + 2);

        // DOS writes 0000:FFFF into the slot when there are no drives at all,
        // which happens if LASTDRIVE is somehow zero. Following it would give
        // us a CDS array in the middle of the interrupt vectors.
        if (segment == 0 && offset == 0xFFFF) return (0, 0);
        if (segment == 0) return (0, 0);

        byte driveCount = _machine.Memory.ReadByte(listOfLists + ListOfListsLastDriveOffset);

        // A count of zero or an absurd one means we are not looking at a list
        // of lists at all, and every entry we computed from it would be a
        // guess at an address in the middle of DOS.
        if (driveCount is 0 or > 26) return (0, 0);

        return (((uint)segment << 4) + offset, driveCount);
    }

    /// <summary>
    /// Plants a short routine in low memory, runs it to its HLT and returns.
    ///
    /// The stack is moved to a scratch area rather than left where the guest
    /// had it: DOS is entered here at a moment of our choosing, and pushing an
    /// interrupt frame onto whatever stack the guest happened to be using can
    /// land on data the guest is in the middle of reading. Half a kilobyte
    /// below the routine is nobody's.
    /// </summary>
    private void RunRoutine(byte[] code)
    {
        for (int i = 0; i < code.Length; i++)
            _machine.Memory.WriteByte(RoutineAddress + (uint)i, code[i]);

        CpuNs.Cpu cpu = _machine.Cpu;

        cpu.State.Halted = false;
        cpu.SetSegment(CpuNs.SegReg.Cs, 0);
        cpu.State.Eip = RoutineAddress;
        cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        cpu.State.Esp = RoutineAddress - 0x10;

        // A generous but finite budget. DOS's own INT 21h dispatch for these
        // two functions is a few hundred instructions; anything that has not
        // finished in this many has gone somewhere it should not, and the
        // right response is to give up rather than to run forever.
        for (int step = 0; step < 200_000; step++)
        {
            if (cpu.State.Halted) return;
            cpu.Step();
        }
    }

    private byte[] ReadRoutineArea()
    {
        var saved = new byte[16];
        for (uint i = 0; i < saved.Length; i++)
            saved[i] = _machine.Memory.ReadByte(RoutineAddress + i);
        return saved;
    }

    private void WriteRoutineArea(byte[] saved)
    {
        for (uint i = 0; i < saved.Length; i++)
            _machine.Memory.WriteByte(RoutineAddress + i, saved[i]);
    }

    /// <summary>
    /// Everything the borrowed routine could disturb, kept so the guest
    /// cannot tell that anything happened. This is the same job a multitasker
    /// does around a task switch, and for the same reason.
    /// </summary>
    private sealed record SavedContext(uint[] Registers, CpuNs.SegmentCache[] Segments,
                                       uint Eip, uint Flags, bool Halted);

    private SavedContext SaveContext()
    {
        CpuNs.CpuState state = _machine.Cpu.State;
        return new SavedContext(
            (uint[])state.Gpr.Clone(),
            (CpuNs.SegmentCache[])state.Segments.Clone(),
            state.Eip,
            _machine.Cpu.Flags.Value,
            state.Halted);
    }

    private void RestoreContext(SavedContext saved)
    {
        CpuNs.CpuState state = _machine.Cpu.State;

        Array.Copy(saved.Registers, state.Gpr, state.Gpr.Length);
        Array.Copy(saved.Segments, state.Segments, state.Segments.Length);

        state.Eip = saved.Eip;
        state.Halted = saved.Halted;
        _machine.Cpu.Flags.SetValue(saved.Flags);
    }
}
