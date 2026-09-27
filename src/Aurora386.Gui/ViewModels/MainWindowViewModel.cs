using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Network;
using Aurora386.Core.Devices.Network.Nat;
using Aurora386.Core.Devices.Interrupts;
using Aurora386.Core.Devices.Timing;
using Aurora386.Core.Devices.System;
using Aurora386.Core.Cpu;
using Aurora386.Core.Dos;
using Aurora386.Core.Host.Midi;
using Aurora386.Gui.Emulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aurora386.Gui.ViewModels;

/// <summary>
/// Drives the main window: the toolbar commands, the status bar indicators
/// and the list of mounted media.
///
/// The emulator runs on its own thread, so everything here follows one rule:
/// state is read from the machine only inside <c>WithMachine</c>, and pushed
/// to the interface through observable properties that Avalonia binds to.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _statusTimer;

    /// <summary>
    /// Access counters as of the last status refresh. Comparing against the
    /// current values tells us whether a drive did anything in the last tick,
    /// which is what makes the lights blink instead of staying lit.
    /// </summary>
    private readonly Dictionary<byte, long> _lastAccessCounts = new();
    private readonly Dictionary<byte, long> _accessCounts = new();

    public EmulatorSession Session { get; }

    /// <summary>One entry per attached drive, shown as a light in the status bar.</summary>
    public ObservableCollection<DriveIndicator> Drives { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _powerLedOn;
    [ObservableProperty] private bool _networkLedOn;
    [ObservableProperty] private bool _turbo;

    /// <summary>
    /// Whether the guest currently owns the host pointer. Bound to a notice
    /// in the status bar, which is the only thing that tells the user what
    /// happened: the cursor has simply disappeared, and without a message
    /// saying which key brings it back the only way out is to guess.
    /// </summary>
    [ObservableProperty] private bool _mouseCaptured;

    /// <summary>What that notice says. See <see cref="ReportMouseCapture"/>.</summary>
    [ObservableProperty] private string _mouseCaptureText = "";

    /// <summary>Frame counts as of the last refresh, to detect activity.</summary>
    private long _lastFramesSent, _lastFramesReceived;

    /// <summary>
    /// Set by the view to open a file picker. The view model has no business
    /// knowing about windows, so it asks through this callback and stays
    /// testable.
    /// </summary>
    public Func<string, IReadOnlyList<string>, Task<string?>>? PickFileAsync { get; set; }

    /// <summary>
    /// Asks the user where to put a file being created, rather than which
    /// existing one to open. A separate callback because it is a different
    /// dialogue with different rules: it may name a file that does not exist,
    /// and it is the place that warns about overwriting one that does.
    /// </summary>
    public Func<string, string, Task<string?>>? PickSaveAsync { get; set; }

    /// <summary>
    /// Asks the user for a folder. Supplied by the window, which is the only
    /// part that knows how to put a picker on the screen.
    /// </summary>
    public Func<string, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>Set by the view to save a screenshot of the current screen.</summary>
    public Func<string, Task>? SaveScreenshotAsync { get; set; }

    /// <summary>Set by the view to open the debugger window.</summary>
    public Action<DebuggerViewModel>? ShowDebugger { get; set; }

    /// <summary>Carries the sound card's output to the host's speakers.</summary>
    private readonly AudioOutput _audio;

    public MainWindowViewModel()
    {
        Session = new EmulatorSession();
        HookMachineEvents();

        _audio = new AudioOutput(Session);
        _audio.Start();

        // The machine that was open last, if it is still where it was; the
        // default disk otherwise.
        if (!TryReopenLastVm())
        {
            AttachDefaultHardDisk();
        }

        // Four refreshes a second is enough for a human to read, and cheap
        // enough that it never competes with the emulation thread.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
    }

    /// <summary>
    /// Looks for a hard disk image next to the emulator and attaches it, so
    /// the machine has something to boot from the moment it starts.
    ///
    /// Requiring the user to mount a disk before every single run is friction
    /// with no purpose: the overwhelmingly common case is wanting the same
    /// disk as last time. A missing file is not an error -- it just means
    /// there is nothing to attach yet.
    /// </summary>
    private void AttachDefaultHardDisk()
    {
        foreach (string candidate in DefaultHardDiskPaths())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                AttachImage(candidate, 0x80, readOnly: false);
                RefreshDriveList();
                StatusText = $"Attached {Path.GetFileName(candidate)}";
                return;
            }
            catch (Exception)
            {
                // An image that cannot be opened -- locked by another
                // emulator, or truncated -- should not stop the window from
                // appearing. The user can mount a different one.
            }
        }
    }

    /// <summary>
    /// Where to look for a hard disk, in order of preference: the repository's
    /// own images directory first, then the working directory.
    /// </summary>
    private static IEnumerable<string> DefaultHardDiskPaths()
    {
        yield return Path.Combine("images", "hdd.img");
        yield return "hdd.img";

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "Aurora386", "hdd.img");
    }

    private void HookMachineEvents()
    {
        Session.WithMachine(machine =>
        {
            machine.DiskServices.DriveAccessed += OnDriveAccessed;

            // The guest decides the CD-ROM's letter, and says so only once
            // its driver has loaded; the indicator follows.
            machine.CompactDiscLetterChanged += _ => Dispatcher.UIThread.Post(RefreshDriveList);

            // APM power-off: the guest has shut down and asked for the power
            // to go. The machine stops where it is, like a PC whose power
            // supply obeyed; Start turns it back on from cold.
            machine.PowerOffRequested += () => Dispatcher.UIThread.Post(() =>
            {
                Session.Stop();
                PowerLedOn = false;
                StatusText = "The guest turned the power off";
            });

            // AURORA_DOSLOG records the DOS calls that move files about --
            // rename, create, delete, exec -- which is what says how far an
            // installer got. Unlike the write log it needs no disk, so it is
            // hooked with the machine itself.
            string? dosLog = Environment.GetEnvironmentVariable("AURORA_DOSLOG");
            if (_dosLog is null && !string.IsNullOrWhiteSpace(dosLog))
                _dosLog = DosCallLog.Attach(machine, dosLog);

        });
    }

    /// <summary>
    /// Called on the emulation thread for every sector transfer. Keep it to a
    /// counter bump: anything heavier would slow the guest down.
    /// </summary>
    private DiskWriteLog? _writeLog;
    private DosCallLog? _dosLog;

    private void OnDriveAccessed(byte driveNumber, bool write)
    {
        lock (_accessCounts)
        {
            _accessCounts.TryGetValue(driveNumber, out long count);
            _accessCounts[driveNumber] = count + 1;
        }
    }

    private void RefreshStatus()
    {
        IsRunning = Session.IsRunning;
        IsPaused = Session.IsPaused;
        PowerLedOn = Session.IsRunning || Session.IsPaused;

        double ips = Session.InstructionsPerSecond;
        SpeedText = ips <= 0 ? "—"
                  : ips >= 1_000_000 ? $"{ips / 1_000_000:F1}M inst/s"
                  : $"{ips / 1000:F0}K inst/s";

        RefreshDriveLights();
        RefreshNetworkLight();
        RefreshMidiStatus();
    }

    /// <summary>Surfaces asynchronous endpoint errors that cannot be thrown on the guest thread.</summary>
    private void RefreshMidiStatus()
    {
        string? diagnostic = Session.WithMachine(machine =>
        {
            if (machine.Midi?.Sink is not WinMmMidiSink output)
            {
                return null;
            }

            if (output.LastError is string error)
            {
                return $"MIDI: {error}";
            }

            ulong dropped = output.DroppedSysExCount;
            return dropped == 0 ? null : $"MIDI: {dropped} oversized SysEx messages discarded";
        });

        if (diagnostic is not null)
        {
            StatusText = diagnostic;
        }
    }

    /// <summary>
    /// Lights the network indicator when frames moved since the last tick.
    /// Reading the counters is cheap enough to do from the timer, and the
    /// card is only ever written by the emulation thread in whole updates.
    /// </summary>
    private void RefreshNetworkLight()
    {
        (long sent, long received) = Session.WithMachine(
            machine => machine.Network is { } network ? (network.FramesSent, network.FramesReceived) : (0L, 0L));

        NetworkLedOn = sent != _lastFramesSent || received != _lastFramesReceived;
        _lastFramesSent = sent;
        _lastFramesReceived = received;
    }

    private void RefreshDriveLights()
    {
        foreach (DriveIndicator drive in Drives)
        {
            long now;
            lock (_accessCounts)
                _accessCounts.TryGetValue(drive.DriveNumber, out now);

            _lastAccessCounts.TryGetValue(drive.DriveNumber, out long before);
            drive.IsActive = now != before;
            _lastAccessCounts[drive.DriveNumber] = now;
        }
    }

    /// <summary>Rebuilds the drive list from what is actually attached.</summary>
    private void RefreshDriveList()
    {
        List<DiskImage> attached = Session.WithMachine(
            machine => machine.DiskServices.AttachedDrives.ToList());

        (IsoImage? disc, byte? letter) = Session.WithMachine(machine => (machine.CompactDisc, machine.CompactDiscLetter));

        Drives.Clear();
        foreach (DiskImage image in attached)
            Drives.Add(new DriveIndicator(image));

        if (disc is not null)
        {
            Drives.Add(new DriveIndicator(disc, letter));
        }

        // Shared folders, by the letter the guest reaches them as.
        foreach ((byte drive, string folder) in Session.WithMachine(machine =>
            machine.Redirector.MountedDrives
                .Select(d => (d, machine.Redirector.GetMounted(d) as HostDirectoryFileSystem))
                .Where(pair => pair.Item2 is not null)
                .Select(pair => (pair.d, pair.Item2!.RootPath))
                .ToList()))
            Drives.Add(new DriveIndicator(drive, folder));
    }

    // --- the mouse ----------------------------------------------------------

    /// <summary>
    /// Which port the mouse is plugged into.
    ///
    /// This is a property rather than three commands because the three
    /// choices are exclusive and the interface shows them as such -- a menu
    /// with a tick beside the current one. Setting it reaches the running
    /// machine straight away, which is the point: finding out which port a
    /// given program expects means trying one and then the other, and having
    /// to reboot DOS between attempts turns a five-second experiment into a
    /// two-minute one.
    /// </summary>
    public MouseInterface MouseInterface
    {
        get => _mouseInterface;
        set
        {
            // Pushed to the machine even when unchanged: the machine may have
            // been rebuilt (cold reset, new session) with its own default.
            Session.WithMachine(machine => machine.MouseInterface = value);
            if (!SetProperty(ref _mouseInterface, value))
            {
                return;
            }

            // The three "is this the one" properties the menu ticks are
            // derived from this one, so they have to be announced by hand.
            OnPropertyChanged(nameof(MouseIsPs2));
            OnPropertyChanged(nameof(MouseIsSerial));
            OnPropertyChanged(nameof(MouseIsDisconnected));

            StatusText = value switch
            {
                MouseInterface.Ps2 => "Mouse on the PS/2 port",
                MouseInterface.Serial => "Mouse on COM1, as a Microsoft serial mouse",
                _ => "Mouse unplugged",
            };
        }
    }

    /// <summary>
    /// Whether the network card is on a NAT network or on nothing at all.
    /// Like the mouse port this goes to the machine at once: a driver that
    /// is already loaded sees the wire appear, the way a cable plugged in
    /// after boot would.
    /// </summary>
    public bool NetworkIsNat
    {
        get => _networkIsNat;
        set
        {
            Session.WithMachine(machine => ApplyNetwork(machine, value));
            if (!SetProperty(ref _networkIsNat, value))
            {
                return;
            }
            OnPropertyChanged(nameof(NetworkIsOff));
            StatusText = value
                ? "Network: NAT through this computer (guest 10.0.2.15, gateway 10.0.2.2)"
                : "Network unplugged";
        }
    }

    public bool NetworkIsOff
    {
        get => !NetworkIsNat;
        set { if (value) NetworkIsNat = false; }
    }

    private bool _networkIsNat;

    /// <summary>
    /// Whether the file's network card, if it has one, is configured for
    /// NAT. Reads <see cref="VmConfiguration.HardwareSettings.Network"/>,
    /// the source of truth from version 2 on -- see its own summary and
    /// <see cref="VmConfiguration.Network"/>'s.
    /// </summary>
    private static bool IsConfiguredForNat(VmConfiguration configuration)
        => configuration.Hardware.Network is [{ } card, ..]
        && card.Mode == VmConfiguration.NetworkMode.Nat;

    /// <summary>
    /// Plugs the card into a NAT or unplugs it, leaving it alone if it is
    /// already where it should be: a NAT holds host ports, and building a
    /// second one while the first is still listening would find them taken.
    /// Does nothing at all when the machine has no network card fitted.
    /// </summary>
    private void ApplyNetwork(Aurora386.Core.Machine machine, bool nat)
    {
        if (machine.Network is null)
        {
            return;
        }
        if (machine.Network.Backend is NatBackend == nat)
        {
            return;
        }

        List<VmConfiguration.PortForward> forwards = _vmConfiguration?.Hardware.Network is { Count: > 0 } cards
            ? cards[0].Forwards
            : [];
        machine.ConnectNetwork(nat ? VmConfiguration.BuildNat(forwards) : new LoopbackBackend());
    }

    // COM1 by default: it is what MOUSE.DRV and every DOS program of the
    // period expect to find. The PS/2 port works too -- the BIOS provides
    // INT 15h C2xx and the 8042 raises IRQ 12 -- but a guest installed while
    // the mouse was on COM1 has that choice written into its own
    // configuration, and moving the plug does not rewrite it.
    private MouseInterface _mouseInterface = MouseInterface.Serial;

    // A menu item binds its tick to a boolean, so each choice needs one. They
    // are settable because a RadioButton-style MenuItem writes back when it
    // is chosen, and writing false -- which happens as the previous choice is
    // cleared -- must not turn the mouse off.
    public bool MouseIsPs2
    {
        get => MouseInterface == MouseInterface.Ps2;
        set { if (value) MouseInterface = MouseInterface.Ps2; }
    }

    public bool MouseIsSerial
    {
        get => MouseInterface == MouseInterface.Serial;
        set { if (value) MouseInterface = MouseInterface.Serial; }
    }

    public bool MouseIsDisconnected
    {
        get => MouseInterface == MouseInterface.None;
        set { if (value) MouseInterface = MouseInterface.None; }
    }

    /// <summary>
    /// Which processor the machine is fitted with.
    ///
    /// Unlike the mouse this cannot usefully be changed while the guest is
    /// running: a program asks what it is running on once, at startup, and
    /// then believes the answer for as long as it lives. So the choice takes
    /// effect at the next reset, and the status line says so rather than
    /// letting the user think nothing happened.
    ///
    /// The 386 is kept as a real option and not merely a label. Software of
    /// the period identifies the processor by trying things and seeing what
    /// fails -- writing a CR0 bit that should not stick, executing an opcode
    /// that should fault -- and the only honest way to know those paths still
    /// work is to have a processor on which they do.
    /// </summary>
    public CpuModel CpuModel
    {
        get => _cpuModel;
        set
        {
            Session.WithMachine(machine => machine.CpuModel = value);
            if (!SetProperty(ref _cpuModel, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CpuIs486));
            OnPropertyChanged(nameof(CpuIs386));

            StatusText = value switch
            {
                CpuModel.Intel486 => "486DX at the next reset",
                _ => "386DX at the next reset",
            };
        }
    }

    // A 486DX: it is the floor for Windows 95, it carries its own floating
    // point unit, and nothing that ran on a 386 stopped running on one.
    private CpuModel _cpuModel = CpuModel.Intel486;

    public bool CpuIs486
    {
        get => CpuModel == CpuModel.Intel486;
        set { if (value) CpuModel = CpuModel.Intel486; }
    }

    public bool CpuIs386
    {
        get => CpuModel == CpuModel.Intel386;
        set { if (value) CpuModel = CpuModel.Intel386; }
    }

    /// <summary>
    /// Called by the window when the guest takes or gives back the pointer.
    ///
    /// The wording has to distinguish two genuinely different situations,
    /// because the user can tell them apart in a second and would otherwise
    /// think the emulator was broken. Where the host can unhook its pointer
    /// the guest gets unlimited movement; where it cannot, the pointer is
    /// still bounded by the window and saying nothing about it would leave
    /// the user pushing at an edge and wondering why.
    /// </summary>
    public void ReportMouseCapture(bool captured, bool unbounded)
    {
        MouseCaptured = captured;

        if (!captured)
        {
            MouseCaptureText = "";
            StatusText = "Pointer released";
            return;
        }

        MouseCaptureText = unbounded
            ? $"Mouse captured — press {Emulation.MouseCapture.ReleaseKeyName} to release"
            : $"Mouse captured (bounded by the window) — press {Emulation.MouseCapture.ReleaseKeyName} to release";
    }

    // --- toolbar commands ---------------------------------------------------

    [RelayCommand]
    private void Start()
    {
        Session.Start();
        StatusText = "Running";
    }

    /// <summary>
    /// Lets the guest run as fast as the host allows, or holds it to the
    /// speed of the machine it is imitating.
    /// </summary>
    [RelayCommand]
    private void ToggleTurbo()
    {
        Turbo = !Turbo;
        Session.Unthrottled = Turbo;
        StatusText = Turbo
            ? "Turbo: running as fast as the host allows"
            : "Running at 386 speed";
    }

    [RelayCommand]
    private void TogglePause()
    {
        Session.TogglePause();
        StatusText = Session.IsPaused ? "Paused" : "Running";
    }

    /// <summary>
    /// A cold reset: the machine is rebuilt from scratch and the media are
    /// re-inserted, which is what pressing the power switch would do. A warm
    /// reset (Ctrl-Alt-Del) is a different thing and belongs to the guest.
    /// </summary>
    [RelayCommand]
    private void Reset()
    {
        List<(string Path, byte Drive, bool ReadOnly)> media = Session.WithMachine(
            machine => machine.DiskServices.AttachedDrives
                .Where(image => image.SourcePath is not null)
                .Select(image => (image.SourcePath!, image.DriveNumber, image.ReadOnly))
                .ToList());

        bool wasRunning = Session.IsRunning;
        Session.Stop();
        Session.Reset();
        HookMachineEvents();

        // A reset builds a brand-new machine, which comes up with the default
        // mouse port. The user's choice belongs to the emulator's settings
        // rather than to the machine, so it is reapplied -- otherwise the
        // serial mouse someone chose because their program needed it quietly
        // becomes a PS/2 one the moment they press Reset.
        Session.WithMachine(machine =>
        {
            machine.MouseInterface = MouseInterface;
            ApplyNetwork(machine, NetworkIsNat);
        });

        foreach ((string path, byte drive, bool readOnly) in media)
            AttachImage(path, drive, readOnly);

        RefreshDriveList();
        if (wasRunning)
        {
            Session.Start();
        }
        StatusText = "Reset";
    }

    [RelayCommand]
    private void StepInstruction()
    {
        Session.Pause();
        Session.StepOne();
        StatusText = "Stepped one instruction";
    }

    /// <summary>
    /// Opens the debugger, pausing the machine first: inspecting registers
    /// that are changing millions of times a second would be useless.
    /// </summary>
    [RelayCommand]
    private void OpenDebugger()
    {
        if (ShowDebugger is null)
        {
            return;
        }

        Session.Pause();
        ShowDebugger(new DebuggerViewModel(Session));
        StatusText = "Debugger open (machine paused)";
    }

    /// <summary>Opens the window that shows what is in video memory.</summary>
    public Action<VideoInspectorViewModel>? ShowVideoInspector { get; set; }

    [RelayCommand]
    private void OpenVideoInspector()
    {
        if (ShowVideoInspector is null)
        {
            return;
        }

        // Paused, like the debugger: reading two megabytes of video memory
        // while the guest is writing it would show a picture that is half of
        // one frame and half of another, which is worse than useless when the
        // question is what the guest drew.
        Session.Pause();
        ShowVideoInspector(new VideoInspectorViewModel(Session));
        StatusText = "Video memory open (machine paused)";
    }

    [RelayCommand]
    private async Task Screenshot()
    {
        if (SaveScreenshotAsync is null)
        {
            return;
        }

        string name = $"aurora386-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), name);

        await SaveScreenshotAsync(path);
        StatusText = $"Screenshot saved to {name}";
    }

    // --- media commands -----------------------------------------------------

    [RelayCommand]
    private Task MountFloppyA() => MountAsync(0x00, "Select a floppy image", FloppyFilters);

    [RelayCommand]
    private Task MountFloppyB() => MountAsync(0x01, "Select a floppy image", FloppyFilters);

    [RelayCommand]
    private Task MountHardDisk() => MountAsync(NextHardDiskNumber(), "Select a hard disk image", HardDiskFilters);

    /// <summary>
    /// Mounts an ISO image as the CD-ROM drive.
    ///
    /// A CD is not a disk as far as the BIOS is concerned: its sectors are
    /// 2048 bytes and it is addressed by block number, so DOS reaches it
    /// through MSCDEX and a device driver rather than through INT 13h. The
    /// image is therefore held separately from the disk drives.
    /// </summary>
    [RelayCommand]
    private async Task MountUsbStorage(string? mode)
    {
        if (PickFileAsync is null)
        {
            return;
        }
        string? path = await PickFileAsync("Select a USB disk image", HardDiskFilters);
        if (path is null)
        {
            return;
        }
        try
        {
            bool wasEnabled = Session.WithMachine(machine => machine.Usb is not null);
            Session.WithMachine(machine => machine.AttachUsbStorage(
                Aurora386.Core.Devices.Usb.UsbMassStorageDevice.Open(path, mode == "readonly")));
            StatusText = $"USB: {Path.GetFileName(path)}" + (wasEnabled ? " inserted" : " inserted; restart the guest to detect the USB controllers");
        }
        catch (Exception ex) { StatusText = $"Could not insert USB disk: {ex.Message}"; }
    }

    [RelayCommand]
    private void EjectUsbStorage()
    {
        Session.WithMachine(machine => machine.EjectUsbStorage());
        StatusText = "USB disk removed";
    }

    [RelayCommand]
    private async Task MountCompactDisc()
    {
        if (PickFileAsync is null)
        {
            return;
        }

        string? path = await PickFileAsync("Select a CD-ROM image", CdFilters);
        if (path is null)
        {
            return;
        }

        try
        {
            Session.WithMachine(machine => machine.AttachCompactDisc(IsoImage.Open(path)));
            RefreshDriveList();
            StatusText = $"Inserted {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not read the disc: {ex.Message}";
        }
    }

    /// <summary>
    /// Shares a folder of the host with the guest, as a drive letter.
    ///
    /// This is not a disk image: there is no file system to format and
    /// nothing is copied. DOS reaches it the way it reached a file server, by
    /// asking a resident program to answer for that drive -- which is what
    /// the redirector does. Files put in the folder appear in the guest at
    /// once, and files the guest writes appear on the host.
    /// </summary>
    [RelayCommand]
    private async Task ShareFolder()
    {
        if (PickFolderAsync is null)
        {
            return;
        }

        string? path = await PickFolderAsync("Choose a folder to share with the guest");
        if (path is null)
        {
            return;
        }

        try
        {
            (bool letterExists, byte drive) = Session.WithMachine(machine =>
            {
                byte next = machine.NextSharedFolderDrive();
                return (machine.Redirector.Mount(next, new HostDirectoryFileSystem(path)), next);
            });

            RefreshDriveList();

            char letter = (char)('A' + drive);

            // DOS decides how many drive letters exist when it starts, from
            // LASTDRIVE in CONFIG.SYS, and defaults to barely more than the
            // drives it found. Mounting past that limit fails in the most
            // unhelpful way possible: the letter reports "invalid drive" and
            // nothing anywhere says why, so say it here.
            StatusText = letterExists
                ? $"{Path.GetFileName(path)} is now drive {letter}:"
                : $"Shared, but DOS has no drive {letter}: -- add " +
                  $"LASTDRIVE={letter} to CONFIG.SYS and restart the guest";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not share that folder: {ex.Message}";
        }
    }

    /// <summary>
    /// What the title bar says: the emulator, and the machine when one is
    /// open. Two windows with two machines in them are told apart by this
    /// and by nothing else.
    /// </summary>
    [ObservableProperty] private string _windowTitle = "Aurora386";

    /// <summary>The folder of the machine currently open, when it came from one.</summary>
    private string? _vmFolder;
    private VmConfiguration? _vmConfiguration;

    /// <summary>
    /// Opens a machine from its folder: stops the one running, has the
    /// session build machines from the file from now on, and starts again.
    ///
    /// Deliberately not Reset. Reset re-attaches whatever the old machine
    /// had, which is right when the same machine is being restarted and
    /// exactly wrong here: the disks of the machine being left would land
    /// on top of the disks of the one being opened.
    /// </summary>
    [RelayCommand]
    private async Task OpenVm()
    {
        if (PickFolderAsync is null)
        {
            return;
        }

        string? folder = await PickFolderAsync("Open a machine folder (one with vm.json in it)");
        if (folder is null)
        {
            return;
        }

        VmConfiguration configuration;
        try { configuration = VmConfiguration.Load(folder); }
        catch (Exception ex) { StatusText = $"Could not open that machine: {ex.Message}"; return; }

        Session.Stop();
        Session.Factory = configuration.BuildMachine;
        try
        {
            Session.Reset();
        }
        catch (Exception ex)
        {
            // A disk that is not there any more, most likely. Fall back to a
            // plain machine rather than leave the session holding nothing.
            Session.Factory = () => new Aurora386.Core.Machine();
            Session.Reset();
            HookMachineEvents();
            RefreshDriveList();
            StatusText = $"Could not build that machine: {ex.Message}";
            return;
        }
        HookMachineEvents();

        _vmFolder = folder;
        _vmConfiguration = configuration;

        // The settings the window shows follow the file, not the other way.
        CpuModel = configuration.Cpu;
        MouseInterface = configuration.Mouse;
        NetworkIsNat = IsConfiguredForNat(configuration);

        RefreshDriveList();
        Session.Start();
        RememberVm(folder);
        WindowTitle = $"Aurora386 \u2014 {configuration.Name}";
        StatusText = $"Opened {configuration.Name}";
    }

    /// <summary>
    /// Writes the machine as it stands to its folder, or to one chosen now
    /// if it has none: every drive, the mouse port, the processor, the boot
    /// order. The memory size stays what the file had, because the window
    /// has no control for it yet and inventing a number would be worse than
    /// keeping the one that was true.
    /// </summary>
    [RelayCommand]
    private async Task SaveVm()
    {
        string? folder = _vmFolder;
        if (folder is null)
        {
            if (PickFolderAsync is null)
            {
                return;
            }
            folder = await PickFolderAsync("Choose a folder to keep this machine in");
            if (folder is null)
            {
                return;
            }
        }

        VmConfiguration configuration = _vmConfiguration ?? new VmConfiguration
        {
            Name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)),
        };

        Session.WithMachine(machine =>
        {
            configuration.Cpu = machine.CpuModel;
            configuration.UsbEnabled = machine.Usb is not null;
            configuration.UsbStorage = machine.UsbStorage?.SourcePath is { } usbPath
                ? new VmConfiguration.UsbDiskEntry { Path = usbPath, ReadOnly = machine.UsbStorage.ReadOnly }
                : null;
            configuration.Mouse = machine.MouseInterface;
            configuration.BootOrder = machine.BootDrives.ToList();

            // Hardware.Network is the source of truth from version 2 on; a
            // machine built with no network card at all has nothing here to
            // update.
            if (machine.Network is { } network && configuration.Hardware.Network.Count > 0)
            {
                configuration.Hardware.Network[0].Mode = network.Backend is NatBackend
                    ? VmConfiguration.NetworkMode.Nat
                    : VmConfiguration.NetworkMode.None;
            }

            // Hardware.Usb is the source of truth from version 2 on, the same
            // as Hardware.Network above; UsbEnabled and UsbStorage are kept
            // only for a version-1 reader.
            configuration.Hardware.Usb = machine.Usb is not null
                ? new VmConfiguration.UsbSettings { Storage = configuration.UsbStorage }
                : null;

            configuration.Disks = machine.DiskServices.AttachedDrives
                .Where(d => d.DriveNumber >= 0x80 && d.SourcePath is not null)
                .Select(d => new VmConfiguration.DiskEntry
                    { Path = d.SourcePath!, Drive = d.DriveNumber, ReadOnly = d.ReadOnly })
                .ToList();
            configuration.Floppies = machine.DiskServices.AttachedDrives
                .Where(d => d.DriveNumber < 0x80 && d.SourcePath is not null)
                .OrderBy(d => d.DriveNumber)
                .Select(d => d.SourcePath!)
                .ToList();
            configuration.CompactDisc = machine.CompactDisc?.SourcePath;
            configuration.SharedFolders = machine.Redirector.MountedDrives
                .Select(drive => (Drive: drive, Folder: machine.Redirector.GetMounted(drive) as HostDirectoryFileSystem))
                .Where(pair => pair.Folder is not null)
                .Select(pair => new VmConfiguration.SharedFolder
                    { Path = pair.Folder!.RootPath, Drive = pair.Drive, ReadOnly = pair.Folder.IsReadOnly })
                .ToList();
        });

        try { configuration.Save(folder); }
        catch (Exception ex) { StatusText = $"Could not save the machine: {ex.Message}"; return; }

        _vmFolder = folder;
        _vmConfiguration = configuration;
        RememberVm(folder);
        WindowTitle = $"Aurora386 \u2014 {configuration.Name}";
        StatusText = $"Saved {configuration.Name} to {Path.GetFileName(folder)}";
    }

    /// <summary>
    /// Saves the machine's whole state -- memory, processor, devices, screen
    /// -- to a file: in the machine's own folder, under snapshot/, when it
    /// has one, and wherever is chosen otherwise.
    ///
    /// The point is the ten minutes a boot into Windows costs. A state saved
    /// at the desktop comes back in a fraction of a second, and an
    /// experiment that needs the same starting point twenty times gets it
    /// twenty times for the price of one boot.
    /// </summary>
    [RelayCommand]
    private async Task SaveState()
    {
        string? path;
        if (_vmFolder is not null)
        {
            string folder = Path.Combine(_vmFolder, "snapshot");
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}.state");
        }
        else
        {
            if (PickSaveAsync is null)
            {
                return;
            }
            path = await PickSaveAsync("Save the machine's state", "machine.state");
            if (path is null)
            {
                return;
            }
        }

        // Paused, not stopped: the emulation thread keeps its place and the
        // capture sees a machine between two instructions, not one halfway
        // through a batch.
        bool wasRunning = Session.IsRunning;
        Session.Pause();
        try
        {
            Session.WithMachine(machine => Aurora386.Core.MachineSnapshot.Capture(machine).Save(path));
            StatusText = $"State saved to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save the state: {ex.Message}";
        }
        finally
        {
            if (wasRunning)
            {
                Session.Resume();
            }
        }
    }

    /// <summary>
    /// Puts a saved state back into the machine. The disks are whatever is
    /// attached now, not what was attached then: a state carries memory and
    /// devices, and expects to find the same disks it left.
    /// </summary>
    [RelayCommand]
    private async Task RestoreState()
    {
        if (PickFileAsync is null)
        {
            return;
        }

        string? path = await PickFileAsync("Restore a saved state", ["state"]);
        if (path is null)
        {
            return;
        }

        bool wasRunning = Session.IsRunning;
        Session.Pause();
        try
        {
            Session.WithMachine(machine => Aurora386.Core.MachineSnapshot.Load(path).RestoreTo(machine));
            RefreshDriveList();
            StatusText = $"State restored from {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not restore the state: {ex.Message}";
        }
        finally
        {
            if (wasRunning)
            {
                Session.Resume();
            }
        }
    }

    private static void RememberVm(string folder)
    {
        UserSettings settings = UserSettings.Load();
        settings.LastVmFolder = folder;
        settings.Save();
    }

    /// <summary>
    /// Opens the machine that was open when the emulator last closed.
    ///
    /// Only when its folder is still there with a vm.json in it, and only if
    /// it builds: a machine whose disk has since been deleted is not a reason
    /// for the window not to appear, so any failure here means "no", and the
    /// default disk is attached instead.
    /// </summary>
    private bool TryReopenLastVm()
    {
        string? folder = UserSettings.Load().LastVmFolder;
        if (folder is null || !File.Exists(Path.Combine(folder, VmConfiguration.FileName)))
            return false;

        try
        {
            VmConfiguration configuration = VmConfiguration.Load(folder);
            Session.Factory = configuration.BuildMachine;
            Session.Reset();
            HookMachineEvents();

            _vmFolder = folder;
            _vmConfiguration = configuration;
            CpuModel = configuration.Cpu;
            MouseInterface = configuration.Mouse;
            NetworkIsNat = IsConfiguredForNat(configuration);
            RefreshDriveList();
            WindowTitle = $"Aurora386 \u2014 {configuration.Name}";
            StatusText = $"Opened {configuration.Name}";
            return true;
        }
        catch (Exception)
        {
            Session.Factory = () => new Aurora386.Core.Machine();
            Session.Reset();
            HookMachineEvents();
            return false;
        }
    }

    /// <summary>
    /// Makes a new hard disk image and attaches it.
    ///
    /// Formatted rather than blank, because blank is the answer to a
    /// different question. A blank image is what somebody wants who is about
    /// to run FDISK to learn how partitioning works; a formatted one is what
    /// somebody wants who needs a second drive, and that is almost always the
    /// question being asked. FORMAT is still there for anyone who wants to do
    /// it themselves.
    /// </summary>
    [RelayCommand]
    private async Task CreateDisk()
    {
        if (PickSaveAsync is null)
        {
            return;
        }

        string? path = await PickSaveAsync("Create a hard disk image", "disk.img");
        if (path is null)
        {
            return;
        }

        try
        {
            const long size = 512L * 1024 * 1024;
            DiskImageBuilder.CreateFat16(path, size, "AURORA");

            byte drive = NextHardDiskNumber();
            AttachImage(path, drive, readOnly: false);
            RefreshDriveList();

            // The letter is not ours to decide: DOS assigns it, counting the
            // partitions it finds. Saying which disk it is avoids promising a
            // letter that turns out to be somebody else's.
            StatusText = $"{Path.GetFileName(path)} created and attached as disk {drive:X2}h "
                       + "-- restart the guest for DOS to give it a letter";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not create that image: {ex.Message}";
        }
    }

    /// <summary>
    /// The letter the shared folder gets. E: keeps clear of the floppies, the
    /// hard disk and the CD-ROM, which between them can reach D:.
    /// </summary>

    /// <summary>
    /// Picks the next free hard disk number. The BIOS numbers hard disks from
    /// 0x80 upwards, so a second disk is 0x81 and so on; mounting several
    /// should extend the chain rather than replace the first one.
    /// </summary>
    private byte NextHardDiskNumber()
    {
        return Session.WithMachine(machine =>
        {
            var used = machine.DiskServices.AttachedDrives
                .Select(image => image.DriveNumber)
                .Where(number => number >= 0x80)
                .ToHashSet();

            for (byte candidate = 0x80; candidate < 0x84; candidate++)
                if (!used.Contains(candidate))
                {
                    return candidate;
                }

            // All four slots taken: replace the last one rather than refuse.
            return (byte)0x83;
        });
    }

    private static readonly string[] FloppyFilters = ["img", "ima", "dsk", "vfd"];
    private static readonly string[] HardDiskFilters = ["img", "hdd", "vhd", "raw"];
    private static readonly string[] CdFilters = ["iso", "cue", "bin"];

    /// <summary>
    /// Asks for a file and inserts it into the given drive.
    ///
    /// Swapping a floppy while the guest is running is not just allowed, it
    /// is expected -- the MS-DOS installer asks for four disks in turn. The
    /// BIOS reports the change through its "change line", so DOS notices the
    /// new medium instead of trusting a stale directory cache.
    /// </summary>
    private async Task MountAsync(byte driveNumber, string title, string[] extensions)
    {
        if (PickFileAsync is null)
        {
            return;
        }

        string? path = await PickFileAsync(title, extensions);
        if (path is null)
        {
            return;
        }

        try
        {
            AttachImage(path, driveNumber, readOnly: false);
            RefreshDriveList();
            StatusText = $"Mounted {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not mount: {ex.Message}";
        }
    }

    private void AttachImage(string path, byte driveNumber, bool readOnly)
    {
        Session.WithMachine(machine =>
        {
            DiskImage image = driveNumber >= 0x80
                ? DiskImage.OpenHardDisk(path, driveNumber, readOnly)
                : DiskImage.OpenFloppy(path, driveNumber, readOnly);

            machine.AttachDisk(image);

            // Setting AURORA_WRITELOG to a path records every file the guest
            // writes to, in order. It has to be hooked here rather than when
            // the machine is built: resolving a sector to a name means reading
            // the volume's own tables, and until a disk is attached there are
            // none. It stays off unless asked for, being far too much work to
            // do on a machine that is merely running.
            string? writeLog = Environment.GetEnvironmentVariable("AURORA_WRITELOG");
            if (driveNumber >= 0x80 && _writeLog is null && !string.IsNullOrWhiteSpace(writeLog))
                _writeLog = DiskWriteLog.Attach(machine, image, writeLog);

            // Tell the guest the medium changed. Without this DOS keeps using
            // the file allocation table it read from the previous disk, and
            // the directory listing shows the old disk's files.
            if (driveNumber < 0x80)
                machine.DiskServices.NotifyMediaChanged(driveNumber);
        });
    }

    /// <summary>
    /// Ejects the floppy in drive A:, leaving the drive empty.
    ///
    /// Ejecting is not the same as swapping. DOS caches the file allocation
    /// table of whatever it last read, so the change line has to be raised
    /// either way -- otherwise a later access would happily use the old disk's
    /// directory to read a drive that is now empty.
    /// </summary>
    [RelayCommand]
    private void EjectFloppyA() => Eject(0x00);

    [RelayCommand]
    private void EjectFloppyB() => Eject(0x01);

    private void Eject(byte driveNumber)
    {
        bool present = Session.WithMachine(
            machine => machine.DiskServices.GetDrive(driveNumber) is not null);

        if (!present)
        {
            StatusText = $"Drive {(driveNumber == 0 ? 'A' : 'B')}: is already empty";
            return;
        }

        Session.WithMachine(machine =>
        {
            machine.DiskServices.Detach(driveNumber);
            machine.DiskServices.NotifyMediaChanged(driveNumber);
        });

        RefreshDriveList();
        StatusText = $"Ejected drive {(driveNumber == 0 ? 'A' : 'B')}:";
    }

    /// <summary>Takes the disc out of the CD drive.</summary>
    [RelayCommand]
    private void EjectCompactDisc()
    {
        bool present = Session.WithMachine(machine => machine.CompactDisc is not null);
        if (!present) { StatusText = "No disc in the drive"; return; }

        Session.WithMachine(machine => machine.EjectCompactDisc());
        RefreshDriveList();
        StatusText = "Ejected the disc";
    }

    [RelayCommand]
    private void EjectDrive(DriveIndicator? drive)
    {
        if (drive is null)
        {
            return;
        }

        Session.WithMachine(machine =>
        {
            machine.DiskServices.Detach(drive.DriveNumber);
            if (drive.DriveNumber < 0x80)
                machine.DiskServices.NotifyMediaChanged(drive.DriveNumber);
        });

        RefreshDriveList();
        StatusText = $"Ejected {drive.Label}";
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _audio.Dispose();
        Session.Dispose();
    }
}

/// <summary>
/// One drive as the status bar sees it: a label, a light, and the name of
/// whatever is currently in it.
/// </summary>
public sealed partial class DriveIndicator : ObservableObject
{
    public byte DriveNumber { get; }
    public string Label { get; }
    public string MediaName { get; }
    public bool IsFloppy => DriveNumber < 0x80;

    [ObservableProperty] private bool _isActive;

    public DriveIndicator(DiskImage image)
    {
        DriveNumber = image.DriveNumber;
        Label = DriveNumber switch
        {
            0x00 => "A:",
            0x01 => "B:",
            _ => $"HD{DriveNumber - 0x80}",
        };
        MediaName = image.SourcePath is null
            ? "(in memory)"
            : Path.GetFileName(image.SourcePath);
    }

    /// <summary>
    /// The CD drive. It has no BIOS drive number of its own, so it is given
    /// one past the hard disks purely so the indicator list can key on it.
    /// </summary>
    /// <summary>A folder of the host, shared with the guest as a drive letter.</summary>
    public DriveIndicator(byte drive, string folder)
    {
        DriveNumber = (byte)(0xE0 + drive);
        Label = $"{(char)('A' + drive)}: folder";
        MediaName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
    }

    /// <param name="letter">The letter the guest gave the drive, once it has said; the label shows it.</param>
    public DriveIndicator(IsoImage disc, byte? letter = null)
    {
        DriveNumber = 0xF0;
        Label = letter is null ? "CD" : $"CD {(char)('A' + letter.Value)}:";
        MediaName = disc.SourcePath is null
            ? disc.VolumeLabel
            : Path.GetFileName(disc.SourcePath);
    }
}
