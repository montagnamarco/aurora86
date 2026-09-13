using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Timing;
using Aurora386.Core.Devices.System;
using Aurora386.Core.Devices.Network;
using Aurora386.Core.Devices.IsaPnp;
using Aurora386.Core.Devices.Pci;
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
public sealed partial class Machine : IDisposable
{
    public PhysicalMemoryBus Memory { get; }
    public IoBus Io { get; }
    public PciBus Pci { get; }
    public PciBios PciServices { get; }
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
    public SoundBlaster16 SoundBlaster { get; }

    /// <summary>The NE2000 Ethernet card.</summary>
    public Ne2000 Network { get; }

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

    public Machine(uint ramSize = 16 * 1024 * 1024)
    {
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

        S3VideoIdentity videoIdentity = S3VideoIdentity.Trio64VPlus;
        VideoRegisters = new VgaRegisters(videoIdentity);
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

        // Both cards are Plug and Play as well as being at their usual
        // addresses: DOS finds them where it expects, and Windows 95 finds
        // them by asking the bus, and may move them.
        IsaPlugAndPlay = new IsaPnpBus();
        IsaPlugAndPlay.Add(IsaPnpCards.ForSoundBlaster16(SoundBlaster));
        IsaPlugAndPlay.Add(IsaPnpCards.ForNe2000(Network));
        IsaPlugAndPlay.Register(Io);

        Cpu = new CpuNs.Cpu(Memory, Io) { Interrupts = Interrupts };

        Bda = new BiosDataArea(Memory);
        VideoServices = new VideoServices(VideoMemory, VideoRegisters, Bda);
        VideoServices.Vesa = new VesaServices(this);
        DiskServices = new DiskServices(Bda);
        KeyboardServices = new KeyboardServices(Bda, Memory, Keyboard);
        CompactDiscServices = new CompactDiscServices(this);

        Ide = new IdeController(this, Interrupts);
        Ide.Register(Io);

        // The second cable, for a second disk. It carries no CD-ROM: that
        // is the slave of the first, where drivers of the period expect it.
        IdeSecondary = new IdeController(this, Interrupts, IdeController.Channel.Secondary);
        IdeSecondary.Register(Io);
        Pci = new PciBus();
        Pci.Add(new PciFunction(0x0000, 0x8086, 0x122D, 0x060000));
        var pciIsa = new PciFunction(0x0008, 0x8086, 0x122E, 0x060100, headerType: 0x80);
        Pci.Add(pciIsa);
        Pci.InterruptRouter = new PciInterruptRouter(Interrupts, pciIsa);
        // Compatibility PIO only: no native-mode BARs or bus-master capability.
        var pciIde = new PciFunction(0x0009, 0x8086, 0x1230, 0x010100, commandMask: 1);
        pciIde.Define(4, 1, 2, 1);
        // These reset defaults include BIOS setup, as does PCI command above.
        // IDETIM bit 15 enables each cable. Windows 95 reported code 29
        // with these registers zero despite the ports being decoded.
        // PIIX reserves bits 14 and 11:10; PIIX3 has a different bit-14 use.
        pciIde.Define(0x40, 0x8000, 2, 0xB3FF);
        pciIde.Define(0x42, 0x8000, 2, 0xB3FF);
        pciIde.ConfigurationChanged += () =>
        {
            bool ioEnabled = (pciIde.Command & 1) != 0;
            bool primaryEnabled = (pciIde.Read(0x40, 2) & 0x8000) != 0;
            bool secondaryEnabled = (pciIde.Read(0x42, 2) & 0x8000) != 0;
            Ide.SetIoDecoding(Io, ioEnabled && primaryEnabled);
            IdeSecondary.SetIoDecoding(Io, ioEnabled && secondaryEnabled);
        };
        Pci.Add(pciIde);

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
        var pciVideo = videoIdentity.CreatePciFunction(0x0020);

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
        const int maximumDosPathBytes = 64;
        const string commandProcessorName = "COMMAND.COM";

        Cpu.SoftwareInterruptIssued += vector =>
        {
            if (CommandProcessorStarted || vector != 0x21)
            {
                return;
            }
            if ((byte)(Cpu.State.Eax >> 8) != 0x4B)
            {
                return;
            }

            // The name is at DS:DX. Windows 95 launches C:\WINDOWS\COMMAND.COM,
            // so filling this tail buffer must not stop the path scan.
            uint at = Cpu.State.Ds.Base + Cpu.State.Dx;
            Span<char> name = stackalloc char[16];
            int length = 0;
            bool terminated = false;

            for (uint i = 0; i < maximumDosPathBytes; i++)
            {
                byte character = Memory.ReadByte(at + i);
                if (character == 0)
                {
                    terminated = true;
                    break;
                }

                // Only the tail is needed, so the window slides.
                if (length == name.Length)
                {
                    for (int j = 1; j < length; j++)
                    {
                        name[j - 1] = name[j];
                    }
                    length--;
                }
                name[length++] = char.ToUpperInvariant((char)character);
            }

            // A suffix at the read bound is not a complete DOS ASCIIZ path.
            if (terminated && length >= commandProcessorName.Length &&
                name[..length].EndsWith(commandProcessorName))
            {
                CommandProcessorStarted = true;
            }
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
    /// </summary>
    public void ConnectNetwork(INetworkBackend backend)
    {
        INetworkBackend previous = Network.Backend;
        if (ReferenceEquals(previous, backend)) return;
        Network.Backend = backend;
        (previous as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        (Network.Backend as IDisposable)?.Dispose();
        foreach (DiskImage disk in _disks) disk.Dispose();
        CompactDisc?.Dispose();
        UsbStorage?.Dispose();
        Memory.Dispose();
    }
}
