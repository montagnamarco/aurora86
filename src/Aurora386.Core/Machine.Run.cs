using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Timing;
using Aurora386.Core.Devices.System;
using Aurora386.Core.Devices.Network;
using Aurora386.Core.Devices.Audio;
using Aurora386.Core.Devices.Video;
using Aurora386.Core.Dos;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core;

/// <summary>
/// The complete emulated PC: CPU, memory, devices and BIOS wired together and
/// ready to execute. This is the class that decides what kind of machine we
/// are pretending to be.
/// </summary>
/// <summary>
/// The execution loop: instruction batches, device time, and the housekeeping
/// done between batches (mouse events, vectors reclaimed from DOS, the
/// redirector's installation).
/// </summary>
public sealed partial class Machine
{

    // --- execution loop --------------------------------------------------------

    /// <summary>
    /// Executes the number of instructions corresponding to the given span of
    /// emulated time, advancing the devices in step.
    /// </summary>
    public void RunFor(TimeSpan duration)
    {
        long instructions = (long)(CpuFrequencyHz * duration.TotalSeconds);
        RunInstructions(instructions);
    }

    /// <summary>
    /// Executes a fixed number of instructions. Devices are advanced in
    /// batches rather than after every instruction: ticking a timer 25 million
    /// times a second would cost more than the emulation it is supposed to be
    /// keeping time for.
    /// </summary>
    public void RunInstructions(long count)
    {
        const int chunk = 2000;
        long executed = 0;

        while (executed < count)
        {
            int steps = (int)Math.Min(chunk, count - executed);

            for (int i = 0; i < steps; i++)
            {
                if (Cpu.State.Halted && !Interrupts.HasPendingInterrupt)
                {
                    // Halted, waiting for an interrupt. There is no point
                    // re-executing HLT, but this batch must still count as
                    // elapsed time, because advancing the devices is the only
                    // thing that can produce the interrupt that will wake the
                    // CPU back up. Skipping the device advance here would wedge
                    // the machine permanently.
                    break;
                }
                Cpu.StepWithInterrupts();
            }

            executed += steps;
            AdvanceDevices(steps);
            TryInstallRedirector();
            TryReclaimMouseVector();
            TryRefreshCompactDiscLetter();

            // The pointer's position is brought up to date here, on the
            // emulation thread, and never from the renderer: the renderer
            // runs on the interface thread, and touching the driver's state
            // from there races with the guest reading it through INT 33h.
            MouseServices.Refresh();
            DispatchMouseEvent();

            if (Keyboard.ResetRequested || SystemControl.ResetRequested)
            {
                Keyboard.ResetRequested = false;
                SystemControl.ResetRequested = false;
                Cpu.Reset();

                // The DOS that is about to boot is a new one: it has not
                // started its command processor, and its INT 2Fh is not
                // hooked, whatever the last one had.
                CommandProcessorStarted = false;
                ForgetMultiplexHook();
            }
        }
    }

    /// <summary>
    /// Calls the guest's INT 33h event handler when the mouse has done
    /// something it asked to hear about.
    ///
    /// It happens here, between batches, and not from inside the mouse code,
    /// because this is a far call <b>into</b> guest code: it pushes a return
    /// address and redirects execution. That is only safe at an instruction
    /// boundary with the guest's own stack in hand -- doing it while we are
    /// partway through servicing one of our own interrupt handlers would
    /// build the call on top of that handler's stack frame and return into
    /// the middle of it.
    /// </summary>
    private void DispatchMouseEvent()
    {
        // Not while the CPU is halted: HLT is waiting for an interrupt, and
        // redirecting execution into a handler would resume the guest at a
        // point it never asked to run from.
        if (Cpu.State.Halted) return;

        // Only in real mode. A protected-mode guest's handler lives behind a
        // selector, and pushing a real-mode segment value at it would send
        // execution to whatever descriptor happens to have that index.
        if (Cpu.State.ProtectedMode) return;

        // On real hardware this callback is reached from an interrupt, and a
        // program protects the moments it cannot be interrupted by clearing
        // the interrupt flag. We are not an interrupt -- we redirect the guest
        // between batches -- so the guest has no way to keep us out unless we
        // honour the same rules it would rely on:
        //
        //   * interrupts must be enabled, so a CLI really does protect;
        //   * no handler may be in service, because the stack in hand belongs
        //     to it until its end-of-interrupt.
        //
        // Without this the callback lands wherever the batch happened to end,
        // including the middle of a routine holding a half-built structure.
        // The symptom was EDIT and QBASIC ignoring the keyboard and QBASIC
        // dying with a corrupted far heap: the callback was returning into
        // the middle of somebody else's frame.
        if (!Cpu.Flags.Interrupt) return;
        if (Interrupts.HasInterruptInService) return;

        MouseServices.TryDispatchEvent(Cpu);
    }

    /// <summary>
    /// Asks DOS where it keeps its structures, once it is up and only if
    /// something is actually mounted.
    ///
    /// The attempt cannot be made at any fixed moment, because there is no
    /// event that says "DOS has finished loading": the boot sector, IO.SYS
    /// and COMMAND.COM hand off to one another without telling the hardware
    /// anything. So it is retried between batches of instructions until it
    /// works, which costs nothing once it has -- and nothing at all on a
    /// machine with no shared folder and no disc.
    ///
    /// The retry is throttled because each failed attempt runs code in the
    /// guest, and doing that every two thousand instructions on a machine
    /// that will never have DOS on it would be pure waste.
    /// </summary>
    /// <summary>
    /// Puts INT 33h back on our driver after DOS has taken it away.
    ///
    /// The BIOS installs the mouse interface at POST, but IO.SYS, while it
    /// initialises, points a block of vectors -- INT 33h among them -- at a
    /// plain IRET of its own, because on a real PC there is no mouse driver
    /// until MOUSE.COM loads. From then on every program asking "is there a
    /// mouse?" gets its AX back unchanged and concludes there is not, which
    /// is exactly what EDIT and QBASIC did here. MOUSE.COM fixes this by
    /// being loaded after DOS; we fix it by noticing.
    ///
    /// Only a vector that leads to an IRET is reclaimed. A real driver the
    /// user chose to load -- a genuine MOUSE.COM, say -- leaves the vector on
    /// its own code, and that is respected: the machine must never fight the
    /// guest for a vector somebody deliberately took.
    /// </summary>
    /// <summary>Whether INT 33h is put back on the BIOS driver after DOS parks it on an IRET.</summary>
    /// <summary>
    /// Off by default, and deliberately so.
    ///
    /// Taking a vector out of the guest's hands is the most invasive thing
    /// this emulator does, and the first field report was EDIT and QBASIC
    /// misbehaving in three different ways because of it. A guest that wants
    /// a mouse under DOS can load MOUSE.COM, exactly as on a real machine;
    /// the emulator offers INT 33h from POST, and if DOS parks the vector the
    /// honest answer is that there is no driver loaded.
    /// </summary>
    public bool ReclaimMouseVector { get; set; }

    private void TryReclaimMouseVector()
    {
        if (!ReclaimMouseVector || _mouseVectorReclaimed) return;
        if (--_mouseVectorCountdown > 0) return;
        _mouseVectorCountdown = MouseVectorCheckInterval;

        if (Cpu.State.ProtectedMode) return;
        if (Mouse.ConnectedInterface == MouseInterface.None) return;

        ushort offset = Memory.ReadWord(0x33 * 4);
        ushort segment = Memory.ReadWord(0x33 * 4 + 2);
        if (segment == 0xF000 && offset == BiosRom.Entry.Mouse) return;

        // Only the vector DOS itself parks, and only once.
        //
        // Both halves matter, and both were learnt the hard way. IO.SYS puts
        // its stub low in memory, below the first program it loads, so a
        // segment above that belongs to a program and is never ours to take
        // -- EDIT installs a handler of its own, and strapping it produced a
        // corrupted far heap in QBASIC and an EDIT that would not open at
        // all. And doing it once is what keeps a program that later takes the
        // vector legitimately from having it taken back a few thousand
        // instructions later.
        if (segment >= FirstProgramSegment) return;

        uint target = ((uint)segment << 4) + offset;
        if (Memory.ReadByte(target) != 0xCF) return;   // somebody's real handler: theirs

        SetVector(0x33, BiosRom.Entry.Mouse);
        _mouseVectorReclaimed = true;
    }

    /// <summary>
    /// Below this paragraph is DOS itself; above it are the programs it loads.
    /// The boundary is generous on purpose: it only has to separate IO.SYS's
    /// stubs from a loaded program, and DOS with its buffers and drivers never
    /// reaches anywhere near here.
    /// </summary>
    private const ushort FirstProgramSegment = 0x1000;

    private bool _mouseVectorReclaimed;

    private const int MouseVectorCheckInterval = 50;
    private int _mouseVectorCountdown = 1;

    /// <summary>
    /// Every so often, once DOS is up and a disc is in, asks the guest which
    /// letter the CD-ROM has. Rarely, because each question borrows the CPU
    /// for an INT 2Fh in the guest; the answer only changes when a driver
    /// loads, which is a once-per-boot event.
    /// </summary>
    private void TryRefreshCompactDiscLetter()
    {
        if (CompactDisc is null || !CommandProcessorStarted) return;
        if (--_discLetterCountdown > 0) return;
        _discLetterCountdown = CompactDiscLetterInterval;
        QueryCompactDiscLetter();
    }

    private const int CompactDiscLetterInterval = 2000;
    private int _discLetterCountdown = CompactDiscLetterInterval;

    private void TryInstallRedirector()
    {
        if (!Redirector.MountedDrives.Any()) return;

        if (--_redirectorInstallCountdown > 0) return;
        _redirectorInstallCountdown = RedirectorInstallInterval;

        uint before = Redirector.CurrentDirectoryStructure;
        bool known = Redirector.IsInstalled;

        _redirectorInstaller.TryInstall(Redirector, force: known);

        // The addresses are re-checked until they stop moving, and then the
        // machine is left alone.
        //
        // Both halves matter. Re-checking is necessary because DOS builds a
        // small Current Directory Structure array early -- one entry per
        // physical drive -- and discards it for a bigger one when CONFIG.SYS
        // names a LASTDRIVE; a redirector that asked once during the boot
        // sector's first moments is holding the address of memory DOS has
        // since given to something else. Stopping is equally necessary,
        // because every check borrows the CPU to make two INT 21h calls in
        // the guest, and doing that at an arbitrary moment for the rest of
        // the machine's life eventually catches DOS in the middle of
        // something of its own.
        if (!known || Redirector.CurrentDirectoryStructure != before)
        {
            _redirectorStableChecks = 0;
            return;
        }

        // Settled: the structures have not moved for a while, so DOS has
        // finished building itself and the vector may be taken over. This is
        // also where a re-hook happens if something loading later displaced
        // us, which is why it is not done once and forgotten.
        //
        // The command processor having started is the other half of the test,
        // and the half that was missing: stillness alone cannot tell a
        // finished DOS from one that has not begun, because the small
        // directory array DOS builds first sits unchanged for a long stretch
        // before CONFIG.SYS is read. Hooking then breaks CONFIG.SYS.
        if (CommandProcessorStarted && ++_redirectorStableChecks >= RedirectorStableChecksNeeded)
            HookMultiplexForRedirector();
    }    /// <summary>Batches between attempts to find DOS's structures.</summary>
    private const int RedirectorInstallInterval = 200;

    /// <summary>
    /// Consecutive unchanged checks after which DOS is considered settled and
    /// its INT 2Fh vector may be taken over.
    ///
    /// Waiting matters far more than the exact number. Hooking the vector
    /// while DOS is still building itself stops CONFIG.SYS from loading its
    /// device drivers -- COUNTRY.SYS and DISPLAY.SYS fail, and the machine
    /// stops at the error prompt -- because those drivers use INT 2Fh
    /// themselves while DOS is in a state no resident program should be
    /// looking at yet. The structures moving is the visible sign that DOS is
    /// still working, so a run of checks with nothing moving is the signal to
    /// go ahead.
    /// </summary>
    private const int RedirectorStableChecksNeeded = 400;

    private int _redirectorStableChecks;
    private int _redirectorInstallCountdown = 1;

    /// <summary>
    /// Advances the devices by the time the executed instructions represent.
    /// The timer runs at 1.19 MHz against the CPU's declared 25 MHz, and the
    /// ratio between them is what converts one into the other.
    /// </summary>
    private void AdvanceDevices(int instructions)
    {
        double pitCycles = (double)instructions * Pit8253.ClockHz / CpuFrequencyHz;
        Timer.Advance(pitCycles);

        // The sound card is driven from the same clock: microseconds of
        // emulated time, converted from the instructions just executed.
        double microseconds = (double)instructions * 1_000_000.0 / CpuFrequencyHz;
        SoundBlaster.Advance(microseconds);
        Usb?.Advance(microseconds);
        UsbStorageController?.Advance(microseconds);

        // And so is the video beam. A program polling for vertical retrace is
        // waiting for a moment that has a duration; without the clock behind
        // it, the answer it gets depends on how fast its own polling loop
        // runs rather than on where the beam is.
        VideoRegisters.AdvanceBeam(microseconds);

        // The keyboard has a clock of its own: it is what makes a held key
        // repeat, without the guest having to ask for anything.
        Keyboard.Advance((double)instructions * 1000.0 / CpuFrequencyHz);

        // Collect anything that arrived on the wire. A real card would act the
        // instant a frame appeared; polling here is close enough, because the
        // guest cannot observe the difference -- it only ever learns about a
        // frame through the interrupt the card raises when it files one.
        Network.Poll();

        // The two mouse ports, which push rather than being polled by the
        // guest: each turns whatever movement has accumulated into a packet
        // and interrupts. Only the one the guest's driver is listening to
        // will be read, but neither knows which that is.
        Keyboard.PollMouse();
        SerialMouse.Poll();
    }
}
