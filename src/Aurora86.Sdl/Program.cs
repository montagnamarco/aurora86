using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Firmware;
using Aurora86.Core.Firmware.Fpu;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Diagnostics;
using Aurora86.Core.Dos;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Input;
using Aurora86.Sdl.Frontend;
using Aurora86.Core.Host.Midi;

// Emulator entry point. The command line says which disk images to attach and
// how the machine should be configured.

if (args.Length == 1 && args[0] == "--list-midi")
{
    try
    {
        foreach (MidiOutputDevice device in WinMmMidiSink.GetDevices())
        {
            Console.WriteLine($"{device.DeviceId}: {device.Name}");
        }
    }
    catch (IOException exception)
    {
        return Fail(exception.Message);
    }

    return 0;
}

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

uint ramMegabytes = 16;
var floppies = new List<string>();
string? hardDisk = null;
string? createDisk = null;
long createSizeMb = 100;
string? secondDisk = null;
string? compactDisc = null;
var sharedFolders = new List<(byte? Drive, string Path)>();
string? bootOrder = null;
string? snapshot = null;
string? dosLogPath = null;
string? writeLogPath = null;
string? mouse = null;
string? cpuModel = null;
VmVideoModel? videoModel = null;
bool hostIntegration = false;
string? printerFolder = null;
string? vmFolder = null;
string? network = null;
var forwards = new List<VmConfiguration.PortForward>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--floppy" or "-f":
            if (++i >= args.Length)
            {
                return Fail("--floppy needs a path");
            }
            floppies.Add(args[i]);
            break;

        case "--hdd" or "-d":
            if (++i >= args.Length)
            {
                return Fail("--hdd needs a path");
            }
            hardDisk = args[i];
            break;

        case "--create-hdd":
            if (++i >= args.Length)
            {
                return Fail("--create-hdd needs a path");
            }
            createDisk = args[i];
            break;

        case "--size":
            if (++i >= args.Length || !long.TryParse(args[i], out createSizeMb))
                return Fail("--size needs a number of megabytes");
            break;

        case "--ram":
            if (++i >= args.Length || !uint.TryParse(args[i], out ramMegabytes))
                return Fail("--ram needs a number of megabytes");
            break;

        case "--vm":
            if (++i >= args.Length)
            {
                return Fail("--vm needs a folder");
            }
            vmFolder = args[i];
            break;

        case "--hdd2":
            if (++i >= args.Length)
            {
                return Fail("--hdd2 needs a path");
            }
            secondDisk = args[i];
            break;

        case "--cd":
            if (++i >= args.Length)
            {
                return Fail("--cd needs a path");
            }
            compactDisc = args[i];
            break;

        case "--share":
        {
            // A folder, or a letter and a folder: "--share F:/some/folder".
            // Without a letter the next free one after the disks is used,
            // E: on a one-disk machine. The option repeats for more folders.
            if (++i >= args.Length)
            {
                return Fail("--share needs a folder");
            }
            string spec = args[i];
            byte? letter = null;
            if (spec.Length > 2 && spec[1] == ':' && char.IsAsciiLetter(spec[0]) && !Directory.Exists(spec))
            {
                letter = (byte)(char.ToUpperInvariant(spec[0]) - 'A');
                spec = spec[2..];
            }
            sharedFolders.Add((letter, spec));
            break;
        }

        case "--boot":
            if (++i >= args.Length)
            {
                return Fail("--boot needs a list of drives, like A,C or CD,C");
            }
            bootOrder = args[i];
            break;

        case "--mouse":
            if (++i >= args.Length || args[i] is not ("ps2" or "serial"))
                return Fail("--mouse needs ps2 or serial");
            mouse = args[i];
            break;

        case "--cpu":
            if (++i >= args.Length || args[i] is not ("386" or "486" or "pentium" or "pentium-mmx"
                    or "pentium2" or "pentium3" or "pentium4"))
                return Fail("--cpu needs 386, 486, pentium, pentium-mmx, pentium2, pentium3 or pentium4");
            cpuModel = args[i];
            break;

        // The display adapter, which is built with the machine and so has to
        // be known before it: unlike the processor it cannot be swapped once
        // the machine exists.
        case "--video":
            if (++i >= args.Length || args[i] is not ("trio" or "virge" or "aurora"))
                return Fail("--video needs trio, virge or aurora");
            videoModel = args[i] switch
            {
                "virge" => VmVideoModel.S3Virge325,
                "aurora" => VmVideoModel.AuroraGpu,
                _ => VmVideoModel.S3Trio64VPlus,
            };
            break;

        // The VMware-compatible channel to the host (docs/VMWARE-BACKDOOR.md).
        case "--host-integration":
            hostIntegration = true;
            break;

        // Where LPT1's print jobs land on the host (docs/PARALLEL-PORT.md).
        case "--printer":
            if (++i >= args.Length)
                return Fail("--printer needs a folder");
            printerFolder = args[i];
            break;

        case "--net":
            if (++i >= args.Length || args[i] is not ("nat" or "none"))
                return Fail("--net needs nat or none");
            network = args[i];
            break;

        case "--forward":
        {
            // tcp:8080:80 -- host port 8080 reaches guest port 80.
            if (++i >= args.Length)
            {
                return Fail("--forward needs proto:hostport:guestport");
            }
            string[] parts = args[i].Split(':');
            if (parts.Length != 3 || parts[0] is not ("tcp" or "udp")
                || !int.TryParse(parts[1], out int hostPort) || !int.TryParse(parts[2], out int guestPort))
                return Fail($"--forward: '{args[i]}' is not proto:hostport:guestport");
            forwards.Add(new VmConfiguration.PortForward { Protocol = parts[0], HostPort = hostPort, GuestPort = guestPort });
            break;
        }

        case "--snapshot":
            if (++i >= args.Length)
            {
                return Fail("--snapshot needs a path");
            }
            snapshot = args[i];
            break;

        case "--doslog":
            if (++i >= args.Length)
            {
                return Fail("--doslog needs a path");
            }
            dosLogPath = args[i];
            break;

        case "--writelog":
            if (++i >= args.Length)
            {
                return Fail("--writelog needs a path");
            }
            writeLogPath = args[i];
            break;

        default:
            // A bare path with no option is taken as a floppy image: it is by
            // far the most common case, so it is worth saving the typing.
            if (!args[i].StartsWith('-'))
            {
                floppies.Add(args[i]);
            }
            else return Fail($"opzione sconosciuta: {args[i]}");
            break;
    }
}

// Create a new hard disk image, if that was asked for.
if (createDisk is not null)
{
    if (File.Exists(createDisk))
        return Fail($"il file {createDisk} esiste gia': rimuovilo o scegli un altro nome");

    var geometry = HardDiskImage.Create(createDisk, createSizeMb * 1024 * 1024);
    Console.WriteLine($"created {createDisk}: {geometry.TotalBytes / 1024 / 1024} MB " +
                      $"({geometry.Cylinders} cilindri, {geometry.Heads} testine, " +
                      $"{geometry.SectorsPerTrack} settori)");
    Console.WriteLine("The disk is partitioned and formatted FAT16, ready to install onto.");

    // If nothing else was requested, creating the image was the whole job.
    if (floppies.Count == 0 && hardDisk is null)
    {
        return 0;
    }
    hardDisk ??= createDisk;
}

if (ramMegabytes is < 1 or > 128)
    return Fail("la RAM deve essere fra 1 e 128 megabyte");

// A machine folder describes the whole machine; anything else on the
// command line is added on top of what the file says.
if (vmFolder is not null && !File.Exists(Path.Combine(vmFolder, VmConfiguration.FileName)))
    return Fail($"{vmFolder} has no {VmConfiguration.FileName} in it");

using var machine = vmFolder is null
    ? new Machine(ramMegabytes * 1024 * 1024, videoModel: videoModel ?? VmVideoModel.S3Trio64VPlus)
    : LoadConfiguredMachine(vmFolder, videoModel);
if (vmFolder is not null) Console.WriteLine($"machine: {vmFolder}");
using var graphicsBackend = Aurora86.Graphics.AuroraGpuBackendConnection.Attach(machine);
if (Environment.GetEnvironmentVariable("AURORA_GPU_BACKEND") is not null)
{
    Console.WriteLine($"graphics backend: {graphicsBackend.Description}");
}
if (printerFolder is not null)
{
    machine.PrintOutputFolder = Path.GetFullPath(printerFolder);
    Console.WriteLine($"printer: jobs on LPT1 go to {machine.PrintOutputFolder}");
}
if (hostIntegration)
{
    machine.Backdoor.Enabled = true;
    Console.WriteLine("host integration: VMware-compatible backdoor on port 5658h");
}

// Floppies take BIOS drive numbers 0x00 and 0x01; the hard disk takes 0x80,
// the convention that separates removable from fixed media.
for (int i = 0; i < floppies.Count && i < 2; i++)
{
    if (!File.Exists(floppies[i])) return Fail($"immagine non trovata: {floppies[i]}");
    try
    {
        machine.AttachDisk(DiskImage.OpenFloppy(floppies[i], (byte)i));
        Console.WriteLine($"drive {(char)('A' + i)}: {Path.GetFileName(floppies[i])}");
    }
    catch (InvalidDataException ex)
    {
        return Fail($"{floppies[i]}: {ex.Message}");
    }
}

if (hardDisk is not null)
{
    if (!File.Exists(hardDisk)) return Fail($"immagine non trovata: {hardDisk}");
    machine.AttachDisk(DiskImage.OpenHardDisk(hardDisk));
    Console.WriteLine($"drive C: {Path.GetFileName(hardDisk)}");
}

// Everything the window can do, the command line can do too: a script that
// boots a machine into a known state needs no hands on it, and a bug that
// shows up only with a second disk or a shared folder needs a way to be
// reproduced from one line.
DiskImage? firstDisk = hardDisk is null ? null : machine.DiskServices.GetDrive(0x80);

if (secondDisk is not null)
{
    if (!File.Exists(secondDisk)) return Fail($"immagine non trovata: {secondDisk}");
    machine.AttachDisk(DiskImage.OpenHardDisk(secondDisk, 0x81));
    Console.WriteLine($"disk 81h: {Path.GetFileName(secondDisk)} (DOS gives it the next letter)");
}

if (compactDisc is not null)
{
    if (!File.Exists(compactDisc)) return Fail($"immagine non trovata: {compactDisc}");
    machine.AttachCompactDisc(IsoImage.Open(compactDisc));
    Console.WriteLine($"cd-rom: {Path.GetFileName(compactDisc)}");
}

foreach ((byte? requested, string folder) in sharedFolders)
{
    if (!Directory.Exists(folder)) return Fail($"cartella non trovata: {folder}");
    byte drive;
    if (requested is { } explicitDrive)
    {
        drive = explicitDrive;
    }
    else if (!machine.TryGetNextSharedFolderDrive(out drive))
        return Fail("nessuna lettera DOS libera per la cartella condivisa; controllare le unità occupate e LASTDRIVE");
    machine.Redirector.Mount(drive, new HostDirectoryFileSystem(folder));
    char letter = (char)('A' + drive);
    Console.WriteLine($"drive {letter}: {folder} (shared; needs LASTDRIVE={letter} or more in CONFIG.SYS)");
}

if (network == "nat" || (network is null && forwards.Count > 0))
{
    machine.ConnectNetwork(VmConfiguration.BuildNat(forwards));
    Console.WriteLine($"network: NAT (guest 10.0.2.15, gateway 10.0.2.2, dns 10.0.2.3)"
                      + (forwards.Count > 0 ? $", {forwards.Count} port(s) forwarded" : ""));
}
else if (network == "none")
{
    machine.ConnectNetwork(new Aurora86.Core.Devices.Network.LoopbackBackend());
}

if (bootOrder is not null)
{
    // Letters, the way a BIOS setup screen shows them: A and B are the
    // floppies, C onwards the hard disks by number, and CD the disc -- which
    // a setup screen of the period also spelled out rather than lettering.
    var order = new List<byte>();
    foreach (string piece in bootOrder.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        string name = piece.Trim().ToUpperInvariant();

        if (name is "CD" or "CDROM")
        {
            order.Add(Machine.CompactDiscBootDrive);
            continue;
        }

        if (name.Length != 1 || name[0] is < 'A' or > 'Z')
            return Fail($"--boot: '{piece}' is not a drive letter");

        char drive = name[0];
        order.Add(drive < 'C' ? (byte)(drive - 'A') : (byte)(0x80 + drive - 'C'));
    }
    machine.BootDrives = order;
    Console.WriteLine($"boot order: {bootOrder.ToUpperInvariant()}");
}

// The processor, which must be chosen before the guest runs its first
// instruction. A bootloader written after the middle nineties assumes RDTSC,
// and a current assembler pads loops with the multi-byte NOP and a compiler
// emits CMOVcc -- both Pentium Pro instructions. On an older model those
// raise an invalid opcode with no handler installed yet: a triple fault, and
// a machine that appears to restart for ever. Anything booting a current ISO
// wants pentium2 or later.
if (cpuModel is not null)
{
    machine.CpuModel = cpuModel switch
    {
        "386" => CpuModel.Intel386,
        "pentium" => CpuModel.Pentium,
        "pentium-mmx" => CpuModel.PentiumMmx,
        "pentium2" => CpuModel.PentiumII,
        "pentium3" => CpuModel.PentiumIII,
        "pentium4" => CpuModel.Pentium4,
        _ => CpuModel.Intel486,
    };
    Console.WriteLine($"cpu: {cpuModel}");
}

if (mouse is not null)
{
    machine.MouseInterface = mouse == "ps2" ? MouseInterface.Ps2 : MouseInterface.Serial;
    Console.WriteLine($"mouse: {mouse}");
}

// The logs go on last, once every disk they might name is attached, and
// stay alive until the window closes.
using DosCallLog? dosLog = dosLogPath is null ? null : DosCallLog.Attach(machine, dosLogPath);
using DiskWriteLog? writeLog = writeLogPath is null || firstDisk is null ? null
                              : DiskWriteLog.Attach(machine, firstDisk, writeLogPath);
if (writeLogPath is not null && firstDisk is null)
    Console.WriteLine("--writelog ignored: there is no hard disk to watch");

// A saved state replaces the boot: it comes after every attachment, so
// that the disks it expects are the ones it finds.
if (snapshot is not null)
{
    if (!File.Exists(snapshot)) return Fail($"stato non trovato: {snapshot}");
    MachineSnapshot.Load(snapshot).RestoreTo(machine);
    Console.WriteLine($"restored: {Path.GetFileName(snapshot)}");
}

Console.WriteLine($"memory: {ramMegabytes} MB");
Console.WriteLine();
Console.WriteLine("keys:  Cmd+Q quit   Cmd+P pause   Cmd+R reset   Cmd+F full screen");
Console.WriteLine();

try
{
    using var window = new EmulatorWindow(machine);
    window.Run();
}
catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
{
    Console.Error.WriteLine("Non riesco a caricare SDL2.");
    Console.Error.WriteLine("Su macOS lo installi con:  brew install sdl2");
    Console.Error.WriteLine($"Dettaglio: {ex.Message}");
    return 1;
}

return 0;

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    Console.Error.WriteLine("run with --help for the list of options");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Aurora86 -- an x86 PC emulator

        USAGE
          aurora86 [options] [floppy-image]

        OPTIONS
          -f, --floppy <file>      floppy image (up to two: drives A: and B:)
          -d, --hdd <file>         hard disk image (drive C:)
              --create-hdd <file>  create a new partitioned hard disk
              --size <MB>          size of the disk to create (default 100)
              --ram <MB>           installed memory, 1 to 128 (default 16)
              --vm <folder>        a machine folder (vm.json); other options add to it
              --list-midi          list Windows MIDI output IDs without opening them
              --hdd2 <file>        second hard disk (81h; DOS gives it the next letter)
              --cd <file>          CD-ROM image (ISO)
              --share [X:]<folder> a folder of this computer as a drive: the next free
                                   letter after the disks, or X:; repeatable
              --boot <list>        boot order, like A,C or CD,C (default: every drive by number)
              --mouse ps2|serial   which port the mouse is on
              --host-integration   answer the VMware-compatible backdoor (port 5658h)
              --printer <folder>   write LPT1 print jobs there (.prn, and .pdf for plain text)
              --video <adapter>    trio (default), virge, or aurora: the Aurora Display
                                   Adapter, VBE up to 1920x1080x32 and a 2D engine
              --cpu <model>        386, 486, pentium, pentium-mmx, pentium2, pentium3 or
                                   pentium4 (default 486; a current ISO needs pentium2 or later)
              --net nat|none       put the NE2000 on a NAT network (guest 10.0.2.15)
              --forward p:h:g      reach guest port g from host port h (p is tcp or udp)
              --snapshot <file>    restore a saved state after attaching everything
              --doslog <file>      log the DOS calls that move files, and the faults
              --writelog <file>    log the files the guest writes on drive C:
          -h, --help               this message

        EXAMPLES
          Boot from a floppy:
            aurora86 disks/dos622-1.img

          Create a 200 MB hard disk:
            aurora86 --create-hdd hdd.img --size 200

          Install DOS from floppy onto the hard disk:
            aurora86 -f disks/dos622-1.img -d hdd.img

        KEYS WHILE RUNNING
          Cmd+Q  quit        Cmd+P  pause
          Cmd+R  reset       Cmd+F  full screen
        """);
}

// A machine folder with its adapter replaced by --video, when given.
static Machine LoadConfiguredMachine(string folder, VmVideoModel? videoModel)
{
    VmConfiguration configuration = VmConfiguration.Load(folder);
    if (videoModel is { } requested)
    {
        configuration.Video = requested;
    }

    return configuration.BuildMachine();
}
