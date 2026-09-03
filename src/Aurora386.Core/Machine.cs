using Aurora386.Core.Bios;
using Aurora386.Core.Devices;
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
public sealed class Machine : IDisposable
{
    public PhysicalMemoryBus Memory { get; }
    public IoBus Io { get; }
    public CpuNs.Cpu Cpu { get; }

    public InterruptController Interrupts { get; }
    public Pit8253 Timer { get; }
    public Keyboard8042 Keyboard { get; }
    public SystemControlPort SystemControl { get; }
    public SystemPort61 SystemPort { get; }
    public Cmos Cmos { get; }

    /// <summary>
    /// The mouse itself: one physical device, read by three different
    /// interfaces. Which of them the guest listens to depends entirely on
    /// what driver it loaded, so all three are fitted and the host's
    /// movements go to whichever asks.
    /// </summary>
    public MouseState Mouse { get; }

    /// <summary>The mouse on the 8042's auxiliary port, interrupting on IRQ 12.</summary>
    public Ps2Mouse Ps2Mouse { get; }

    /// <summary>A Microsoft serial mouse on COM1, for drivers that expect one there.</summary>
    public SerialMouse SerialMouse { get; }

    public VgaRegisters VideoRegisters { get; }
    public VgaMemory VideoMemory { get; }
    public VgaRenderer Video { get; }

    // Audio: the DMA controller is a system device rather than part of the
    // sound card, but the card is the only thing here that uses it.
    public Dma8237 Dma { get; }
    public SoundBlaster16 SoundBlaster { get; }

    /// <summary>The NE2000 Ethernet card.</summary>
    public Ne2000 Network { get; }

    public BiosDataArea Bda { get; }
    public VideoServices VideoServices { get; }
    public DiskServices DiskServices { get; }
    public KeyboardServices KeyboardServices { get; }

    /// <summary>The MSCDEX interface, through which DOS reaches the CD-ROM.</summary>
    public CompactDiscServices CompactDiscServices { get; }

    /// <summary>
    /// The DOS network redirector: what makes a host folder or a CD appear as
    /// a drive letter whose files can be opened with ordinary DOS calls.
    /// </summary>
    public RedirectorServices Redirector { get; }

    /// <summary>INT 33h, the mouse driver interface DOS programs and Windows use.</summary>
    public MouseServices MouseServices { get; }
    public XmsManager Xms { get; }

    /// <summary>
    /// The clock speed the emulated CPU claims to run at. It does not control
    /// how fast we actually execute; it is the ratio used to convert executed
    /// instructions into device time, so it sets how fast the guest perceives
    /// its own hardware to be.
    /// </summary>
    public int CpuFrequencyHz { get; set; } = 25_000_000;   // a 25 MHz 386

    /// <summary>True once the boot sector has been loaded and given control.</summary>
    public bool Booted { get; private set; }


    private readonly List<DiskImage> _disks = [];
    private readonly RedirectorInstaller _redirectorInstaller;

    public Machine(uint ramSize = 16 * 1024 * 1024)
    {
        Memory = new PhysicalMemoryBus(ramSize);
        Io = new IoBus();

        Interrupts = new InterruptController();
        Interrupts.Register(Io);

        Timer = new Pit8253(Interrupts);
        Timer.Register(Io);

        Keyboard = new Keyboard8042(Interrupts, Memory);
        Keyboard.Register(Io);

        SystemControl = new SystemControlPort(Memory);
        SystemControl.Register(Io);

        SystemPort = new SystemPort61(Timer);
        SystemPort.Register(Io);

        Cmos = new Cmos(ramSize);
        Cmos.Register(Io);

        Mouse = new MouseState();
        Ps2Mouse = new Ps2Mouse(Mouse);
        Keyboard.AttachMouse(Ps2Mouse);

        SerialMouse = new SerialMouse(Mouse, Interrupts);
        SerialMouse.Register(Io);

        VideoRegisters = new VgaRegisters();
        VideoRegisters.Register(Io);
        VideoMemory = new VgaMemory(VideoRegisters);
        Memory.MapDevice(VgaMemory.Base, VgaMemory.Size, VideoMemory);
        Video = new VgaRenderer(VideoMemory, VideoRegisters);

        // The 8237 DMA pair and the Sound Blaster 16 that streams through it.
        // Registering the card also claims the OPL3's own ports at 0x388.
        Dma = new Dma8237(Memory);
        Dma.Register(Io);

        SoundBlaster = new SoundBlaster16(Interrupts, Dma);
        SoundBlaster.Register(Io);

        // The network card starts with a loopback backend: frames it sends
        // come straight back to it. That is enough for a driver to find the
        // card and configure it, and it keeps the emulator from touching the
        // host's network until somebody deliberately asks it to.
        Network = new Ne2000(Interrupts, new LoopbackBackend());
        Network.Register(Io);

        Cpu = new CpuNs.Cpu(Memory, Io) { Interrupts = Interrupts };

        Bda = new BiosDataArea(Memory);
        VideoServices = new VideoServices(VideoMemory, VideoRegisters, Bda);
        DiskServices = new DiskServices(Bda);
        KeyboardServices = new KeyboardServices(Bda, Memory, Keyboard);
        CompactDiscServices = new CompactDiscServices(this);
        Redirector = new RedirectorServices(this);
        _redirectorInstaller = new RedirectorInstaller(this);
        MouseServices = new MouseServices(this, Mouse);
        Xms = new XmsManager(Memory);

        InstallBios();
    }

    private void InstallBios()
    {
        Memory.MapRom(BiosRom.Base, BiosRom.Build());

        // The 386 begins fetching at 0xFFFFFFF0, so the top 64 KB of the
        // 32-bit space must show the same ROM that is mapped at 0xF0000.
        Memory.MapHighAlias(0xFFFF0000, BiosRom.Base);
        Cpu.NativeHandler = HandleBiosCall;
        Cpu.Reset();
    }

    /// <summary>
    /// The CD-ROM currently in the drive, if any.
    ///
    /// A compact disc is kept apart from the disk drives on purpose. Its
    /// sectors are 2048 bytes rather than 512 and it is addressed by block
    /// number rather than by cylinder/head/sector, so none of the INT 13h
    /// services apply: DOS reaches a CD through MSCDEX and a device driver.
    /// Pretending it were another <see cref="DiskImage"/> would leak
    /// immediately.
    /// </summary>
    public IsoImage? CompactDisc { get; private set; }

    /// <summary>
    /// Inserts a disc, replacing whatever was in the drive, and gives it a
    /// drive letter through the redirector.
    ///
    /// The two halves are both necessary and do different jobs. Attaching the
    /// image is what lets a program read raw sectors through MSCDEX's device
    /// request interface, which is how copy protection and CD audio worked.
    /// Mounting it on the redirector is what makes "DIR D:" list the disc --
    /// two entirely separate paths through DOS that happen to reach the same
    /// piece of plastic.
    /// </summary>
    public void AttachCompactDisc(IsoImage disc)
    {
        CompactDisc?.Dispose();
        CompactDisc = disc;

        MountRedirectedDrive(CompactDiscServices.DriveLetter, new IsoFileSystem(disc));
    }

    /// <summary>Ejects the disc, leaving the drive empty.</summary>
    public void EjectCompactDisc()
    {
        UnmountRedirectedDrive(CompactDiscServices.DriveLetter);

        CompactDisc?.Dispose();
        CompactDisc = null;
    }

    /// <summary>Attaches a disk image to a BIOS drive number.</summary>
    public void AttachDisk(DiskImage image)
    {
        // Replacing the disk in a drive means letting go of the one that was
        // there. Keeping it would hold its file open for as long as the
        // machine runs, and every swap during an installation would add
        // another -- eleven floppies, eleven open files, none of them
        // reachable.
        DiskImage? previous = DiskServices.GetDrive(image.DriveNumber);
        if (previous is not null)
        {
            _disks.Remove(previous);
            previous.Dispose();
        }

        _disks.Add(image);
        DiskServices.Attach(image);

        // The parameter table describes whichever floppy is in drive A:, so
        // it has to follow the disk that is actually there.
        if (image.DriveNumber == 0x00)
            WriteDiskParameterTable(image.SectorsPerTrack);
    }

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
            case BiosRom.Handler.Xms: Xms.Handle(cpu); return;
            case BiosRom.Handler.Mouse: MouseServices.Handle(cpu); return;

            case BiosRom.Handler.MouseIrq:
                // IRQ 12. A guest with its own PS/2 driver hooks this vector
                // and we never see it; this stub is for the case where
                // nothing has, and its whole job is to drain the packet the
                // mouse just delivered. Leaving the byte in the output
                // register would block the keyboard behind it, so an
                // unclaimed mouse would stop the machine accepting input.
                cpu.Io.ReadByte(0x60);
                cpu.Io.WriteByte(0xA0, 0x20);
                cpu.Io.WriteByte(0x20, 0x20);
                return;

            case BiosRom.Handler.Serial: HandleSerial(cpu); return;
            case BiosRom.Handler.Printer: HandlePrinter(cpu); return;

            case BiosRom.Handler.DummyIrq:
                // The catch-all handler, which serves two quite different
                // purposes: it backs hardware IRQs that have no handler of
                // their own, and it fills the vectors nobody ever uses.
                //
                // In the first case the interrupt must be acknowledged, or the
                // PIC stays stuck with a line in service and never delivers
                // another interrupt again. In the second it must NOT be, since
                // an unsolicited EOI would close some other, genuinely active
                // interrupt. The handler itself cannot tell which case it is
                // in, so we ask the controller whether anything is really in
                // service.
                if (Interrupts.Master.HasInterruptInService)
                {
                    // An interrupt that came in via the slave controller has
                    // to be closed on BOTH chips. Sending the EOI only to the
                    // master is the classic mistake: the slave goes on
                    // believing its line is still being serviced, its priority
                    // logic blocks everything at or below that level, and
                    // every IRQ from 8 to 15 silently stops arriving forever
                    // after -- with the master looking perfectly healthy.
                    if (Interrupts.Slave.HasInterruptInService)
                        cpu.Io.WriteByte(0xA0, 0x20);
                    cpu.Io.WriteByte(0x20, 0x20);
                }
                return;
        }
    }

    /// <summary>
    /// Machine initialisation: sets up the BIOS data area, the interrupt
    /// vector table and the devices, exactly as a real BIOS's POST would.
    /// </summary>
    private void RunPost()
    {
        Bda.Initialize(640, DiskServices.HardDiskCount);
        InstallInterruptVectors();
        InitializeInterruptController();

        // 80x25 text mode, which is how a PC presents itself at power-on.
        VideoRegisters.VideoMode = 0x03;
        VideoRegisters.TextColumns = 80;
        VideoRegisters.TextRows = 25;
        ClearScreen();
        VideoRegisters.NotifyModeChanged();
    }

    private void InstallInterruptVectors()
    {
        // No vector may be left at zero. A real BIOS fills every one of them,
        // and software relies on it: DOS reads words out of this table and
        // uses them as segment values even for vectors it does not handle, so
        // a zero there is not "free vector" but a pointer that sends execution
        // to 0000:0000. Fill everything first, then overwrite the entries that
        // have a real handler.
        for (int vector = 0; vector < 256; vector++)
            SetVector((byte)vector, BiosRom.Entry.DummyIrq);

        // Each vector is four bytes -- offset then segment -- starting at
        // physical address 0.
        SetVector(0x08, BiosRom.Entry.TimerTick);
        SetVector(0x09, BiosRom.Entry.KeyboardIrq);
        SetVector(0x10, BiosRom.Entry.Video);
        SetVector(0x11, BiosRom.Entry.EquipmentList);
        SetVector(0x12, BiosRom.Entry.MemorySize);
        SetVector(0x13, BiosRom.Entry.Disk);
        SetVector(0x14, BiosRom.Entry.Serial);
        SetVector(0x15, BiosRom.Entry.System);
        SetVector(0x16, BiosRom.Entry.Keyboard);
        SetVector(0x17, BiosRom.Entry.Printer);
        SetVector(0x19, BiosRom.Entry.Bootstrap);
        SetVector(0x1A, BiosRom.Entry.TimeOfDay);

        // INT 1Eh is a pointer, not a handler: it addresses eleven bytes
        // describing the floppy drive. DOS reads it to find out the geometry,
        // above all the sectors per track, which is what tells a 1.44 MB
        // floppy from a 2.88 MB one.
        SetVectorFar(0x1E, DiskParameterSegment, DiskParameterOffset);
        WriteDiskParameterTable(18);
        SetVector(0x2F, BiosRom.Entry.Multiplex);

        // INT 33h is not a BIOS service at all -- it belongs to MOUSE.COM,
        // which is why a real machine has this vector pointing at nothing
        // until a driver is loaded. Providing it here gives the guest a
        // working mouse with no driver, which is the whole point.
        SetVector(0x33, BiosRom.Entry.Mouse);

        // IRQ 12's vector, so that a mouse packet nobody claimed still gets
        // read out of the controller rather than jamming it.
        SetVector(0x74, BiosRom.Entry.MouseIrq);

        // IRQs 2-7 and 8-15 with no handler of their own point at a stub that
        // does nothing but acknowledge the interrupt.
        for (byte vector = 0x0A; vector <= 0x0F; vector++)
            SetVector(vector, BiosRom.Entry.DummyIrq);
        for (byte vector = 0x70; vector <= 0x77; vector++)
            SetVector(vector, BiosRom.Entry.DummyIrq);
    }

    private void SetVector(byte vector, ushort offset)
    {
        Memory.WriteWord((uint)vector * 4, offset);
        Memory.WriteWord((uint)vector * 4 + 2, 0xF000);
    }

    /// <summary>
    /// Programs the two PICs the way the BIOS does: IRQ 0-7 onto vectors
    /// 0x08-0x0F, IRQ 8-15 onto vectors 0x70-0x77. Note that the master's
    /// range collides with the CPU's own exception vectors, a design mistake
    /// IBM made in 1981 and which nobody could ever fix without breaking every
    /// program in existence.
    /// </summary>
    private void InitializeInterruptController()
    {
        // Master
        Io.WriteByte(0x20, 0x11);   // ICW1: begin initialisation, ICW4 will follow
        Io.WriteByte(0x21, 0x08);   // ICW2: vector base
        Io.WriteByte(0x21, 0x04);   // ICW3: the slave hangs off line 2
        Io.WriteByte(0x21, 0x01);   // ICW4: 8086 mode
        Io.WriteByte(0x21, 0xB8);   // mask: leave timer, keyboard and cascade enabled

        // Slave
        Io.WriteByte(0xA0, 0x11);
        Io.WriteByte(0xA1, 0x70);
        Io.WriteByte(0xA1, 0x02);
        Io.WriteByte(0xA1, 0x01);
        // IRQ 12 is the mouse and is the one slave line that is wired up, so
        // it is the one bit left clear. IRQ 2 on the master is already open,
        // which it must be: the slave reaches the CPU only through it, and a
        // masked cascade silently swallows every interrupt from 8 to 15.
        Io.WriteByte(0xA1, 0xEF);
    }

    private void ClearScreen()
    {
        for (int i = 0; i < 80 * 25; i++)
        {
            uint offset = VgaMemory.TextWindowOffset + (uint)(i * 2);
            VideoMemory[offset] = (byte)' ';
            VideoMemory[offset + 1] = 0x07;
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

        cpu.Io.WriteByte(0x20, 0x20);   // end of interrupt
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

                // Function 15h is MSCDEX, which answers for the CD-ROM.
                if ((function >> 8) == 0x15 && CompactDiscServices.Handle(cpu)) return;

                // No other resident service is installed. Leaving AX
                // untouched is precisely how INT 2Fh says "nobody answered".
                return;
        }
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

    private void HandleSystemServices(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);   // AH

        switch (function)
        {
            case 0x88:
                // Extended memory, in KB above the first megabyte.
                uint extendedKb = Memory.RamSize > 0x100000
                    ? (Memory.RamSize - 0x100000) / 1024 : 0;
                cpu.State.Ax = (ushort)Math.Min(extendedKb, 0xFFFF);
                cpu.SetCarryInStackedFlags(false);
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
                // System configuration table. Not provided: some programs ask
                // for it, but all of them cope with its absence.
                cpu.State.SetReg8(4, 0x86);
                cpu.SetCarryInStackedFlags(true);
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

    /// <summary>
    /// INT 19h: loads the boot sector and hands control to it.
    ///
    /// The sector is read to 0000:7C00, an address fixed by the PC
    /// architecture, and must end with the 0xAA55 signature -- without it the
    /// disk is not considered bootable.
    /// </summary>
    private void Boot(CpuNs.Cpu cpu)
    {
        foreach (DiskImage disk in _disks.OrderBy(d => d.DriveNumber))
        {
            var sector = new byte[DiskImage.SectorSize];
            if (!disk.ReadSectors(0, 0, 1, 1, sector)) continue;

            if (sector[510] != 0x55 || sector[511] != 0xAA) continue;

            const uint bootAddress = 0x7C00;
            sector.CopyTo(Memory.GetRamSpan(bootAddress, sector.Length));

            // The boot sector expects DL to hold the drive it was loaded
            // from: that is how it knows where to find the rest of the system.
            cpu.State.SetReg8(2, disk.DriveNumber);

            cpu.SetSegment(CpuNs.SegReg.Cs, 0);
            cpu.State.Eip = bootAddress;
            cpu.SetSegment(CpuNs.SegReg.Ds, 0);
            cpu.SetSegment(CpuNs.SegReg.Es, 0);
            cpu.SetSegment(CpuNs.SegReg.Ss, 0);
            cpu.State.Esp = 0x7C00;      // stack grows down from just below the sector

            Booted = true;
            return;
        }

        // No bootable disk: halt, which is what a real PC does after printing
        // "No bootable device".
        cpu.State.Halted = true;
    }

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
    private void TryInstallRedirector()
    {
        if (Redirector.IsInstalled) return;
        if (!Redirector.MountedDrives.Any()) return;

        if (--_redirectorInstallCountdown > 0) return;
        _redirectorInstallCountdown = RedirectorInstallInterval;

        _redirectorInstaller.TryInstall(Redirector);
    }

    /// <summary>Batches between attempts to find DOS's structures.</summary>
    private const int RedirectorInstallInterval = 200;
    private int _redirectorInstallCountdown = 1;

    /// <summary>
    /// Attaches a filesystem to a drive letter through the redirector, and
    /// installs the redirector at once if DOS is already up.
    ///
    /// The letter is a number with 0 meaning A:, the convention DOS uses
    /// everywhere below the surface.
    /// </summary>
    public void MountRedirectedDrive(byte drive, IRedirectedFileSystem fileSystem)
    {
        Redirector.Mount(drive, fileSystem);
        _redirectorInstaller.TryInstall(Redirector);
    }

    /// <summary>Hands a drive letter back to DOS.</summary>
    public void UnmountRedirectedDrive(byte drive) => Redirector.Unmount(drive);

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

    /// <summary>
    /// Fills in the diskette parameter table that INT 1Eh points at.
    ///
    /// Most of the eleven bytes are timings for a drive we do not simulate --
    /// how long the head takes to settle, how long the motor needs to come up
    /// to speed. The one that matters is the sectors per track, which is how
    /// DOS knows where one track ends and the next begins. Give it 18 for a
    /// disk that has 36 and it reads half of every track, finds half a
    /// directory, and goes looking for the rest where a smaller disk would
    /// have kept it.
    /// </summary>
    private void WriteDiskParameterTable(int sectorsPerTrack)
    {
        uint address = ((uint)DiskParameterSegment << 4) + DiskParameterOffset;

        // The values a PC/AT BIOS publishes, with the gap lengths that suit
        // the density: a 2.88 MB disk packs its sectors more tightly.
        bool dense = sectorsPerTrack >= 36;

        Memory.WriteByte(address + 0, 0xAF);   // step rate and head unload time
        Memory.WriteByte(address + 1, 0x02);   // head load time; DMA in use
        Memory.WriteByte(address + 2, 0x25);   // ticks before the motor stops
        Memory.WriteByte(address + 3, 0x02);   // 512 bytes a sector
        Memory.WriteByte(address + 4, (byte)sectorsPerTrack);
        Memory.WriteByte(address + 5, dense ? (byte)0x1B : (byte)0x1B);   // gap length
        Memory.WriteByte(address + 6, 0xFF);   // data length, unused at 512 bytes
        Memory.WriteByte(address + 7, dense ? (byte)0x54 : (byte)0x6C);   // format gap
        Memory.WriteByte(address + 8, 0xF6);   // the byte a format writes
        Memory.WriteByte(address + 9, 0x0F);   // head settle time
        Memory.WriteByte(address + 10, 0x08);  // motor start time
    }

    /// <summary>
    /// Where the diskette parameter table lives.
    ///
    /// It has to be in RAM, not in the ROM: the values change with the disk
    /// that is in the drive, and DOS itself may write to it -- some versions
    /// point the vector at a copy of their own. It sits just above the BIOS
    /// data area, in the space the interrupt vectors leave free.
    /// </summary>
    private const ushort DiskParameterSegment = 0x0000;
    private const ushort DiskParameterOffset = 0x0522;

    /// <summary>Writes a vector that points at data rather than at code.</summary>
    private void SetVectorFar(byte vector, ushort segment, ushort offset)
    {
        Memory.WriteWord((uint)(vector * 4), offset);
        Memory.WriteWord((uint)(vector * 4 + 2), segment);
    }

    /// <summary>
    /// Reports mouse movement, in the units the hardware counts: how far it
    /// moved since last time, with Y positive downwards.
    ///
    /// This is relative and not a position on purpose. A mouse does not know
    /// where the pointer is -- the guest's driver owns that, and applies its
    /// own sensitivity and its own limits to decide. Handing it an absolute
    /// position would fight whatever the guest believes.
    /// </summary>
    public void MoveMouse(int deltaX, int deltaY) => Mouse.Move(deltaX, deltaY);

    /// <summary>Presses or releases a mouse button.</summary>
    public void SetMouseButton(MouseButtons button, bool pressed)
        => Mouse.SetButton(button, pressed);

    /// <summary>Tells the machine a key has been pressed.</summary>
    public void PressKey(byte scanCode) => Keyboard.PushScanCode(scanCode);

    /// <summary>Tells the machine a key has been released; bit 7 marks the break code.</summary>
    public void ReleaseKey(byte scanCode) => Keyboard.PushScanCode((byte)(scanCode | 0x80));

    /// <summary>The screen's contents as plain text, for tests and diagnostics.</summary>
    public string GetScreenText()
    {
        var builder = new System.Text.StringBuilder();
        int columns = VideoRegisters.TextColumns;
        int rows = VideoRegisters.TextRows;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                uint offset = VgaMemory.TextWindowOffset + (uint)((row * columns + column) * 2);
                byte ch = VideoMemory[offset];
                builder.Append(ch is >= 32 and < 127 ? (char)ch : ' ');
            }
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        foreach (DiskImage disk in _disks) disk.Dispose();
        CompactDisc?.Dispose();
        Memory.Dispose();
    }
}
