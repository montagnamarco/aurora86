using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Aurora386.Core.Bios;
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

    /// <summary>Frame counts as of the last refresh, to detect activity.</summary>
    private long _lastFramesSent, _lastFramesReceived;

    /// <summary>
    /// Set by the view to open a file picker. The view model has no business
    /// knowing about windows, so it asks through this callback and stays
    /// testable.
    /// </summary>
    public Func<string, IReadOnlyList<string>, Task<string?>>? PickFileAsync { get; set; }

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

        AttachDefaultHardDisk();

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
            if (!File.Exists(candidate)) continue;

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
        });
    }

    /// <summary>
    /// Called on the emulation thread for every sector transfer. Keep it to a
    /// counter bump: anything heavier would slow the guest down.
    /// </summary>
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
    }

    /// <summary>
    /// Lights the network indicator when frames moved since the last tick.
    /// Reading the counters is cheap enough to do from the timer, and the
    /// card is only ever written by the emulation thread in whole updates.
    /// </summary>
    private void RefreshNetworkLight()
    {
        (long sent, long received) = Session.WithMachine(
            machine => (machine.Network.FramesSent, machine.Network.FramesReceived));

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

        IsoImage? disc = Session.WithMachine(machine => machine.CompactDisc);

        Drives.Clear();
        foreach (DiskImage image in attached)
            Drives.Add(new DriveIndicator(image));

        if (disc is not null) Drives.Add(new DriveIndicator(disc));
    }

    // --- toolbar commands ---------------------------------------------------

    [RelayCommand]
    private void Start()
    {
        Session.Start();
        StatusText = "Running";
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

        foreach ((string path, byte drive, bool readOnly) in media)
            AttachImage(path, drive, readOnly);

        RefreshDriveList();
        if (wasRunning) Session.Start();
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
        if (ShowDebugger is null) return;

        Session.Pause();
        ShowDebugger(new DebuggerViewModel(Session));
        StatusText = "Debugger open (machine paused)";
    }

    [RelayCommand]
    private async Task Screenshot()
    {
        if (SaveScreenshotAsync is null) return;

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
    private async Task MountCompactDisc()
    {
        if (PickFileAsync is null) return;

        string? path = await PickFileAsync("Select a CD-ROM image", CdFilters);
        if (path is null) return;

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
                if (!used.Contains(candidate)) return candidate;

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
        if (PickFileAsync is null) return;

        string? path = await PickFileAsync(title, extensions);
        if (path is null) return;

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
        if (drive is null) return;

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
    public DriveIndicator(IsoImage disc)
    {
        DriveNumber = 0xF0;
        Label = "CD";
        MediaName = disc.SourcePath is null
            ? disc.VolumeLabel
            : Path.GetFileName(disc.SourcePath);
    }
}
