using System.Text.Json;
using System.Text.Json.Serialization;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Network.Nat;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Dos;

namespace Aurora386.Core.Configuration;

/// <summary>
/// Everything that makes one virtual machine different from another, kept
/// in a file a person can read and edit.
///
/// The emulator used to be one machine, with menus for changing what was in
/// its drives. That is fine until there are two: a DOS machine for games
/// and a Windows one for the setup that keeps failing, each with its own
/// disks and its own memory size, and swapping between them by hand means
/// getting one setting wrong every time. A folder with a vm.json and the
/// images beside it is the unit that fixes that. It can be copied, backed
/// up and handed to somebody else, and it opens the same way whether the
/// window or the command line asks for it.
///
/// Paths inside the folder are stored relative to it, so that moving the
/// folder moves the machine. Paths outside it are stored as they are, on
/// the grounds that they point at something the machine borrows rather
/// than owns.
/// </summary>
public sealed class VmConfiguration
{
    /// <summary>The name of the file inside a machine's folder.</summary>
    public const string FileName = "vm.json";

    /// <summary>
    /// The format's version, so that a file written by an older emulator can
    /// be brought forward rather than refused. There has been one so far.
    /// </summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "PC";
    public CpuModel Cpu { get; set; } = CpuModel.Intel486;
    public uint RamMegabytes { get; set; } = 16;

    /// <summary>Hard disks by BIOS drive number: 0x80 is the first.</summary>
    public List<DiskEntry> Disks { get; set; } = new();

    /// <summary>Floppy images for A: and B:, in that order.</summary>
    public List<string> Floppies { get; set; } = new();

    public string? CompactDisc { get; set; }
    public bool UsbEnabled { get; set; }
    public UsbDiskEntry? UsbStorage { get; set; }
    public sealed class UsbDiskEntry
    {
        public string Path { get; set; } = "";
        public bool ReadOnly { get; set; }
    }
    public List<SharedFolder> SharedFolders { get; set; } = new();
    public MouseInterface Mouse { get; set; } = MouseInterface.Serial;

    /// <summary>Drives to try booting from, as BIOS numbers. Empty means every drive by number.</summary>
    public List<byte> BootOrder { get; set; } = new();

    public NetworkSettings Network { get; set; } = new();

    public enum NetworkMode
    {
        /// <summary>The card is there but its frames go nowhere: the machine is off the network.</summary>
        None,

        /// <summary>A private network behind the emulator, reaching out through the host's sockets.</summary>
        Nat,
    }

    public sealed class NetworkSettings
    {
        public NetworkMode Mode { get; set; } = NetworkMode.None;

        /// <summary>Host ports that reach the guest, for the connections a NAT would otherwise refuse.</summary>
        public List<PortForward> Forwards { get; set; } = new();
    }

    public sealed class PortForward
    {
        /// <summary>"tcp" or "udp".</summary>
        public string Protocol { get; set; } = "tcp";
        public int HostPort { get; set; }
        public int GuestPort { get; set; }
    }

    public sealed class DiskEntry
    {
        public string Path { get; set; } = "";
        public byte Drive { get; set; } = 0x80;
        public bool ReadOnly { get; set; }
    }

    public sealed class SharedFolder
    {
        public string Path { get; set; } = "";

        /// <summary>The drive letter as a number from zero: 4 is E:.</summary>
        public byte Drive { get; set; } = 4;

        public bool ReadOnly { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads a machine's folder. Paths come back absolute.</summary>
    public static VmConfiguration Load(string folder)
    {
        string file = System.IO.Path.Combine(folder, FileName);
        VmConfiguration configuration =
            JsonSerializer.Deserialize<VmConfiguration>(File.ReadAllText(file), Options)
            ?? throw new InvalidDataException($"{file} does not describe a machine.");

        configuration.Migrate();
        configuration.MapPaths(path => System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, path)));
        return configuration;
    }

    /// <summary>Writes the machine's folder. Paths inside it are stored relative to it.</summary>
    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        string root = System.IO.Path.GetFullPath(folder);
        string prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? root
            : root + System.IO.Path.DirectorySeparatorChar;

        // A copy is serialised, so that the object in hand keeps its absolute
        // paths and goes on being usable after it has been saved.
        VmConfiguration copy = JsonSerializer.Deserialize<VmConfiguration>(
            JsonSerializer.Serialize(this, Options), Options)!;
        copy.MapPaths(path =>
        {
            string full = System.IO.Path.GetFullPath(path);
            return full.StartsWith(prefix, StringComparison.Ordinal) ? full[prefix.Length..] : path;
        });

        File.WriteAllText(System.IO.Path.Combine(folder, FileName),
                          JsonSerializer.Serialize(copy, Options));
    }

    private void MapPaths(Func<string, string> map)
    {
        foreach (DiskEntry disk in Disks) disk.Path = map(disk.Path);
        for (int i = 0; i < Floppies.Count; i++) Floppies[i] = map(Floppies[i]);
        if (CompactDisc is not null) CompactDisc = map(CompactDisc);
        if (UsbStorage is not null) UsbStorage.Path = map(UsbStorage.Path);
        foreach (SharedFolder shared in SharedFolders) shared.Path = map(shared.Path);
    }

    /// <summary>
    /// Brings a file written by an older emulator up to the current shape.
    ///
    /// There is nothing to do yet. The place exists so that the first change
    /// to the format has somewhere to go other than "refuse the file", which
    /// is what happens to formats that did not plan for a second version.
    /// </summary>
    private void Migrate()
    {
        Version = CurrentVersion;
    }

    /// <summary>
    /// Builds the machine this describes: memory, processor, every drive
    /// and folder attached, not yet running.
    ///
    /// This is the one place the description is turned into a machine, and
    /// both the window and the command line go through it, which is what
    /// makes "the same vm.json opens the same way everywhere" true rather
    /// than hoped for.
    /// </summary>
    /// <summary>A NAT with the given ports forwarded, ready to plug into a card.</summary>
    public static NatBackend BuildNat(IEnumerable<PortForward> forwards)
    {
        var nat = new NatBackend();
        foreach (PortForward forward in forwards)
            nat.Forward(!string.Equals(forward.Protocol, "udp", StringComparison.OrdinalIgnoreCase),
                        forward.HostPort, forward.GuestPort);
        return nat;
    }

    public Machine BuildMachine()
    {
        var machine = new Machine(RamMegabytes * 1024 * 1024);
        machine.CpuModel = Cpu;
        if (UsbEnabled) machine.EnableUsb();
        if (UsbStorage is not null)
            machine.AttachUsbStorage(Devices.Usb.UsbMassStorageDevice.Open(UsbStorage.Path, UsbStorage.ReadOnly));

        for (int i = 0; i < Floppies.Count && i < 2; i++)
            machine.AttachDisk(DiskImage.OpenFloppy(Floppies[i], (byte)i));

        foreach (DiskEntry disk in Disks)
            machine.AttachDisk(DiskImage.OpenHardDisk(disk.Path, disk.Drive, disk.ReadOnly));

        if (CompactDisc is not null)
            machine.AttachCompactDisc(IsoImage.Open(CompactDisc));

        foreach (SharedFolder shared in SharedFolders)
            machine.Redirector.Mount(shared.Drive, new HostDirectoryFileSystem(shared.Path) { IsReadOnly = shared.ReadOnly });

        machine.MouseInterface = Mouse;
        machine.BootDrives = BootOrder;
        if (Network.Mode == NetworkMode.Nat) machine.ConnectNetwork(BuildNat(Network.Forwards));
        return machine;
    }
}
