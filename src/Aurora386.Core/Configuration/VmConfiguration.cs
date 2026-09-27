using System.Text.Json;
using System.Text.Json.Serialization;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Network.Nat;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Dos;
using Aurora386.Core.Host.Midi;

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
    /// be brought forward rather than refused.
    ///
    /// Version 2 added <see cref="Hardware"/>: a place for the expansion
    /// cards the machine registry (<c>plans/specs/device-registry.md</c>)
    /// now builds from a name and settings rather than a hand-wired field.
    /// A version-1 file has none of it and reads as the machine it always
    /// described -- <see cref="Hardware"/>'s own defaults are today's
    /// hardware -- and every file this build writes is version 2.
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "PC";
    public CpuModel Cpu { get; set; } = CpuModel.Intel486;
    public uint RamMegabytes { get; set; } = 16;

    /// <summary>Optional host output for an explicitly configured MPU-401 card.</summary>
    public MidiOutputSettings? MidiOutput { get; set; }

    /// <summary>
    /// Selects one enumerated Windows MIDI endpoint. A missing ID is invalid:
    /// enabling output must never silently select the host's default synthesizer.
    /// </summary>
    public sealed class MidiOutputSettings
    {
        public uint? DeviceId { get; set; }
    }

    /// <summary>Hard disks by BIOS drive number: 0x80 is the first.</summary>
    public List<DiskEntry> Disks { get; set; } = new();

    /// <summary>Floppy images for A: and B:, in that order.</summary>
    public List<string> Floppies { get; set; } = new();

    public string? CompactDisc { get; set; }

    /// <summary>
    /// Where a version-1 file kept its USB switch, and where it is read from
    /// during <see cref="Migrate"/>. From version 2 on <see
    /// cref="HardwareSettings.Usb"/> is the source of truth that <see
    /// cref="BuildMachine"/> actually reads; this property is left populated
    /// afterwards (it still deserialises a version-1 file's own value) but
    /// nothing consults it once <see cref="Version"/> is current -- the same
    /// arrangement as <see cref="Network"/>.
    /// </summary>
    public bool UsbEnabled { get; set; }

    /// <summary>Where a version-1 file kept its USB storage image. See <see cref="UsbEnabled"/>.</summary>
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

    /// <summary>
    /// Where a version-1 file kept its network settings, and where they are
    /// read from during <see cref="Migrate"/>. From version 2 on <see
    /// cref="HardwareSettings.Network"/> is the source of truth that <see
    /// cref="BuildMachine"/> actually reads; this property is left populated
    /// afterwards (it still deserialises a version-1 file's own "network"
    /// object) but nothing consults it once <see cref="Version"/> is current.
    /// </summary>
    public NetworkSettings Network { get; set; } = new();

    /// <summary>
    /// The chipset, the processor as the registry names it, and the
    /// expansion cards fitted -- the "hardware" object of a version-2 file.
    ///
    /// <see cref="HardwareSettings.Sound"/>, <see
    /// cref="HardwareSettings.Network"/> and <see cref="HardwareSettings.Usb"/>
    /// are all the real source of truth <see cref="BuildMachine"/> reads from
    /// version 2 on; <see cref="UsbEnabled"/> and <see cref="UsbStorage"/>
    /// above (like the top-level <see cref="Network"/>) stay only for reading
    /// a version-1 file's own values during <see cref="Migrate"/>.
    /// </summary>
    public HardwareSettings Hardware { get; set; } = new();

    /// <summary>
    /// The chipset and the expansion cards it carries: sound, network and USB
    /// all reach <see cref="BuildMachine"/> from here -- see
    /// <c>plans/specs/device-registry.md</c>.
    /// </summary>
    public sealed class HardwareSettings
    {
        /// <summary>The motherboard used by configurations which omit the chipset.</summary>
        public const string DefaultChipset = "i430fx";

        /// <summary>Either "i430fx" or "i430tx"; see <see cref="VmConfiguration.Migrate"/>.</summary>
        public string Chipset { get; set; } = DefaultChipset;

        /// <summary>
        /// Sound cards, in the order that fixes their Plug and Play select
        /// numbers. Today's machine -- one Sound Blaster 16 at 220h, IRQ 5,
        /// DMA 1 and 5 -- is the default, so a version-1 file, which never
        /// mentioned this, still builds the machine it always did. An empty
        /// list means no sound card at all: ports 220h-22Fh answer as an
        /// empty bus, and the front end's audio output stays silent.
        /// </summary>
        public List<SoundCardEntry> Sound { get; set; } = new() { new SoundCardEntry() };

        /// <summary>
        /// Network cards, in the same stable order. This is the source of
        /// truth for the machine's network hardware; the top-level <see
        /// cref="VmConfiguration.Network"/> is read only while migrating a
        /// version-1 file forward -- see <see cref="VmConfiguration.Migrate"/>.
        /// Today's machine -- one NE2000 at 300h, IRQ 3, off the network -- is
        /// the default, matching <see cref="Sound"/>: an empty list means no
        /// network card at all, and ports 300h-31Fh answer as an empty bus.
        /// </summary>
        public List<NetworkCardEntry> Network { get; set; } = new() { new NetworkCardEntry() };

        /// <summary>
        /// The USB host adapter, or null for none -- today's machine, and the
        /// default for a version-1 file that never enabled it. Unlike <see
        /// cref="Sound"/> and <see cref="Network"/> this is a single card
        /// rather than a list: only one is ever registered ("uhci-piix"), and
        /// its PCI addresses are fixed by the chipset rather than read from
        /// the entry.
        /// </summary>
        public UsbSettings? Usb { get; set; }
    }

    /// <summary>One entry of <see cref="HardwareSettings.Sound"/>: a catalog name and the resources it claims.</summary>
    public sealed class SoundCardEntry
    {
        public string Card { get; set; } = "sb16";

        /// <summary>Base I/O port, as hexadecimal digits without a prefix -- "220" for 220h.</summary>
        public string Port { get; set; } = "220";

        public int Irq { get; set; } = 5;
        public int Dma { get; set; } = 1;
        public int HighDma { get; set; } = 5;

        /// <summary>The <see cref="Devices.Cards.CardSettings"/> the registry actually builds from.</summary>
        public Devices.Cards.CardSettings ToCardSettings() => new()
        {
            Card = Card,
            Port = Convert.ToUInt16(Port, 16),
            Irq = Irq,
            Dma = Dma,
            HighDma = HighDma,
        };
    }

    /// <summary>One entry of <see cref="HardwareSettings.Network"/>: a catalog name, the resources it claims, and the wire it is plugged into.</summary>
    public sealed class NetworkCardEntry
    {
        public string Card { get; set; } = "ne2000-isa";

        /// <summary>Base I/O port, as hexadecimal digits without a prefix -- "300" for 300h.</summary>
        public string Port { get; set; } = "300";

        public int Irq { get; set; } = 3;

        public NetworkMode Mode { get; set; } = NetworkMode.None;

        /// <summary>Host ports that reach the guest, for a card connected through <see cref="NetworkMode.Nat"/>.</summary>
        public List<PortForward> Forwards { get; set; } = new();

        /// <summary>The <see cref="Devices.Cards.CardSettings"/> the registry actually builds from.</summary>
        public Devices.Cards.CardSettings ToCardSettings() => new()
        {
            Card = Card,
            Port = Convert.ToUInt16(Port, 16),
            Irq = Irq,
            Mode = Mode == NetworkMode.Nat ? Devices.Cards.NetworkMode.Nat : Devices.Cards.NetworkMode.None,
            Forwards = Forwards.Select(forward => new Devices.Cards.PortForward
            {
                Tcp = !string.Equals(forward.Protocol, "udp", StringComparison.OrdinalIgnoreCase),
                HostPort = forward.HostPort,
                GuestPort = forward.GuestPort,
            }).ToList(),
        };
    }

    /// <summary>One entry of <see cref="HardwareSettings.Usb"/>: a catalog name and whatever mass storage image is inserted.</summary>
    public sealed class UsbSettings
    {
        public string Card { get; set; } = "uhci-piix";

        /// <summary>The image inserted into the storage controller's port, or null with nothing inserted.</summary>
        public UsbDiskEntry? Storage { get; set; }

        /// <summary>The <see cref="Devices.Cards.CardSettings"/> the registry actually builds from.</summary>
        public Devices.Cards.CardSettings ToCardSettings() => new() { Card = Card };
    }

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

        // A configuration built in code with only the legacy top-level
        // fields (UsbEnabled, UsbStorage, Network) is written as version 2,
        // whose loader ignores those fields: convert them into Hardware first,
        // or the saved machine would lose its USB and its network settings.
        if (!_migrated)
        {
            ConvertLegacyFieldsIntoHardware();
            _migrated = true;
        }

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
        if (CompactDisc is not null)
        {
            CompactDisc = map(CompactDisc);
        }
        if (UsbStorage is not null)
        {
            UsbStorage.Path = map(UsbStorage.Path);
        }
        if (Hardware.Usb?.Storage is not null)
        {
            Hardware.Usb.Storage.Path = map(Hardware.Usb.Storage.Path);
        }
        foreach (SharedFolder shared in SharedFolders) shared.Path = map(shared.Path);
    }

    /// <summary>
    /// True once this configuration has been through <see cref="Migrate"/>,
    /// whether by being loaded from a file (whatever its own version) or by
    /// <see cref="BuildMachine"/> applying the same conversion on its own
    /// for a configuration built directly in code. See <see
    /// cref="ConvertLegacyFieldsIntoHardware"/> for why this matters: a
    /// version-2-or-later file's own "hardware" object must not be
    /// second-guessed from the legacy top-level fields once this is true.
    /// </summary>
    private bool _migrated;

    /// <summary>
    /// Brings a file written by an older emulator up to the current shape.
    ///
    /// A version-1 file never had a <see cref="Hardware"/> object, so <see
    /// cref="ConvertLegacyFieldsIntoHardware"/> is what supplies it, from the
    /// top-level fields such a file actually had. A version-2-or-later
    /// file's own "hardware" object is always the whole truth from here on
    /// instead, including an explicit JSON null for one of its card lists --
    /// which means "no card of that kind" and must not be papered over by a
    /// stale top-level default meant only for a version-1 file. An omitted
    /// list, by contrast, never reaches this method at all: deserialising
    /// leaves it at its field initialiser's default (today's one card),
    /// because the property was never touched.
    ///
    /// What the chipset accepts is checked here rather than left to the
    /// registry, so a typo in the file is refused with its own name rather
    /// than surfacing as a missing device somewhere in POST; the same goes
    /// for a card list naming something the registry does not know, or
    /// naming the wrong kind of card for its list.
    /// </summary>
    private void Migrate()
    {
        if (Devices.Chipset.ChipsetNames.Parse(Hardware.Chipset) is null)
        {
            throw new InvalidDataException(
                $"Unknown chipset '{Hardware.Chipset}'; expected 'i430fx' or 'i430tx'.");
        }

        if (Version < CurrentVersion)
        {
            ConvertLegacyFieldsIntoHardware();
        }

        // An explicit JSON null for either list means "none": turned into an
        // empty list here rather than left to null-reference the first thing
        // that enumerates it in BuildMachine.
        Hardware.Sound ??= new List<SoundCardEntry>();
        Hardware.Network ??= new List<NetworkCardEntry>();

        ValidateCardKind("hardware.sound", "sound", Hardware.Sound.Select(card => card.Card));
        ValidateCardKind("hardware.network", "network", Hardware.Network.Select(card => card.Card));
        if (Hardware.Usb is not null)
        {
            ValidateCardKind("hardware.usb", "usb", [Hardware.Usb.Card]);
        }

        Version = CurrentVersion;
        _migrated = true;
    }

    /// <summary>
    /// Refuses a card list that names something the registry does not know,
    /// or names the right registry entry for the wrong list (a sound card
    /// entry naming a network card, say) -- both loud failures at <see
    /// cref="Load"/> rather than a machine that silently came up without the
    /// device, or with it in the wrong slot.
    /// </summary>
    private static void ValidateCardKind(string listName, string expectedKind, IEnumerable<string> cardNames)
    {
        foreach (string name in cardNames)
        {
            string? kind = Devices.Cards.CardCatalog.KindOf(name);
            if (kind is null)
            {
                throw new InvalidDataException($"'{listName}' names an unknown card '{name}'.");
            }

            if (!string.Equals(kind, expectedKind, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"'{listName}' names '{name}', which is a {kind} card, not a {expectedKind} card.");
            }
        }
    }

    /// <summary>
    /// Feeds <see cref="HardwareSettings.Usb"/> and <see
    /// cref="HardwareSettings.Network"/> from the legacy top-level <see
    /// cref="UsbEnabled"/>, <see cref="UsbStorage"/> and <see cref="Network"/>
    /// fields, but only where <see cref="Hardware"/> itself was left at its
    /// own untouched default -- so this never overwrites a hardware object a
    /// version-2-or-later file, or a caller building one directly in code,
    /// set explicitly.
    ///
    /// Called from three places. <see
    /// cref="Migrate"/> calls it for a genuine version-1 file, which never
    /// had a "hardware" object to set anything in, so "untouched default" is
    /// simply true. <see cref="BuildMachine"/> calls it for a configuration
    /// that has never been through <see cref="Migrate"/> at all -- built
    /// directly in code rather than loaded -- so that code written against
    /// the pre-registry fields (<c>configuration.UsbEnabled = true;</c>)
    /// keeps working without needing to know <see cref="Hardware"/> exists.
    /// <see cref="Save"/> calls it for the same kind of configuration before
    /// writing it: the file is version 2, whose loader ignores the legacy
    /// fields, so they have to be in Hardware by then.
    /// </summary>
    private void ConvertLegacyFieldsIntoHardware()
    {
        if (Hardware.Usb is null && (UsbEnabled || UsbStorage is not null))
        {
            Hardware.Usb = new UsbSettings
            {
                Storage = UsbStorage is null
                    ? null
                    : new UsbDiskEntry { Path = UsbStorage.Path, ReadOnly = UsbStorage.ReadOnly },
            };
        }

        if (IsUntouchedDefaultNetwork(Hardware.Network) && (Network.Mode != NetworkMode.None || Network.Forwards.Count > 0))
        {
            Hardware.Network = new List<NetworkCardEntry>
            {
                new() { Mode = Network.Mode, Forwards = Network.Forwards },
            };
        }
    }

    /// <summary>
    /// Whether <see cref="HardwareSettings.Network"/> is still exactly the
    /// one entry its own field initialiser gives it -- the signal that
    /// nothing has set it explicitly, so <see
    /// cref="ConvertLegacyFieldsIntoHardware"/> may still feed it from the
    /// legacy top-level <see cref="Network"/> field. Unlike <see
    /// cref="HardwareSettings.Usb"/>, whose untouched default is simply
    /// null, <see cref="HardwareSettings.Network"/> defaults to a non-null
    /// single entry, so "untouched" has to be checked by value instead.
    /// </summary>
    private static bool IsUntouchedDefaultNetwork(List<NetworkCardEntry>? network)
        => network is { Count: 1 } list
           && list[0].Card == "ne2000-isa" && list[0].Port == "300" && list[0].Irq == 3
           && list[0].Mode == NetworkMode.None && list[0].Forwards.Count == 0;

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
        if (!_migrated)
        {
            // Never loaded, so Migrate never ran: a configuration built
            // directly in code still honours UsbEnabled/UsbStorage/Network
            // exactly as it always could, the same conversion Migrate does
            // for a version-1 file. See ConvertLegacyFieldsIntoHardware.
            ConvertLegacyFieldsIntoHardware();
            Hardware.Sound ??= new List<SoundCardEntry>();
            Hardware.Network ??= new List<NetworkCardEntry>();
        }

        var hardware = new Devices.Cards.HardwareConfiguration
        {
            Sound = Hardware.Sound.Select(card => card.ToCardSettings()).ToList(),
            Network = Hardware.Network.Select(card => card.ToCardSettings()).ToList(),
            Usb = Hardware.Usb?.ToCardSettings(),
        };

        Devices.Chipset.Chipset chipset = Devices.Chipset.ChipsetNames.Parse(Hardware.Chipset)
            ?? throw new InvalidDataException($"Unknown chipset '{Hardware.Chipset}'.");

        // The configured model is selected at construction, not afterwards
        // (independent review, item 6): the BIOS's MP tables describe a
        // specific CPU's signature, feature flags and local APIC version,
        // and Machine builds its ROM image once during construction --
        // setting CpuModel only after the fact would leave a TX board's MP
        // tables describing whatever the default model was, never the one
        // actually configured.
        Machine machine = new Machine(RamMegabytes * 1024 * 1024, hardware, chipset, initialCpuModel: Cpu);
        try
        {
            if (Hardware.Usb?.Storage is { } storage)
                machine.AttachUsbStorage(Devices.Usb.UsbMassStorageDevice.Open(storage.Path, storage.ReadOnly));

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

            // Open host output last, after every guest resource has been validated.
            if (MidiOutput is not null)
            {
                if (machine.Midi is null)
                {
                    throw new InvalidDataException("MIDI output requires an MPU-401 sound card.");
                }

                if (MidiOutput.DeviceId is not uint deviceId)
                {
                    throw new InvalidDataException("MIDI output requires an explicit deviceId.");
                }

                machine.ConnectMidi(WinMmMidiSink.Open(deviceId));
            }

            return machine;
        }
        catch
        {
            // A card built above may already hold a host resource -- a
            // NAT-backed network card's listening sockets chief among them
            // -- that nobody else gets the chance to close if this method
            // throws without returning the machine: the caller never sees a
            // reference to dispose. Close it here instead of leaking it.
            machine.Dispose();
            throw;
        }
    }
}
