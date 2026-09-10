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
/// The native halves of the BIOS services that are not big enough for a
/// class of their own: the dispatcher for the ROM hooks, the timer tick,
/// INT 2Fh multiplex, serial and printer stubs, INT 15h and the time of day.
/// </summary>
public sealed partial class Machine
{

    /// <summary>
    /// Dispatches BIOS calls to their handlers. This is the seam where
    /// execution leaves emulated code and continues in C#: the ROM contains
    /// only a stub that traps out to here.
    /// </summary>
    private void HandleBiosCall(CpuNs.Cpu cpu, byte handlerId)
    {
        switch ((BiosRom.Handler)handlerId)
        {
            case BiosRom.Handler.Post: RunPost(); return;
            case BiosRom.Handler.Video: VideoServices.Handle(cpu); return;
            case BiosRom.Handler.Disk: DiskServices.Handle(cpu); return;
            case BiosRom.Handler.Keyboard: KeyboardServices.Handle(cpu); return;
            case BiosRom.Handler.KeyboardIrq: KeyboardServices.HandleIrq(cpu); return;
            case BiosRom.Handler.TimerTick: HandleTimerTick(cpu); return;
            case BiosRom.Handler.EquipmentList: cpu.State.Ax = Bda.EquipmentWord; return;
            case BiosRom.Handler.MemorySize: cpu.State.Ax = Bda.MemorySizeKb; return;
            case BiosRom.Handler.System: HandleSystemServices(cpu); return;
            case BiosRom.Handler.TimeOfDay: HandleTimeOfDay(cpu); return;
            case BiosRom.Handler.Bootstrap: Boot(cpu); return;
            case BiosRom.Handler.Multiplex: HandleMultiplex(cpu); return;
            case BiosRom.Handler.Coprocessor: Fpu.Handle(cpu); return;
            case BiosRom.Handler.RedirectorMultiplex: HandleRedirectorMultiplex(cpu); return;
            case BiosRom.Handler.Xms: Xms.Handle(cpu); return;
            case BiosRom.Handler.PlugAndPlay: PlugAndPlay.Handle(cpu); return;
            case BiosRom.Handler.AdvancedPowerManagement: PowerManagement.Handle(cpu, fromEntryPoint: true); return;
            case BiosRom.Handler.Mouse: MouseServices.Handle(cpu); return;

            // The ROM entry reads the byte and acknowledges both controllers.
            case BiosRom.Handler.MouseIrq: PointingDevice.OnInterrupt(cpu); return;

            case BiosRom.Handler.Serial: HandleSerial(cpu); return;
            case BiosRom.Handler.Printer: HandlePrinter(cpu); return;

            // The ROM entry acknowledges both controllers.
            case BiosRom.Handler.DummyIrq: return;
        }
    }

    /// <summary>
    /// The system tick handler: bumps the BIOS data area's tick counter and
    /// chains to INT 1Ch, which programs are free to hook.
    /// </summary>
    private void HandleTimerTick(CpuNs.Cpu cpu)
    {
        uint ticks = Bda.TimerTicks + 1;

        // The counter rolls over at midnight, after 1,573,040 ticks (24 hours
        // at 18.2 Hz), and raises a flag that DOS uses to advance the date.
        if (ticks >= 1_573_040)
        {
            ticks = 0;
            Bda.MidnightFlag = 1;
        }
        Bda.TimerTicks = ticks;
        // The ROM entry goes on to call INT 1Ch and acknowledge the controller.
    }

    /// <summary>
    /// INT 2Fh, the "multiplex" interrupt through which resident programs
    /// announce themselves. Here it exists so the XMS driver can be found:
    /// a program asks with AX=4300h whether it is present, then with AX=4310h
    /// for the address to call.
    /// </summary>
    private void HandleMultiplex(CpuNs.Cpu cpu)
    {
        // Always clear carry before answering.
        //
        // INT 2Fh signals "nobody answered" by leaving AX unchanged, not by
        // setting carry; carry means the call failed. Since the caller may
        // well arrive with carry already set -- DOS deliberately does an STC
        // before certain calls -- leaving it alone would turn "no such
        // service" into "the operation failed", and the caller acts on the
        // difference.
        cpu.SetCarryInStackedFlags(false);

        ushort function = cpu.State.Ax;

        switch (function)
        {
            case 0x4300:
                // "XMS is installed": the expected answer is 0x80 in AL.
                cpu.State.SetReg8(0, 0x80);
                return;

            // --- the Windows interface, INT 2Fh AX=16xx --------------------
            //
            // This is how Windows and DOS negotiate with each other, and how a
            // resident program learns that Windows is starting so it can make
            // itself safe first. Windows broadcasts the start and end of each
            // session and of each virtual machine within it; a DOS that never
            // answers 1600h is one Windows takes to be pre-3.0, and enhanced
            // mode expects the broadcasts to reach someone.
            //
            // The answers here are the ones a plain DOS gives: no Windows is
            // already running, and the broadcasts are acknowledged by leaving
            // the registers as the caller set them, which is what "nobody
            // objected" looks like on this interface.

            case 0x1600:
                // "Is Windows in enhanced mode already running?" AL=0 means no.
                // Answering anything else would make Windows refuse to start,
                // believing a second copy of itself is already up.
                cpu.State.SetReg8(0, 0x00);
                return;

            case 0x1605:
                // Windows is starting. A resident program that cannot survive
                // it says so by setting CX to a non-zero value; leaving CX
                // alone is consent, which is what a machine with nothing
                // resident should give.
                return;

            case 0x1606:
                // Windows is shutting down.
                return;

            case 0x1607:
                // A virtual device is calling out to its DOS-side half. With
                // no such halves installed here, the call is simply returned:
                // the device treats that as "not present" and carries on.
                return;

            case 0x160B:
                // Enumerate the TSRs that asked to be told about Windows.
                // CX=0 ends the enumeration, which is the honest answer when
                // none registered.
                cpu.State.Cx = 0;
                return;

            case 0x4310:
                // The driver's entry point, in ES:BX. From here on the
                // program calls it directly with a far call and never goes
                // through this interrupt again.
                cpu.SetSegment(CpuNs.SegReg.Es, 0xF000);
                cpu.State.Bx = BiosRom.Entry.XmsEntry;
                return;

            default:
                // Function 11h is the network redirector, which answers for
                // every drive letter that has no FAT behind it -- a folder on
                // the host, or a CD.
                if ((function >> 8) == 0x11 && Redirector.Handle(cpu)) return;

                // Function 15h is MSCDEX -- and we answer it only when the
                // guest has no MSCDEX of its own.
                //
                // Two things answering the same interface is worse than
                // neither. The real MSCDEX keeps state we know nothing about:
                // which drive letters it took, which directories it has
                // cached, what it last told the program asking. Replying in
                // its place hands out answers that contradict its own, and
                // the program acts on whichever it got -- which surfaces as
                // DOS's "Abort, Retry, Ignore?" over a drive that works.
                //
                // So the emulator's MSCDEX is a stand-in for when there is
                // none. With a real one loaded the right thing to do is stay
                // quiet and let it talk to our drive through the IDE
                // controller, the way it would talk to a real one.
                if ((function >> 8) == 0x15
                    && !GuestHasItsOwnCompactDiscExtensions
                    && CompactDiscServices.Handle(cpu)) return;

                // No other resident service is installed. Leaving AX
                // untouched is precisely how INT 2Fh says "nobody answered".
                return;
        }
    }

    /// <summary>
    /// The redirector's own INT 2Fh handler, which sits in front of the one
    /// DOS installs.
    ///
    /// It has to sit in front, and this is the detail that makes or breaks
    /// the whole feature. The BIOS owns INT 2Fh at power-on, but DOS takes
    /// the vector over during startup for its own multiplex functions -- and
    /// once it has, a redirector that is still only reachable through the
    /// BIOS's handler is never called at all, because DOS does not chain
    /// outward for function 11h: as far as it is concerned there is no
    /// redirector to chain to. A real redirector was a TSR that hooked the
    /// vector after DOS was up, and that is what we imitate.
    ///
    /// Sitting in front brings the obligation that goes with it. INT 2Fh is
    /// a bus rather than a service: dozens of unrelated resident programs
    /// share it, each answering its own function number and passing on the
    /// rest. A handler that swallows what it does not recognise silences
    /// everybody behind it -- which here would mean DOS itself, and the
    /// symptom would be a machine that boots and then behaves strangely in
    /// ways that have nothing visibly to do with a shared folder. So anything
    /// that is not ours is passed on, by rewriting the interrupt frame so
    /// that our IRET lands in DOS's handler with the caller's registers and
    /// flags exactly as they arrived.
    /// </summary>
    private void HandleRedirectorMultiplex(CpuNs.Cpu cpu)
    {
        ushort function = cpu.State.Ax;

        if ((function >> 8) != 0x11)
        {
            // Not the redirector's function at all: pass it straight on. This
            // is the common case by a wide margin -- DOS's own multiplex
            // services, the print spooler, anything else resident.
            ChainToPreviousMultiplexHandler(cpu);
            return;
        }

        if (Redirector.Handle(cpu)) return;

        // Function 11h that we declined: a subfunction we do not implement,
        // or a drive that is not ours.
        //
        // The answer has to be the one DOS's own INT 2Fh handler gives for
        // a function 11h nobody serves: carry set and AX=1, "invalid
        // function". That is what every such call got before the vector was
        // hooked, and DOS is written for it -- a declined 23h makes it
        // canonicalise the name itself, a declined 25h is ignored. Leaving
        // the registers as they arrived is not the same thing, though it
        // looks like the polite way to say "nobody was listening": the
        // carry DOS passed in was clear, and clear reads as success. That
        // one difference sent DIR on a shared drive down the "no CDS" path
        // with a name that was never resolved, and it reported "path not
        // found" for a directory that was there.
        //
        // Produced here rather than by chaining to the displaced handler:
        // the effect is identical, and the chain would cross DOS while it
        // is inside the very INT 21h call that asked.
        cpu.State.Ax = 0x0001;
        cpu.SetCarryInStackedFlags(true);
    }

    /// <summary>
    /// Sends the current INT 2Fh call on to the handler we displaced, by
    /// letting execution run into the far jump that follows our entry point.
    ///
    /// It is a jump and not a return, and the difference is the whole of it.
    /// The obvious-looking trick -- overwrite the IP and CS in the interrupt
    /// frame and let our own IRET carry execution into the other handler --
    /// looks right and is wrong: an IRET pops three words, so by the time the
    /// displaced handler is running, the frame it needs for its own IRET has
    /// already been eaten, and it returns to whatever was on the stack below.
    ///
    /// Nothing needs to be done here but move past the IRET: the two bytes
    /// after it are a JMP FAR through the saved vector, which is exactly how
    /// every TSR passed a call along.
    /// </summary>
    private static void ChainToPreviousMultiplexHandler(CpuNs.Cpu cpu)
    {
        // Our entry is: hook (3 bytes), IRET (1), then the jump. Execution is
        // sitting just past the hook, on the IRET; stepping over it lands on
        // the jump.
        cpu.State.Eip = BiosRom.Entry.RedirectorMultiplex + 4;
    }

    /// <summary>The INT 2Fh vector the redirector displaced, segment:offset.</summary>
    private uint _previousMultiplexVector;

    /// <summary>
    /// Puts the redirector's handler in front of whatever holds INT 2Fh now.
    ///
    /// Called once DOS is up and its structures have been found, because
    /// hooking before then would displace the BIOS's own handler and gain
    /// nothing: DOS would overwrite the vector a moment later anyway. It is
    /// public so that a test can hook against a handler it planted itself,
    /// which is the only way to check the chaining without booting DOS.
    /// </summary>
    /// <summary>
    /// Whether the redirector takes the INT 2Fh vector over from DOS.
    ///
    /// Off by default, and that is a deliberate, conservative choice rather
    /// than a placeholder. Hooking the vector is genuinely required for the
    /// redirector to be reached at all -- DOS installs a multiplex handler of
    /// its own during startup and does not chain outward for function 11h --
    /// but sharing that vector correctly is also the part most able to stop
    /// the machine dead, and a redirector that does not work yet is far
    /// better than one that prevents DOS from booting.
    ///
    /// <para><b>What is known about the remaining problem, measured on
    /// MS-DOS 6.22.</b> Turning this on makes CONFIG.SYS fail to load its
    /// device drivers: COUNTRY.SYS and DISPLAY.SYS report "Errore in
    /// CONFIG.SYS" and the machine stops at the error prompt, having never
    /// reached COMMAND.COM. The consequence is visible in the structures --
    /// DOS never gets far enough to discard its first, three-entry Current
    /// Directory Structure array for the full-sized one, so the redirector is
    /// left holding a LASTDRIVE of three and every letter past C: is refused
    /// however correctly the rest of the protocol behaves.</para>
    ///
    /// <para>Waiting for the structures to stop moving before hooking is not
    /// enough, and the reason is worth recording: the early array is perfectly
    /// stable for a long stretch <i>before</i> CONFIG.SYS is processed, so
    /// "nothing has moved lately" does not distinguish "DOS has finished"
    /// from "DOS has not started yet". What is needed is a signal that DOS is
    /// past CONFIG.SYS entirely -- the transition to COMMAND.COM -- and this
    /// code does not have one yet.</para>
    ///
    /// <para>With the switch off everything else works: the structures are
    /// found correctly once DOS has settled (LASTDRIVE=Z reads as 26, the
    /// array is located at its final address), the drive is marked in the
    /// CDS, and the whole of the function 11h protocol behaves as its tests
    /// require. What is missing is only that DOS never routes a call to
    /// it.</para>
    /// </summary>
    public bool RedirectorHooksMultiplex { get; set; } = true;

    /// <summary>
    /// What the redirector knows about the DOS it is attached to, for a
    /// snapshot. The guest's memory carries the hooked vector and the
    /// marked CDS entries, but three things live only on this side: where
    /// DOS keeps its structures, the vector the hook displaced (its copy in
    /// ROM is not part of RAM), and the fact that COMMAND.COM has started.
    /// A snapshot restored without them boots a guest whose shared folders
    /// answer nothing, because every call about them is declined.
    /// </summary>
    internal byte[] CaptureDosState()
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write(CommandProcessorStarted);
        writer.Write(Redirector.SwappableDataArea);
        writer.Write(Redirector.CurrentDirectoryStructure);
        writer.Write(Redirector.DriveCount);
        writer.Write(_previousMultiplexVector);
        writer.Flush();
        return buffer.ToArray();
    }

    internal void RestoreDosState(byte[] data)
    {
        if (data.Length == 0) return;
        using var reader = new BinaryReader(new MemoryStream(data));
        CommandProcessorStarted = reader.ReadBoolean();
        uint sda = reader.ReadUInt32();
        uint cds = reader.ReadUInt32();
        byte driveCount = reader.ReadByte();
        _previousMultiplexVector = reader.ReadUInt32();
        _multiplexHooked = _previousMultiplexVector != 0;

        // Attaching again re-marks whatever is mounted now in the restored
        // CDS, which also covers a folder mounted after the snapshot was
        // taken.
        if (sda != 0 && cds != 0) Redirector.AttachToDos(sda, cds, driveCount);

        // The far jump at the end of the multiplex entry reads its target
        // from ROM, and ROM is not in the snapshot.
        if (_previousMultiplexVector != 0)
        {
            Memory.WriteRomWord(BiosRom.Base + BiosRom.Entry.PreviousMultiplexVector, (ushort)_previousMultiplexVector);
            Memory.WriteRomWord(BiosRom.Base + BiosRom.Entry.PreviousMultiplexVector + 2, (ushort)(_previousMultiplexVector >> 16));
        }
    }

    /// <summary>
    /// Whether the vector has been taken over for the DOS the redirector is
    /// attached to. Once is all it takes, and twice is fatal: the moment
    /// something else hooks INT 2Fh in front of us -- KEYB, MSCDEX, any
    /// resident program loaded from AUTOEXEC.BAT -- its saved "previous"
    /// vector is ours. Hooking again then saves that program as our
    /// previous, and the chain becomes a ring: it jumps to us, we jump to
    /// it, and the first INT 2Fh the guest makes never returns. The machine
    /// froze on COMMAND.COM's own AE00h call, with the tick counter still.
    /// </summary>
    private bool _multiplexHooked;

    /// <summary>Forgets the hook, for a DOS that has been rebooted or rebuilt: the next attachment hooks afresh.</summary>
    internal void ForgetMultiplexHook()
    {
        _multiplexHooked = false;
        _previousMultiplexVector = 0;
    }

    public void HookMultiplexForRedirector()
    {
        if (!RedirectorHooksMultiplex || _multiplexHooked) return;

        ushort offset = Memory.ReadWord(0x2F * 4);
        ushort segment = Memory.ReadWord(0x2F * 4 + 2);

        uint current = ((uint)segment << 16) | offset;
        uint ours = ((uint)0xF000 << 16) | BiosRom.Entry.RedirectorMultiplex;

        // Already hooked. Chaining to ourselves would be an infinite loop, so
        // this test is not an optimisation.
        if (current == ours) return;

        _previousMultiplexVector = current;
        _multiplexHooked = true;

        // The jump at the end of our entry point reads its target from here.
        // It lives in the ROM image, which only the emulator writes to.
        Memory.WriteRomWord(BiosRom.Base + BiosRom.Entry.PreviousMultiplexVector, offset);
        Memory.WriteRomWord(BiosRom.Base + BiosRom.Entry.PreviousMultiplexVector + 2, segment);

        SetVector(0x2F, BiosRom.Entry.RedirectorMultiplex);
    }

    /// <summary>
    /// INT 14h: serial ports. None are fitted.
    ///
    /// The distinction that matters here is between "not ready right now" and
    /// "not there at all". Status code 0x80 means <b>timeout</b>, i.e. a
    /// transient fault, and software that receives it retries -- which on a
    /// port that does not exist means retrying forever. Reporting the lines as
    /// simply absent is a final answer: the software concludes there is no
    /// such port and moves on.
    /// </summary>
    private void HandleSerial(CpuNs.Cpu cpu)
    {
        // COM1 has a mouse on it; the other three ports have nothing.
        //
        // The distinction has to be made, because the BIOS data area now
        // declares COM1 at 0x3F8 and software believes it. Answering "nothing
        // connected" for a port the machine says exists is a contradiction a
        // driver can act on: it reads the data area, finds the port, asks the
        // BIOS about it, and concludes the hardware is broken.
        //
        // What the mouse driver actually does is talk to the port registers
        // directly -- serial mice are timing-sensitive enough that going
        // through the BIOS was never fast enough -- so this exists to be
        // consistent rather than to carry data.
        bool com1 = cpu.State.Dx == 0;

        if (!com1)
        {
            // AH = line status, AL = modem status. With everything clear
            // nothing can be sent and no error is claimed: there is simply
            // nothing there.
            cpu.State.Ax = 0x0000;
            return;
        }

        // Bit 5: the transmit register is empty, so a byte may be written.
        // Bit 6: the transmitter itself is idle. Together they say the port
        // is present and ready.
        cpu.State.SetReg8(4, 0x60);                      // AH: line status

        // Clear to send, data set ready and carrier detect, which is what the
        // mouse's own wiring loops back to whoever raises DTR and RTS.
        const byte Ready = 0x10 | 0x20 | 0x80;
        cpu.State.SetReg8(0, Ready);                     // AL: modem status
    }

    /// <summary>
    /// INT 17h: parallel ports. As with the serial ports, the answer says
    /// "absent" rather than "busy", so software does not sit there retrying.
    /// </summary>
    private void HandlePrinter(CpuNs.Cpu cpu)
    {
        // AH = printer status. Bit 3 reports an I/O error, which is how a
        // port with nothing plugged into it announces itself.
        cpu.State.SetReg8(4, 0x08);
    }

    /// <summary>
    /// INT 15h AH=E8h: the memory map, in the two shapes that outlived the
    /// 64 MB limit of AH=88h. E801h gives two numbers, memory between 1 and
    /// 16 MB and memory above 16 MB; E820h gives a list of ranges with what
    /// each one is, which is how a modern loader learns where it may put
    /// things. Windows 95 uses the first, Windows 98 and everything since
    /// the second.
    /// </summary>
    private void HandleMemoryMap(CpuNs.Cpu cpu)
    {
        switch (cpu.State.GetReg8(0))   // AL
        {
            case 0x01:
            {
                uint extended = Memory.RamSize > 0x100000 ? Memory.RamSize - 0x100000 : 0;
                // Below 16 MB in KB, capped at 15 MB (the ISA hole is where
                // the 16th would be); above 16 MB in 64 KB blocks. Both
                // pairs, because callers read whichever they were told to.
                uint lowKb = Math.Min(extended, 15u * 1024 * 1024) / 1024;
                uint highBlocks = Memory.RamSize > 0x1000000 ? (Memory.RamSize - 0x1000000) / 65536 : 0;
                cpu.State.Ax = cpu.State.Cx = (ushort)lowKb;
                cpu.State.Bx = cpu.State.Dx = (ushort)highBlocks;
                cpu.SetCarryInStackedFlags(false);
                return;
            }

            case 0x20:
            {
                const uint Smap = 0x534D4150;   // 'SMAP'
                if (cpu.State.Edx != Smap || cpu.State.Ecx < 20)
                {
                    cpu.State.SetReg8(4, 0x86);
                    cpu.SetCarryInStackedFlags(true);
                    return;
                }

                var ranges = MemoryMap();
                uint index = cpu.State.Ebx;
                if (index >= ranges.Count)
                {
                    cpu.State.SetReg8(4, 0x86);
                    cpu.SetCarryInStackedFlags(true);
                    return;
                }

                (ulong start, ulong length, uint type) = ranges[(int)index];
                uint buffer = cpu.FarPointerToLinear(cpu.State.Es.Selector, cpu.State.Edi);
                cpu.WriteLinearDword(buffer + 0, (uint)start);
                cpu.WriteLinearDword(buffer + 4, (uint)(start >> 32));
                cpu.WriteLinearDword(buffer + 8, (uint)length);
                cpu.WriteLinearDword(buffer + 12, (uint)(length >> 32));
                cpu.WriteLinearDword(buffer + 16, type);

                cpu.State.Eax = Smap;
                cpu.State.Ecx = 20;
                // EBX is the continuation: the next index, or zero after the
                // last entry, which is how the caller knows to stop.
                cpu.State.Ebx = index + 1 < ranges.Count ? index + 1 : 0;
                cpu.SetCarryInStackedFlags(false);
                return;
            }

            default:
                cpu.State.SetReg8(4, 0x86);
                cpu.SetCarryInStackedFlags(true);
                return;
        }
    }

    /// <summary>
    /// The physical address space as ranges: type 1 is memory the operating
    /// system may use, type 2 is spoken for -- the BIOS data area at the
    /// top of conventional memory, the ROM, the frame buffer.
    /// </summary>
    public IReadOnlyList<(ulong Start, ulong Length, uint Type)> MemoryMap()
    {
        var map = new List<(ulong, ulong, uint)>();
        ulong ebda = (ulong)ExtendedBiosDataSegment << 4;
        map.Add((0, ebda, 1));
        map.Add((ebda, 0xA0000 - ebda, 2));
        map.Add((0xF0000, 0x10000, 2));
        if (Memory.RamSize > 0x100000) map.Add((0x100000, Memory.RamSize - 0x100000, 1));
        map.Add((LinearFrameBufferBase, (ulong)VgaMemory.TotalSize + 0x10000, 2));
        return map;
    }

    private void HandleSystemServices(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);   // AH

        switch (function)
        {
            case 0xC2:
                PointingDevice.Handle(cpu);
                return;

            case 0x87:
                MoveExtendedMemory(cpu);
                return;

            case 0x88:
                // Extended memory, in KB above the first megabyte.
                uint extendedKb = Memory.RamSize > 0x100000
                    ? (Memory.RamSize - 0x100000) / 1024 : 0;
                cpu.State.Ax = (ushort)Math.Min(extendedKb, 0xFFFF);
                cpu.SetCarryInStackedFlags(false);
                return;

            case 0x53:
                PowerManagement.Handle(cpu, fromEntryPoint: false);
                return;

            case 0xE8:
                HandleMemoryMap(cpu);
                return;

            case 0x4F:
                // Keyboard intercept. The BIOS calls this for every scancode,
                // so anything that hooks it can rewrite or discard the code.
                // The stock version leaves AL alone and returns with carry
                // set, which means "the keystroke stands, go ahead and process
                // it".
                cpu.SetCarryInStackedFlags(true);
                return;

            case 0xC0:
                // The system configuration table, returned in ES:BX.
                //
                // Refusing this call is not as harmless as it looks. Callers
                // are inconsistent about how they detect the refusal: some
                // check the carry flag, but others -- Windows' own DOS
                // extender among them -- test ES:BX for zero and read the
                // table wherever those registers happen to point. Leaving
                // them untouched hands such a caller whatever was in them,
                // which it then reads as feature bits.
                cpu.SetSegment(CpuNs.SegReg.Es, 0xF000);
                cpu.State.Bx = BiosRom.Entry.SystemConfiguration;
                cpu.State.SetReg8(4, 0);
                cpu.SetCarryInStackedFlags(false);
                return;

            case 0x24:
            {
                // The BIOS A20 interface (INT 15h, AH=24h). DOS and extended
                // memory managers prefer it to poking the keyboard controller
                // themselves, since the BIOS knows which of the several
                // possible mechanisms this machine actually has.
                byte subFunction = cpu.State.GetReg8(0);   // AL
                switch (subFunction)
                {
                    case 0x00:   // disable A20
                        Memory.A20Enabled = false;
                        cpu.State.SetReg8(4, 0);
                        cpu.SetCarryInStackedFlags(false);
                        return;
                    case 0x01:   // enable A20
                        Memory.A20Enabled = true;
                        cpu.State.SetReg8(4, 0);
                        cpu.SetCarryInStackedFlags(false);
                        return;
                    case 0x02:   // query the current state
                        cpu.State.SetReg8(4, 0);
                        cpu.State.SetReg8(0, (byte)(Memory.A20Enabled ? 1 : 0));
                        cpu.SetCarryInStackedFlags(false);
                        return;
                    case 0x03:   // report which control mechanisms exist
                        cpu.State.SetReg8(4, 0);
                        // Bit 0: keyboard controller. Bit 1: port 0x92.
                        cpu.State.Bx = 0x0003;
                        cpu.SetCarryInStackedFlags(false);
                        return;
                    default:
                        cpu.State.SetReg8(4, 0x86);
                        cpu.SetCarryInStackedFlags(true);
                        return;
                }
            }

            default:
                cpu.State.SetReg8(4, 0x86);   // unsupported function
                cpu.SetCarryInStackedFlags(true);
                return;
        }
    }

    private void HandleTimeOfDay(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);

        switch (function)
        {
            case 0x00:
            {
                // Tick count since power-on, returned in CX:DX.
                uint ticks = Bda.TimerTicks;
                cpu.State.Cx = (ushort)(ticks >> 16);
                cpu.State.Dx = (ushort)ticks;
                cpu.State.SetReg8(0, Bda.MidnightFlag);
                Bda.MidnightFlag = 0;      // reading it clears the flag
                return;
            }

            case 0x02:
            {
                // Current time from the CMOS clock, in BCD digits.
                cpu.Io.WriteByte(0x70, 0x04);
                cpu.State.SetReg8(5, cpu.Io.ReadByte(0x71));   // CH: hours
                cpu.Io.WriteByte(0x70, 0x02);
                cpu.State.SetReg8(1, cpu.Io.ReadByte(0x71));   // CL: minutes
                cpu.Io.WriteByte(0x70, 0x00);
                cpu.State.SetReg8(2, cpu.Io.ReadByte(0x71));   // DH: seconds
                cpu.State.SetReg8(6, 0);                        // DL: standard time, not DST
                cpu.SetCarryInStackedFlags(false);
                return;
            }

            case 0x04:
            {
                // Current date from the CMOS clock.
                cpu.Io.WriteByte(0x70, 0x32);
                cpu.State.SetReg8(5, cpu.Io.ReadByte(0x71));   // CH: century
                cpu.Io.WriteByte(0x70, 0x09);
                cpu.State.SetReg8(1, cpu.Io.ReadByte(0x71));   // CL: year
                cpu.Io.WriteByte(0x70, 0x08);
                cpu.State.SetReg8(2, cpu.Io.ReadByte(0x71));   // DH: month
                cpu.Io.WriteByte(0x70, 0x07);
                cpu.State.SetReg8(6, cpu.Io.ReadByte(0x71));   // DL: day
                cpu.SetCarryInStackedFlags(false);
                return;
            }

            default:
                cpu.SetCarryInStackedFlags(true);
                return;
        }
    }
}
