using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Apic;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Timing;
using Aurora386.Core.Devices.System;
using Aurora386.Core.Devices.Network;
using Aurora386.Core.Devices.IsaPnp;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Devices.Chipset;
using Aurora386.Core.Devices.Audio;
using Aurora386.Core.Devices.Cards;
using Aurora386.Core.Devices.Video;
using Aurora386.Core.Dos;
using Aurora386.Core.IO;
using Aurora386.Core.Memory;
using Aurora386.Core.Timing;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core;

/// <summary>
/// The complete emulated PC: CPU, memory, devices and BIOS wired together and
/// ready to execute. This is the class that decides what kind of machine we
/// are pretending to be.
/// </summary>
public sealed partial class Machine : IDisposable
{
    public PhysicalMemoryBus Memory { get; }
    public IoBus Io { get; }
    public PciBus Pci { get; }
    public PciBios PciServices { get; }
    public CpuNs.Cpu Cpu { get; }

    public InterruptController Interrupts { get; }

    /// <summary>
    /// The cycle-accurate event queue that sits alongside <see
    /// cref="AdvanceDevices"/>: due cycles are the same count as <see
    /// cref="CpuNs.Cpu.Cycles"/>. Nothing schedules on it yet -- the PIT and
    /// the sound card still move with the batch advance -- but
    /// <c>RunInstructions</c> already shortens a batch to stop exactly at the
    /// next due event and fires it before the batch after. See <see
    /// cref="Scheduler"/>'s summary for what a reset does to it.
    /// </summary>
    public Scheduler Scheduler { get; } = new();

    public Pit8253 Timer { get; }
    public Keyboard8042 Keyboard { get; }
    public SystemControlPort SystemControl { get; }
    public SystemPort61 SystemPort { get; }
    public Cmos Cmos { get; }

    /// <summary>
    /// The mouse itself: one physical device, read by three different
    /// interfaces. Which of them the guest listens to depends entirely on
    /// what driver it loaded, so both ports are fitted and INT 33h is
    /// provided on top of whichever one the mouse is plugged into --
    /// see <see cref="MouseInterface"/>.
    /// </summary>
    public MouseState Mouse { get; }

    /// <summary>The mouse on the 8042's auxiliary port, interrupting on IRQ 12.</summary>
    public Ps2Mouse Ps2Mouse { get; }

    /// <summary>A Microsoft serial mouse on COM1, for drivers that expect one there.</summary>
    public SerialMouse SerialMouse { get; }

    public VgaRegisters VideoRegisters { get; }
    public VgaMemory VideoMemory { get; }
    public VgaRenderer Video { get; }

    /// <summary>The S3's 2D drawing engine: fills, copies and lines.</summary>
    public S3Accelerator Accelerator { get; }

    // Audio: the DMA controller is a system device rather than part of the
    // sound card, but the card is the only thing here that uses it.
    public Dma8237 Dma { get; }

    /// <summary>
    /// The host-side mix every sound card's output ends up in (F1.6). A
    /// front end pulls interleaved stereo frames from here, at <see
    /// cref="AudioMixer.OutputSampleRate"/>, rather than from any one card's
    /// own buffer, which is what lets more than one audio source (the SB16
    /// today, later an ES1371 and an MPU-401 synth) share one host device.
    /// </summary>
    public AudioMixer Audio { get; }

    /// <summary>
    /// Every expansion card fitted -- sound, network and USB -- in the stable
    /// order that fixed its Plug and Play select number when the machine was
    /// built. <see cref="EnableUsb"/> may append the USB card to this list
    /// after construction, for a machine that started with none configured;
    /// nothing else does, so every other card's position is fixed for the
    /// machine's whole life.
    /// </summary>
    public IReadOnlyList<IExpansionCard> Cards { get; private set; } = [];

    /// <summary>
    /// The bus every expansion card was, or will be, attached through --
    /// kept so that <see cref="EnableUsb"/> can build the USB card the same
    /// way at run time as the constructor builds every other card.
    /// </summary>
    private readonly MachineBus _bus;

    /// <summary>
    /// The first Sound Blaster 16 card fitted, or null on a machine built
    /// with an empty sound list. With no card, ports 220h-22Fh answer as an
    /// empty bus -- 0xFF, like any unclaimed port -- and nothing else about
    /// the machine notices.
    /// </summary>
    public SoundBlaster16? SoundBlaster { get; private set; }

    /// <summary>The optional standalone MPU-401 UART, whose output sink is owned by this machine.</summary>
    public Mpu401Uart? Midi { get; private set; }

    /// <summary>
    /// The first NE2000 card fitted, or null on a machine built with an empty
    /// network list. With no card, ports 300h-31Fh answer as an empty bus and
    /// no Plug and Play identity for it is on the ISA bus either.
    /// </summary>
    public Ne2000? Network { get; private set; }

    public BiosDataArea Bda { get; }
    public VideoServices VideoServices { get; }
    public DiskServices DiskServices { get; }
    public KeyboardServices KeyboardServices { get; }

    /// <summary>
    /// The IDE controller, which is how a real CD-ROM driver reaches the
    /// drive. Everything else about disks goes through the BIOS, which is
    /// enough for DOS and not enough for anyone who talks to the hardware.
    /// </summary>
    public IdeController Ide { get; }

    /// <summary>The secondary IDE channel (170h, IRQ 15), home of the second hard disk.</summary>
    public IdeController IdeSecondary { get; }

    /// <summary>
    /// The DOS network redirector: what makes a host folder or a CD appear as
    /// a drive letter whose files can be opened with ordinary DOS calls.
    /// </summary>
    public RedirectorServices Redirector { get; }

    /// <summary>INT 33h, the mouse driver interface DOS programs and Windows use.</summary>
    public MouseServices MouseServices { get; }
    public PointingDeviceServices PointingDevice { get; }
    public XmsManager Xms { get; }
    public PnpBios PlugAndPlay { get; }

    /// <summary>
    /// The guest asked for the power to go off, through APM. Nothing here
    /// stops the processor -- that is the front end's decision, and it may
    /// want to show "It is now safe to turn off your computer" first.
    /// </summary>
    public event Action? PowerOffRequested;

    internal void RequestPowerOff() => PowerOffRequested?.Invoke();
    public IsaPnpBus IsaPlugAndPlay { get; }

    /// <summary>
    /// The letter the guest gives the CD-ROM, as last asked of it, counting
    /// from zero for A:; null until DOS is up or when there is no drive.
    /// It is the guest's answer, not the emulator's guess: MSCDEX decides
    /// once it has seen how many disks there are.
    /// </summary>
    public byte? CompactDiscLetter { get; private set; }

    public event Action<byte?>? CompactDiscLetterChanged;

    /// <summary>Asks the guest now, and reports a change. Null when the guest cannot be asked.</summary>
    public byte? QueryCompactDiscLetter()
    {
        byte? letter = _redirectorInstaller.QueryCompactDiscLetter(Redirector);
        if (letter != CompactDiscLetter)
        {
            CompactDiscLetter = letter;
            CompactDiscLetterChanged?.Invoke(letter);
        }
        return letter;
    }
    public ApmBios PowerManagement { get; }

    /// <summary>
    /// The software coprocessor. This machine has no 80387, so floating-point
    /// instructions fault to vector 7 and are finished here instead.
    /// </summary>
    public FpuEmulator Fpu { get; } = new();

    /// <summary>
    /// The clock speed the emulated CPU claims to run at. It does not control
    /// how fast we actually execute; it is the ratio used to convert executed
    /// instructions into device time, so it sets how fast the guest perceives
    /// its own hardware to be.
    /// </summary>
    public int CpuFrequencyHz { get; set; } = 25_000_000;   // a 25 MHz 386

    /// <summary>
    /// Converts a span of emulated wall-clock time into the number of CPU
    /// cycles it takes at <see cref="CpuFrequencyHz"/> -- one cycle per
    /// instruction today. For a device scheduling itself on <see
    /// cref="Scheduler"/> from a datasheet timing given in microseconds,
    /// rather than working out the ratio by hand each time.
    /// </summary>
    public long CyclesFromMicroseconds(double microseconds)
        => (long)(microseconds * CpuFrequencyHz / 1_000_000.0);

    /// <summary>True once the boot sector has been loaded and given control.</summary>
    public bool Booted { get; private set; }


    private readonly List<DiskImage> _disks = [];
    private readonly RedirectorInstaller _redirectorInstaller;

    /// <summary>Diagnostics: where DOS keeps its list of lists.</summary>
    public uint DosListOfLists => _redirectorInstaller.ListOfListsAddress;

    /// <summary>
    /// Where the SVGA linear frame buffer appears in physical memory: the
    /// whole of video memory as one flat array, which is what a display
    /// driver in protected mode uses instead of moving a 64 KB window about.
    /// </summary>
    public uint LinearFrameBufferBase { get; private set; }

    /// <summary>
    /// Where the drawing engine's registers appear as memory: a 64 KB window
    /// directly above the frame buffer.
    /// </summary>
    public uint MmioBase { get; private set; }

    /// <summary>The Trio64V+ memory-mapped registers sit this far above the linear frame buffer.</summary>
    public const uint TrioMmioOffset = 0x1000000;

    /// <summary>The I/O APIC's fixed physical address (coordinator decision A3: no relocation register is modelled).</summary>
    public const uint IoApicPhysicalBase = 0xFEC00000;

    /// <summary>The S3's command register: I/O space (bit 0) and memory space (bit 1) always decode.</summary>
    private const uint S3CommandFixedDecode = 0x3;

    /// <summary>The motherboard selected at construction; expansion cards retain their own configuration.</summary>
    public Chipset Chipset { get; }

    /// <summary>The PIIX4 IDE DMA engine, absent on the default PIO-only motherboard.</summary>
    public BusMasterIdeController? BusMasterIde { get; }

    /// <summary>The PIIX4 power-management timer and register block.</summary>
    public Piix4PowerManagement? Piix4Power { get; }

    /// <summary>
    /// The local APIC wrapper in front of <see cref="Interrupts"/> (work
    /// package P4.4): always present, on either chipset, because the local
    /// APIC is part of the CPU package rather than the motherboard. It
    /// forwards the 8259 pair completely unchanged unless the current
    /// model's silicon has an APIC, the unit is hardware-enabled, and
    /// <see cref="Imcr"/> (when fitted) has switched the IMCR to 1 -- see
    /// its own summary for coordinator decision A1.
    /// </summary>
    public LocalApic Apic { get; }

    /// <summary>The i430TX/PIIX4 board's I/O APIC, fixed at FEC00000h; null on an i430FX board (decision A2).</summary>
    public IoApic? IoApic { get; }

    /// <summary>The i430TX/PIIX4 board's IMCR (ports 22h/23h); null on an i430FX board, which has none (decision A2).</summary>
    public Imcr? Imcr { get; }

    public Machine(
        uint ramSize = 16 * 1024 * 1024,
        HardwareConfiguration? hardware = null,
        Chipset chipset = Chipset.I430Fx,
        CpuNs.CpuModel? initialCpuModel = null)
    {
        if (!Enum.IsDefined(chipset))
        {
            throw new ArgumentOutOfRangeException(nameof(chipset));
        }

        Chipset = chipset;
        hardware ??= HardwareConfiguration.Default;

        // The addressable space is larger than the RAM, because the linear
        // frame buffer lives above it. A real card put it above the 16 MB
        // line -- below that is the ISA hole and whatever memory the machine
        // has, and a card cannot know how much of either -- so here it goes
        // directly above the installed RAM, which is the same reasoning
        // applied to a machine whose memory size is known.
        uint frameBufferBase = Math.Max(ramSize, 16u * 1024 * 1024);
        // The addressable space reaches past the frame buffer to the 64 KB
        // of engine registers a Trio64V+ keeps at the frame buffer's base
        // plus 16 MB. A write past the end of the bus is dropped silently,
        // and that silence is what a display driver drawing through those
        // registers looked like: icons drawn by the processor, and nothing
        // where the engine should have filled.
        //
        // The local APIC's MMIO page (FEE00000h) and the I/O APIC's fixed
        // window (FEC00000h, work package P4.4) sit far above this: rather
        // than raising this ceiling to reach them, which would force the
        // dense per-page arrays below to span most of the 4 GB address
        // space for every machine whether or not it ever uses an APIC, they
        // are registered as high windows (see PhysicalMemoryBus.RegisterHighWindow)
        // once the local APIC and, on the i430TX board, the I/O APIC exist.
        Memory = new PhysicalMemoryBus(ramSize, frameBufferBase + TrioMmioOffset + 0x10000);
        // No option ROMs are fitted here. Reporting writable RAM makes
        // EMM386 reserve the entire range instead of mapping UMB pages into
        // it. Its page tables supply extended RAM behind these addresses.
        Memory.Unmap(0xC0000, 0x30000);
        LinearFrameBufferBase = frameBufferBase;
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

        // And the same memory again, flat, above the RAM. A protected-mode
        // display driver writes here and never touches the bank register.
        Memory.MapDevice(LinearFrameBufferBase, VgaMemory.TotalSize, VideoMemory.Linear);

        // And the card is told where that is, because a driver asks the
        // hardware rather than being configured with the answer.
        VideoRegisters.ApertureBase = LinearFrameBufferBase;

        Video = new VgaRenderer(VideoMemory, VideoRegisters);

        // The 2D engine, which is what makes the SVGA resolutions usable: at
        // 1024x768 a window scroll done by the processor means three quarters
        // of a megabyte across the bus, and done by the card means a dozen
        // register writes.
        Accelerator = new S3Accelerator(VideoMemory, VideoRegisters);
        Accelerator.Register(Io);
        VideoMemory.Engine = Accelerator.Mmio;

        // The engine's registers again, mapped as memory directly above the
        // frame buffer. A protected-mode driver writes there rather than to
        // the I/O ports: an OUT from protected-mode code is a fault the
        // monitor has to catch and emulate, and a memory write is not -- on a
        // 486 that is a few hundred cycles per register against a few.
        MmioBase = LinearFrameBufferBase + VgaMemory.TotalSize;
        Memory.MapDevice(MmioBase, 0x10000, Accelerator.Mmio);
        VideoRegisters.MmioBase = MmioBase;

        // And where the Trio64V+ actually puts them: 16 MB above the
        // linear frame buffer, whatever the card's memory size. The S3
        // Windows driver for that chip writes there without asking, and
        // without being told to by any register; the emulator learnt this
        // from a trace of a driver that filled nothing.
        Memory.MapDevice(LinearFrameBufferBase + TrioMmioOffset, 0x10000, Accelerator.Mmio);

        // The 8237 DMA pair. Any card that streams through it -- the sound
        // card, below -- reaches it through MachineBus rather than owning it:
        // on real hardware this was wired to the slot connector, not fitted
        // to any one card.
        Dma = new Dma8237(Memory);
        Dma.Register(Io);

        // Every card -- every expansion card built below -- is Plug and Play
        // as well as being at its usual address: DOS finds it where it
        // expects, and Windows 95 finds it by asking the bus, and may move
        // it. The bus itself starts answering its own three ports only once
        // every card has had a chance to add itself, further down.
        IsaPlugAndPlay = new IsaPnpBus();

        Cpu = new CpuNs.Cpu(Memory, Io) { Interrupts = Interrupts };

        if (initialCpuModel is { } startupModel)
        {
            // Applied before the BIOS ROM is built and before RunPost reads
            // Cpu.State.Features: Machine.CpuModel (below) can only be set
            // after construction, by which time InstallBios has already
            // burned the CPUID-derived MP tables into the fixed ROM image,
            // so a caller that wants the BSP's MP table processor entry (or
            // its CPUID-visible APIC bit) to describe anything other than
            // the default model has to say so here instead.
            Cpu.State.SelectModel(startupModel);
            Cpu.State.Reset();
        }

        // The local APIC wrapper: always built, on either chipset, since an
        // on-die APIC belongs to the CPU package rather than the
        // motherboard (coordinator decision A2). Reading APIC_BASE's raw
        // value once up front, rather than waiting for the first WRMSR,
        // matches the register's actual reset state (enable bit already
        // set) the moment the wrapper starts answering Cpu.Interrupts.
        Apic = new LocalApic(Interrupts, Cpu, Memory, Scheduler, () => Cpu.Cycles);
        Cpu.Msrs.ApicBaseChanged += Apic.OnApicBaseChanged;
        Apic.OnApicBaseChanged(Cpu.Msrs.ApicBaseRaw);
        Cpu.Interrupts = Apic;

        Bda = new BiosDataArea(Memory);
        VideoServices = new VideoServices(VideoMemory, VideoRegisters, Bda);
        VideoServices.Vesa = new VesaServices(this);
        DiskServices = new DiskServices(Bda);
        KeyboardServices = new KeyboardServices(Bda, Memory, Keyboard);
        CompactDiscServices = new CompactDiscServices(this);

        bool hasPiix4 = chipset == Chipset.I430Tx;
        Ide = new IdeController(this, Interrupts, hasBusMasterDma: hasPiix4);
        Ide.Register(Io);

        // The second cable, for a second disk. It carries no CD-ROM: that
        // is the slave of the first, where drivers of the period expect it.
        IdeSecondary = new IdeController(
            this, Interrupts, IdeController.Channel.Secondary, hasBusMasterDma: hasPiix4);
        IdeSecondary.Register(Io);
        Pci = new PciBus();
        Pci.DmaMemory = new PhysicalDmaMemory(Memory);
        ushort hostBridgeId = hasPiix4 ? (ushort)0x7100 : (ushort)0x122D;
        ushort isaBridgeId = hasPiix4 ? (ushort)0x7110 : (ushort)0x122E;
        var pciHost = new PciFunction(0x0000, 0x8086, hostBridgeId, 0x060000);
        if (hasPiix4)
        {
            pciHost.Define(8, 1, 1); // Production 82439TX A1/A2 revision.
        }

        Pci.Add(pciHost);
        var pciIsa = new PciFunction(0x0008, 0x8086, isaBridgeId, 0x060100, headerType: 0x80);
        Pci.Add(pciIsa);
        Pci.InterruptRouter = new PciInterruptRouter(Interrupts, pciIsa);

        if (hasPiix4)
        {
            // The I/O APIC and the IMCR exist only on this board (decision
            // A2); a P54C or earlier CPU dropped into a TX board still gets
            // them, inert, since IMCR stays in PIC mode until a guest OS
            // switches it -- which nothing without an APIC would ever do.
            //
            // Registered as a high window (Memory.RegisterHighWindow), not
            // MapDevice: FEC00000h sits far above every other physical
            // address this machine answers, and the fixed window mechanism
            // exists precisely so that reaching it costs nothing on any
            // address below it (see PhysicalMemoryBus's own summary).
            IoApic = new IoApic(Apic, Interrupts);
            Memory.RegisterHighWindow(IoApicPhysicalBase, Aurora386.Core.Devices.Apic.IoApic.PageSize, IoApic);
            Interrupts.AttachIoApic(IoApic);

            // EOI of a level-triggered vector tells every I/O APIC to clear
            // the matching Remote IRR bit (plans/specs/apic.md §1.3): there
            // is only one I/O APIC on this board, but the wiring is the same
            // direct call a second one would need.
            Apic.LevelTriggeredEoi += IoApic.ClearRemoteIrr;

            Imcr = new Imcr();
            Imcr.Register(Io);
            Imcr.ApicModeChanged += mode => Apic.ImcrApicMode = mode;
        }
        // Both chipsets use compatibility ports. Only PIIX4 exposes a DMA engine.
        ushort ideDeviceId = hasPiix4 ? (ushort)0x7111 : (ushort)0x1230;
        uint ideClass = hasPiix4 ? 0x010180u : 0x010100u;
        ushort ideCommandMask = hasPiix4 ? (ushort)5 : (ushort)1;
        var pciIde = new PciFunction(0x0009, 0x8086, ideDeviceId, ideClass,
            commandMask: ideCommandMask);
        pciIde.Define(4, 1, 2, ideCommandMask);
        // These reset defaults include BIOS setup, as does PCI command above.
        // IDETIM bit 15 enables each cable. Windows 95 reported code 29
        // with these registers zero despite the ports being decoded.
        // PIIX reserves bits 14 and 11:10; PIIX3 has a different bit-14 use.
        uint ideTimingMask = hasPiix4 ? 0xF3FFu : 0xB3FFu;
        pciIde.Define(0x40, 0x8000, 2, ideTimingMask);
        pciIde.Define(0x42, 0x8000, 2, ideTimingMask);
        pciIde.ConfigurationChanged += () =>
        {
            bool ioEnabled = (pciIde.Command & 1) != 0;
            bool primaryEnabled = (pciIde.Read(0x40, 2) & 0x8000) != 0;
            bool secondaryEnabled = (pciIde.Read(0x42, 2) & 0x8000) != 0;
            Ide.SetIoDecoding(Io, ioEnabled && primaryEnabled);
            IdeSecondary.SetIoDecoding(Io, ioEnabled && secondaryEnabled);
        };
        Pci.Add(pciIde);

        if (hasPiix4)
        {
            // Timing selections are retained for the driver; transfers use
            // the same deterministic sector engine for every negotiated mode.
            pciIde.Define(0x44, 0, 1, 0xFF); // SIDETIM, slave DMA/PIO timing.
            pciIde.Define(0x48, 0, 1, 0x0F); // UDMACTL, one enable bit per drive.
            pciIde.Define(0x4A, 0, 2, 0x3333); // UDMATIM, two bits per drive.
            pciIde.DefineBar(4, 16, io: true, address: 0xC000);
            BusMasterIde = new BusMasterIdeController(
                pciIde, Io, Pci.DmaMemory, Ide, IdeSecondary);

            var pciPower = new PciFunction(0x000B, 0x8086, 0x7113, 0x068000);
            Piix4Power = new Piix4PowerManagement(
                pciPower, Io, 0xB000, () => Cpu.Cycles, () => CpuFrequencyHz);
            Pci.Add(pciPower);
        }

        // The S3 Trio64V+ as a PCI display adapter.
        //
        // Windows 95 will not use a card-specific display driver unless it can
        // identify the card, and for an S3 of this generation it identifies it
        // by PCI vendor and device ID -- its driver database lists the Trio64V+
        // as PCI\VEN_5333&DEV_8811 and nothing else. With the card absent from
        // the bus, Plug and Play found no display device, Windows fell back to
        // the standard VGA driver, and the desktop ran 640x480 in sixteen
        // colours with the accelerator never touched. The registers and the
        // memory windows are reached the same way they always were; this entry
        // exists so that the card can be found.
        //
        // The base address register advertises the linear framebuffer, which
        // is where the driver expects the aperture: prefetchable memory, and
        // the low bits read back as zero so that software sizing the region
        // sees how large it is.
        var pciVideo = new PciFunction(0x0020, 0x5333, 0x8811, 0x030000);
        pciVideo.Define(8, 0x03000000 | 0x54, 4);      // class, with revision 0x54

        // Memory space is decoded unconditionally and the aperture never
        // moves: the three physical windows behind it -- the linear
        // framebuffer, the engine registers just above it, and the
        // Trio64V+'s real MMIO location 16 MB further up -- are wired
        // directly below, in Devices/Video/**, which this plan does not
        // touch (F1.5 is chipset and PCI, not video). Command bits 0 and 1
        // are fixed at 1 (read-only) so a driver that actually checks them
        // before touching the VGA ports or the aperture sees the truth
        // rather than a decode bit that says off while the hardware answers
        // anyway; the base address
        // register itself stays writable so sizing continues to work, but a
        // base other than the one fitted below is accepted into
        // configuration space and simply not honoured -- fixed-and-honest,
        // not movable.
        pciVideo.Define(4, S3CommandFixedDecode, 2, 0);

        // The base address register advertises the linear framebuffer:
        // prefetchable memory, at the address the aperture really answers.
        //
        // The writable mask is what states the size, and it has to state the
        // true one. Software sizes a region by writing all ones and reading
        // back: the bits that stay clear are the ones the card does not
        // decode, so the lowest bit that can be set is the size. A mask of
        // 0xFC000000 claims 64 MB of aperture over 2 MB of memory, and a
        // driver that maps what it is told to then addresses memory the card
        // never answers at.
        const uint apertureMask = ~(uint)(VgaMemory.TotalSize - 1);
        pciVideo.Define(0x10, LinearFrameBufferBase | 0x08, 4, apertureMask);
        Pci.Add(pciVideo);

        Pci.Register(Io);
        PciServices = new PciBios(Pci);

        // Expansion cards: built from the configuration in a stable order,
        // which is what fixes their PCI device numbers and Plug and Play
        // select numbers. Sound, network and USB have all moved into this
        // registry (F1.4a, F1.4b, F1.4c). The bus is kept so that EnableUsb
        // can build the same card at run time, for a machine started with
        // none configured -- see that method's own summary.
        Audio = new AudioMixer();
        _bus = new MachineBus(Io, Memory, Interrupts, Dma, Pci, IsaPlugAndPlay,
            Scheduler, Audio, () => CpuFrequencyHz, chipset);

        // Built first, unattached: the factory step alone claims nothing --
        // no port, no PCI function, and critically no host socket for a
        // NAT-backed network card -- so the collision check below runs
        // before any card has opened a resource that would need closing if
        // the check fails.
        var cards = new List<IExpansionCard>();
        foreach (CardSettings settings in hardware.Sound)
        {
            cards.Add(CardCatalog.Build(_bus, settings));
        }
        foreach (CardSettings settings in hardware.Network)
        {
            cards.Add(CardCatalog.Build(_bus, settings));
        }
        CardSettings? usbSettings = hardware.Usb;
        if (hasPiix4 && usbSettings is null)
        {
            // PIIX4's integrated USB function is part of the motherboard.
            usbSettings = new CardSettings { Card = "uhci-piix" };
        }

        if (usbSettings is not null)
        {
            cards.Add(CardCatalog.Build(_bus, usbSettings));
        }

        // Two cards of the same kind would keep their state under the same
        // snapshot section name -- IExpansionCard.SnapshotId is a fixed
        // four letters per card type, not assigned per instance -- and
        // MachineSnapshot has no way to tell them apart on either capture or
        // restore. Rather than silently letting the second card's state
        // overwrite the first's section (or restore into the wrong card),
        // such a configuration is refused up front: fitting two of the same
        // kind is not supported yet.
        IGrouping<string, IExpansionCard>? collision = cards
            .GroupBy(card => card.SnapshotId)
            .FirstOrDefault(group => group.Count() > 1);
        if (collision is not null)
        {
            throw new ArgumentException(
                $"Two cards fitted to this machine both keep their state under snapshot section " +
                $"'{collision.Key}': fitting two of the same kind is not supported yet.", nameof(hardware));
        }

        ValidateExpansionWindows(hardware.Sound, hardware.Network);

        // Only now does each card claim its ports, its PCI function, its
        // Plug and Play identity and -- for a NAT-backed network card -- its
        // host listening sockets. If attaching one card throws (two cards
        // asked for the same forwarded host port, say), whatever the cards
        // before it already opened is closed again before the exception
        // reaches the caller, who has no machine to dispose: the
        // constructor never returns one.
        var attached = new List<IExpansionCard>();
        try
        {
            foreach (IExpansionCard card in cards)
            {
                card.Attach(_bus);
                attached.Add(card);
            }
        }
        catch
        {
            DisposeNetworkBackends(attached);
            throw;
        }

        Cards = cards;
        SoundBlaster = Cards.OfType<SoundBlaster16Card>().FirstOrDefault()?.Device;
        Midi = Cards.OfType<Mpu401Card>().FirstOrDefault()?.Device;
        Network = Cards.Select(GetNetworkDevice).FirstOrDefault(device => device is not null);
        if (Cards.OfType<UhciCard>().FirstOrDefault() is { } usbCard)
        {
            WireUsbCard(usbCard);
        }

        // Registered last, now that every card -- the network card above and
        // whatever sound cards were just built -- has added its identity.
        IsaPlugAndPlay.Register(Io);

        Redirector = new RedirectorServices(this);
        _redirectorInstaller = new RedirectorInstaller(this);
        WatchForCommandProcessor();
        MouseServices = new MouseServices(this, Mouse);
        PointingDevice = new PointingDeviceServices(this);
        Video.Pointer = MouseServices.Pointer;
        Xms = new XmsManager(Memory);
        PlugAndPlay = new PnpBios(this);
        PowerManagement = new ApmBios(this);

        InstallBios();
    }

    /// <summary>
    /// True once DOS has launched its command processor.
    ///
    /// This is the signal the redirector needs before it may take the INT 2Fh
    /// vector, and finding one took a while. Hooking earlier stops CONFIG.SYS
    /// loading its device drivers, and "the structures have not moved lately"
    /// does not distinguish a finished DOS from one that has not started:
    /// the small three-entry directory array DOS builds first sits perfectly
    /// still for a long stretch before CONFIG.SYS is even read.
    ///
    /// COMMAND.COM starting is unambiguous. CONFIG.SYS is done by then --
    /// that is what DOS does next -- so the drive table has reached its full
    /// size and the vector is safe to share.
    /// </summary>
    public bool CommandProcessorStarted { get; private set; }

    /// <summary>
    /// Watches for the command processor being launched, which is an ordinary
    /// INT 21h function 4B like any other program's.
    /// </summary>
    private void WatchForCommandProcessor()
    {
        Cpu.SoftwareInterruptIssued += vector =>
        {
            if (CommandProcessorStarted || vector != 0x21) return;
            if ((byte)(Cpu.State.Eax >> 8) != 0x4B) return;

            // The name is at DS:DX, and DOS launches it by a path that ends
            // in COMMAND.COM whether or not a SHELL= line moved it.
            uint at = Cpu.State.Ds.Base + Cpu.State.Dx;
            Span<char> name = stackalloc char[16];
            int length = 0;

            for (uint i = 0; i < 64 && length < name.Length; i++)
            {
                byte character = Memory.ReadByte(at + i);
                if (character == 0) break;

                // Only the tail is needed, so the window slides.
                if (length == name.Length)
                {
                    for (int j = 1; j < length; j++) name[j - 1] = name[j];
                    length--;
                }
                name[length++] = char.ToUpperInvariant((char)character);
            }

            if (length >= 11 && name[..length].EndsWith("COMMAND.COM"))
                CommandProcessorStarted = true;
        };
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

    /// <summary>
    /// Which processor is fitted.
    ///
    /// This is the closest thing the emulator has to opening the case and
    /// changing the chip, and like that operation it only makes sense with
    /// the machine switched off: a guest that has already asked what it is
    /// running on, and configured itself accordingly, will not ask again.
    /// So the value is applied at RESET, which is where a real processor
    /// publishes its own identity too.
    ///
    /// A 386 is worth keeping available rather than merely simulating an
    /// older name: software of the period detects the processor by trying
    /// things and seeing what fails, and the only way to know that path
    /// still works is to have a processor on which it does fail.
    /// </summary>
    public CpuNs.CpuModel CpuModel
    {
        get => Cpu.State.Model;
        set
        {
            if (Cpu.State.Model == value) return;
            Cpu.State.SelectModel(value);
            Cpu.State.Reset();

            // Neither SelectModel nor CpuState.Reset touches APIC_BASE, so
            // switching models never raises Cpu.Msrs.ApicBaseChanged on its
            // own -- yet whether the local APIC is present at all just
            // changed. Re-applying the MSR's own current (unchanged) value
            // lets Apic notice a model that newly has, or just lost,
            // CpuFeature.Apic.
            Apic.OnApicBaseChanged(Cpu.Msrs.ApicBaseRaw);

            // The MP tables describe this specific model's signature,
            // feature flags and local APIC version (independent review,
            // item 6): a switch away from an Apic-capable model must make
            // them disappear from the ROM, and a switch onto one must make
            // them appear, exactly as if the machine had been built with
            // this model from the start.
            RefreshBiosMpTables();
        }
    }

    /// <summary>
    /// Which port the mouse is plugged into, or none at all.
    ///
    /// Both ports stay fitted whatever this says -- the 8042 keeps its
    /// auxiliary port and COM1 keeps its UART, because a guest that has
    /// already probed them must not have them vanish underneath it. What
    /// changes is which one the movements are delivered to, which is the
    /// same thing the user would achieve by moving the plug.
    /// </summary>
    public MouseInterface MouseInterface
    {
        get => Mouse.ConnectedInterface;
        set
        {
            Mouse.ConnectedInterface = value;
            PublishPointingDeviceInEquipment();
        }
    }

    /// <summary>
    /// Bit 2 of the equipment word says a pointing device hangs off the
    /// keyboard controller. It is the first thing MOUSE.DRV and MOUSE.COM
    /// look at: with it clear they never try INT 15h C2xx and go looking
    /// for a serial mouse instead, so it follows the port the mouse is on.
    /// </summary>
    private void PublishPointingDeviceInEquipment()
    {
        ushort equipment = Bda.EquipmentWord;
        equipment = Mouse.ConnectedInterface == MouseInterface.Ps2
            ? (ushort)(equipment | 0x0004)
            : (ushort)(equipment & ~0x0004);
        Bda.EquipmentWord = equipment;
    }

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

    /// <summary>
    /// Plugs the network card into a different wire. The one it had is
    /// closed if it owns anything -- a NAT holds host sockets -- so that
    /// switching a machine off the network really takes it off.
    ///
    /// Does nothing but close the offered backend when there is no network
    /// card fitted at all: there is nowhere to plug it in, and leaving the
    /// backend's own sockets open with nothing ever going to read them would
    /// be the actual leak.
    /// </summary>
    public void ConnectNetwork(INetworkBackend backend)
    {
        if (Network is null)
        {
            (backend as IDisposable)?.Dispose();
            return;
        }

        INetworkBackend previous = Network.Backend;
        if (ReferenceEquals(previous, backend))
        {
            return;
        }

        Network.Backend = backend;
        bool stillConnected = Cards.Any(card =>
            ReferenceEquals(GetNetworkDevice(card)?.Backend, previous));
        if (!stillConnected)
        {
            (previous as IDisposable)?.Dispose();
        }
    }

    /// <summary>Attaches a MIDI output and closes the previous host sink after detaching it.</summary>
    public void ConnectMidi(IMidiSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (Midi is null)
        {
            (sink as IDisposable)?.Dispose();
            return;
        }

        IMidiSink previous = Midi.Sink;
        if (ReferenceEquals(previous, sink))
        {
            return;
        }

        Midi.SetSink(sink);
        (previous as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        DisposeNetworkBackends(Cards);
        if (Midi is not null)
        {
            Midi.Sink.Reset();
            (Midi.Sink as IDisposable)?.Dispose();
        }

        foreach (DiskImage disk in _disks) disk.Dispose();
        CompactDisc?.Dispose();
        UsbStorage?.Dispose();
        Memory?.Dispose();
    }

    /// <summary>Preserves the legacy network service for either NE2000 bus attachment.</summary>
    private static Ne2000? GetNetworkDevice(IExpansionCard card)
    {
        return card switch
        {
            Ne2000Card isa => isa.Device,
            Rtl8029Card pci => pci.Device.Core,
            _ => null,
        };
    }

    /// <summary>Rejects overlapping sound and network windows before any card opens host resources.</summary>
    private static void ValidateExpansionWindows(
        IEnumerable<CardSettings> settings,
        IEnumerable<CardSettings> networkSettings)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (CardSettings card in settings)
        {
            if (string.Equals(card.Card, "sb16", StringComparison.OrdinalIgnoreCase))
            {
                AddRange(card.Port, 16); // DSP/mixer bank.
                AddRange(0x388, 4); // OPL3 address/data banks.
            }
            else if (string.Equals(card.Card, "mpu401-uart", StringComparison.OrdinalIgnoreCase))
            {
                AddRange(card.Port, 2); // MIDI data and status/command.
            }
        }

        // Network cards attach after sound cards. The ISA registrar would
        // otherwise overwrite an already fitted MPU window silently.
        foreach (CardSettings card in networkSettings)
        {
            int port = card.Port;
            bool isRtl8139 = string.Equals(card.Card, "rtl8139-pci", StringComparison.OrdinalIgnoreCase);
            if (port == 0 && string.Equals(card.Card, "rtl8029-pci", StringComparison.OrdinalIgnoreCase))
            {
                port = 0x300;
            }
            else if (port == 0 && isRtl8139)
            {
                port = 0xC200; // Rtl8139Card.DefaultPort: see that constant for why 300h will not do.
            }

            // The RTL8139's I/O BAR is 256 bytes, eight times the DP8390
            // core's own 32-byte window both the ISA NE2000 and the RTL8029
            // share.
            AddRange(port, isRtl8139 ? 256 : 32);
        }

        void AddRange(int start, int length)
        {
            int end = start + length;
            if (ranges.Any(range => start < range.End && range.Start < end))
            {
                throw new InvalidOperationException("Expansion cards require distinct initial I/O windows.");
            }

            ranges.Add((start, end));
        }
    }

    /// <summary>Closes every network wire once, including partially attached machines.</summary>
    private static void DisposeNetworkBackends(IEnumerable<IExpansionCard> cards)
    {
        var disposed = new HashSet<INetworkBackend>(ReferenceEqualityComparer.Instance);
        foreach (IExpansionCard card in cards)
        {
            // The RTL8139 is not NE2000-compatible, so it carries its own
            // backend rather than one GetNetworkDevice can reach; it is
            // still just a wire to close, exactly like every other card's.
            INetworkBackend? backend = card is Rtl8139Card rtl ? rtl.Backend : GetNetworkDevice(card)?.Backend;
            if (backend is IDisposable disposable && disposed.Add(backend))
            {
                disposable.Dispose();
            }
        }
    }
}
