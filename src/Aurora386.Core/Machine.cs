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

    public Machine(uint ramSize = 16 * 1024 * 1024)
    {
        // The addressable space is larger than the RAM, because the linear
        // frame buffer lives above it. A real card put it above the 16 MB
        // line -- below that is the ISA hole and whatever memory the machine
        // has, and a card cannot know how much of either -- so here it goes
        // directly above the installed RAM, which is the same reasoning
        // applied to a machine whose memory size is known.
        uint frameBufferBase = Math.Max(ramSize, 16u * 1024 * 1024);
        // Lo spazio indirizzabile comprende il frame buffer e i 64 KB di
        // registri mappati subito sopra.
        Memory = new PhysicalMemoryBus(ramSize, frameBufferBase + VgaMemory.TotalSize + 0x10000);
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

        // The engine's registers again, mapped as memory directly above the
        // frame buffer. A protected-mode driver writes there rather than to
        // the I/O ports: an OUT from protected-mode code is a fault the
        // monitor has to catch and emulate, and a memory write is not -- on a
        // 486 that is a few hundred cycles per register against a few.
        MmioBase = LinearFrameBufferBase + VgaMemory.TotalSize;
        Memory.MapDevice(MmioBase, 0x10000, Accelerator.Mmio);
        VideoRegisters.MmioBase = MmioBase;

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
        Memory.Dispose();
    }
}
