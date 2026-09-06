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
            DispatchMouseEvent();

            if (Keyboard.ResetRequested || SystemControl.ResetRequested)
            {
                Keyboard.ResetRequested = false;
                SystemControl.ResetRequested = false;
                Cpu.Reset();
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
    public bool ReclaimMouseVector { get; set; } = true;

    private void TryReclaimMouseVector()
    {
        if (!ReclaimMouseVector) return;
        if (--_mouseVectorCountdown > 0) return;
        _mouseVectorCountdown = MouseVectorCheckInterval;

        if (Cpu.State.ProtectedMode) return;
        if (Mouse.ConnectedInterface == MouseInterface.None) return;

        ushort offset = Memory.ReadWord(0x33 * 4);
        ushort segment = Memory.ReadWord(0x33 * 4 + 2);
        if (segment == 0xF000 && offset == BiosRom.Entry.Mouse) return;

        uint target = ((uint)segment << 4) + offset;
        if (Memory.ReadByte(target) != 0xCF) return;   // somebody's real handler: theirs

        SetVector(0x33, BiosRom.Entry.Mouse);
    }

    private const int MouseVectorCheckInterval = 50;
    private int _mouseVectorCountdown = 1;

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
        if (++_redirectorStableChecks >= RedirectorStableChecksNeeded)
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
        SoundBlaster.Advance((double)instructions * 1_000_000.0 / CpuFrequencyHz);

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
