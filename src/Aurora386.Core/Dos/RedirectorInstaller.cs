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

    /// <summary>
    /// The stack the borrowed routine runs on, and it deliberately does not
    /// grow down from the routine itself.
    ///
    /// INT 21h is not a shallow call: DOS pushes a great deal, and the two
    /// functions used here go several frames deep. A stack placed just below
    /// the routine would grow straight down through 0400h-04FFh, which is the
    /// extended BIOS data area -- serial and parallel port addresses, video
    /// state, the keyboard's own variables. Overwriting them does not fail
    /// visibly: the machine carries on and then behaves oddly much later,
    /// which is exactly the kind of damage that takes days to trace back.
    ///
    /// So the stack goes in the emulator's own scratch area, well above
    /// anything the guest uses: the two kilobytes below 0x1000, which sit
    /// between the DOS data area and the point where the system loads itself.
    ///
    /// It emphatically does not go at 7C00h, which is the obvious-looking
    /// choice and a trap. Nothing lives there once DOS is running, which is
    /// what makes it tempting -- but that is exactly where the BIOS loads the
    /// boot sector, and where the system keeps working until it has moved
    /// itself into place. A stack there, growing downwards during startup,
    /// writes over the system while it is still loading: the machine prints
    /// its first line and stops, with nothing at all to say what happened.
    ///
    /// The area is saved and restored around every call, like the routine
    /// itself, so borrowing it costs the guest nothing.
    /// </summary>
    private const uint StackAddress = 0x1000;

    /// <summary>How much of the guest's memory the borrowed stack may use.</summary>
    private const int StackSize = 0x400;

    /// <summary>Offset within the list of lists where the CDS array's address sits.</summary>
    private const uint ListOfListsCdsOffset = 0x16;

    /// <summary>
    /// Offset of the LASTDRIVE byte: how many entries the CDS array has.
    ///
    /// Not to be confused with the byte immediately before it at 0x20, which
    /// counts block devices -- three on a machine with two floppies and one
    /// disk. The two are easy to mistake for each other because they agree on
    /// a default configuration, and because a redirector that reads the wrong
    /// one still works for C: and fails only on the letters that matter.
    ///
    /// Measured on MS-DOS 6.22 with LASTDRIVE=Z in CONFIG.SYS: 0x20 reads 3
    /// and 0x21 reads 0x1A, which is 26, which is Z. What makes this
    /// genuinely confusing is that 0x21 reads 3 as well if it is consulted
    /// early enough -- before CONFIG.SYS has been processed, DOS is still
    /// using a provisional array with one entry per physical drive, and every
    /// field describes that array truthfully. The offset is right; the moment
    /// is wrong. That is what the re-checking above exists to fix.
    /// </summary>
    private const uint ListOfListsLastDriveOffset = 0x21;

    /// <summary>Address of DOS's list of lists, for diagnostics.</summary>
    public uint ListOfListsAddress { get; private set; }

    /// <summary>
    /// Learns, or re-learns, where DOS keeps its structures. Returns true when
    /// the addresses are known.
    ///
    /// It has to be able to re-learn, and this is not a nicety. DOS builds a
    /// small Current Directory Structure array early -- one entry per physical
    /// drive -- and then throws it away and allocates a bigger one when
    /// CONFIG.SYS is processed and LASTDRIVE is known. A redirector that asks
    /// once, during the boot sector's first few seconds, gets the address of
    /// the array that is about to be discarded, and every flag it sets goes
    /// into memory DOS has handed to something else. Asking again and
    /// noticing the array has moved is the only way to be right.
    /// </summary>
    public bool TryInstall(RedirectorServices redirector, bool force = false)
    {
        if (redirector.IsInstalled && !force) return true;
        if (!_machine.Booted) return false;

        // DOS is not reentrant, and this is the flag that says so.
        //
        // The first two bytes of the Swappable Data Area are the critical
        // error flag and the InDOS flag: DOS raises InDOS on entry to INT 21h
        // and lowers it on the way out, and every TSR ever written checked it
        // before calling DOS from an interrupt. Calling in while it is up
        // corrupts DOS's own state -- it has one set of internal stacks and
        // one scratch area, and a second call scribbles over what the first
        // was using. The symptom is not an error but a machine that stops
        // partway through booting, with nothing to say why.
        //
        // On the first attempt the SDA address is not known yet, so there is
        // nothing to check; that call happens from the run loop between
        // instructions, which is as safe a moment as any.
        if (redirector.SwappableDataArea != 0 && IsDosBusy(redirector)) return false;

        // A halted CPU is waiting for an interrupt, and the guest expects to
        // resume at the instruction after the HLT once one arrives. Clearing
        // the halt to run our own code and then restoring it works, but the
        // interrupt that would have woken the guest can arrive while we are
        // inside DOS -- and be handled there, on our stack, in the middle of
        // a call the guest knows nothing about. Waiting for a moment when the
        // guest is actually running costs nothing: it is running almost all
        // of the time.
        if (_machine.Cpu.State.Halted) return false;

        // Do not go near DOS until it has finished starting.
        //
        // The interrupt vector is filled long before the system behind it can
        // answer: the kernel installs its handlers within the first fifty
        // thousand instructions and then spends eighty million more loading
        // drivers, relocating itself and reading CONFIG.SYS. A call arriving
        // in that stretch reaches a system with no stack to spare and no
        // structures in their final place, and DOS treats the muddle as a
        // critical error -- the "Abort, Retry, Ignore?" handler, which waits
        // for a reply nobody will give. The machine stops with its first line
        // of output on screen and nothing to explain it.
        //
        // Measured on a real boot: asking at 53,200 instructions is what
        // caused that. The command interpreter is not resident until around
        // eighty million.
        //
        // Waiting a fixed number of instructions would be guesswork about
        // someone else's machine. What can be observed instead is the
        // keyboard: DOS reads a key only from the command prompt, so the
        // moment the guest has asked for one, startup is over. The BIOS's own
        // buffer pointers are the evidence, and they cost nothing to read.
        if (!_machine.Bda.WaitingAtPrompt) return false;

        // Wait until DOS is not merely present but finished starting.
        //
        // The interrupt vector exists long before the system behind it is
        // ready: the kernel installs its handlers early and then spends a
        // good while loading drivers, relocating itself and reading
        // CONFIG.SYS. Calling INT 21h during that stretch reaches a system
        // that cannot answer, and DOS treats the confusion as a critical
        // error -- the "Abort, Retry, Ignore?" handler, which then waits for
        // a reply that no one will give. The machine stops with its first
        // line of output on screen and nothing to explain it.
        //
        // COMMAND.COM taking over is the signal that startup is done, and the
        // evidence is INT 2Eh, the back door it owns for running a command
        // line. Testing that the vector is merely non-zero proves nothing --
        // our own BIOS fills it during the POST, and DOS points it at itself
        // long before it is ready. What matters is where it points: once
        // COMMAND.COM is resident the vector names a segment of its own, well
        // above the kernel.
        //
        // Measured on a real boot, the vector moves five times: from the
        // BIOS, twice within the kernel as it loads, again when DOS relocates
        // itself high, and finally to the command interpreter eighty million
        // instructions in. Only that last move means the machine is ready to
        // be asked anything.

        // A guest that is not in real mode cannot be interrupted this way:
        // the INT would go through the protected-mode descriptor tables, into
        // whatever the guest has installed there, with a stack we do not
        // control. Windows in enhanced mode is the case that matters, and the
        // right time to install is long before it starts.
        if (_machine.Cpu.State.ProtectedMode) return false;

        SavedContext saved = SaveContext();
        byte[] savedMemory = ReadRoutineArea();
        byte[] savedStack = ReadStackArea();

        try
        {
            uint sda = CallForSwappableDataArea();
            if (sda == 0) return false;

            (uint cds, byte driveCount) = CallForCurrentDirectoryStructure();
            if (cds == 0) return false;

            if (sda == redirector.SwappableDataArea
                && cds == redirector.CurrentDirectoryStructure
                && driveCount == redirector.DriveCount)
            {
                // Nothing moved -- but that alone is not enough, and this is
                // the mistake that cost the redirector its whole reason for
                // existing.
                //
                // DOS builds a small directory array first, one entry per
                // physical drive, and that array sits perfectly still for a
                // long stretch BEFORE CONFIG.SYS is read. "Nothing moved"
                // therefore describes a DOS that has finished and a DOS that
                // has not started in exactly the same words. Hooking in the
                // second case stops CONFIG.SYS loading its device drivers --
                // COUNTRY.SYS and DISPLAY.SYS fail and the machine never
                // reaches COMMAND.COM at all.
                //
                // The command processor starting is the signal that tells the
                // two apart, because CONFIG.SYS is finished by definition
                // once DOS gets that far.
                if (_machine.CommandProcessorStarted)
                    _machine.HookMultiplexForRedirector();

                return true;
            }

            redirector.AttachToDos(sda, cds, driveCount);

            // A DOS whose structures have moved is a different DOS -- one
            // still building itself, or one that has rebooted -- and the
            // hook it gets must be a fresh one.
            _machine.ForgetMultiplexHook();

            // Deliberately NOT hooking the vector here. The addresses have
            // just changed, which means DOS is still building itself -- and
            // hooking INT 2Fh while it is doing that stops CONFIG.SYS from
            // loading its device drivers. The hook waits until the addresses
            // have stopped moving; the run loop decides when that is.
            return true;
        }
        finally
        {
            WriteRoutineArea(savedMemory);
            WriteStackArea(savedStack);
            RestoreContext(saved);
        }
    }

    /// <summary>
    /// True while DOS is inside an INT 21h call of its own.
    ///
    /// The critical error flag is checked alongside InDOS because DOS raises
    /// it while a critical-error handler is running -- a drive not ready, a
    /// write-protected disk -- and reentering then is worse still: the
    /// handler is about to make decisions on behalf of a call that is already
    /// in flight.
    /// </summary>
    private bool IsDosBusy(RedirectorServices redirector)
    {
        byte criticalError = _machine.Memory.ReadByte(redirector.SwappableDataArea);
        byte inDos = _machine.Memory.ReadByte(redirector.SwappableDataArea + 1);

        return criticalError != 0 || inDos != 0;
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

        // The list of lists belongs to the DOS kernel, which lives low in
        // memory -- in the first 64 KB when it loads normally, and in the
        // high memory area when CONFIG.SYS says DOS=HIGH. An address up in
        // the hundreds of kilobytes is where loaded drivers and programs sit,
        // never the kernel.
        //
        // This matters because the check above is weak on its own: run the
        // interrupt before DOS exists and it returns whatever the registers
        // happened to hold, which can easily point somewhere with a plausible
        // small number in it. That is exactly what happened -- the redirector
        // installed itself during the BIOS's own startup, twenty thousand
        // instructions in, against a byte that read 3.
        const uint HighestPlausibleKernel = 0x20000;
        if (listOfLists >= HighestPlausibleKernel && listOfLists < 0xFFFF0) return (0, 0);

        return (((uint)segment << 4) + offset, driveCount);
    }

    /// <summary>
    /// Plants a short routine in low memory, runs it to its HLT and returns.
    ///
    /// The stack is moved to a scratch area rather than left where the guest
    /// had it: DOS is entered here at a moment of our choosing, and pushing an
    /// interrupt frame onto whatever stack the guest happened to be using can
    /// land on data the guest is in the middle of reading.
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
        cpu.State.Esp = StackAddress;

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

    /// <summary>
    /// Saves the memory the borrowed stack will grow into.
    ///
    /// The stack has to be put back as carefully as the routine: DOS pushes a
    /// great deal on the way through INT 21h, and whatever it writes there is
    /// on top of memory that belonged to the guest a moment earlier.
    /// </summary>
    private byte[] ReadStackArea()
    {
        var saved = new byte[StackSize];
        for (uint i = 0; i < saved.Length; i++)
            saved[i] = _machine.Memory.ReadByte(StackAddress - StackSize + i);
        return saved;
    }

    private void WriteStackArea(byte[] saved)
    {
        for (uint i = 0; i < saved.Length; i++)
            _machine.Memory.WriteByte(StackAddress - StackSize + i, saved[i]);
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
