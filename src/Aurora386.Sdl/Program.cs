using Aurora386.Core;
using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
using Aurora386.Core.Dos;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.Input;
using Aurora386.Sdl.Frontend;

// Emulator entry point. The command line says which disk images to attach and
// how the machine should be configured.

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
string? sharedFolder = null;
string? bootOrder = null;
string? snapshot = null;
string? dosLogPath = null;
string? writeLogPath = null;
string? mouse = null;
string? vmFolder = null;
string? network = null;
var forwards = new List<VmConfiguration.PortForward>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--floppy" or "-f":
            if (++i >= args.Length) return Fail("--floppy needs a path");
            floppies.Add(args[i]);
            break;

        case "--hdd" or "-d":
            if (++i >= args.Length) return Fail("--hdd needs a path");
            hardDisk = args[i];
            break;

        case "--create-hdd":
            if (++i >= args.Length) return Fail("--create-hdd needs a path");
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
            if (++i >= args.Length) return Fail("--vm needs a folder");
            vmFolder = args[i];
            break;

        case "--hdd2":
            if (++i >= args.Length) return Fail("--hdd2 needs a path");
            secondDisk = args[i];
            break;

        case "--cd":
            if (++i >= args.Length) return Fail("--cd needs a path");
            compactDisc = args[i];
            break;

        case "--share":
            if (++i >= args.Length) return Fail("--share needs a folder");
            sharedFolder = args[i];
            break;

        case "--boot":
            if (++i >= args.Length) return Fail("--boot needs a list of drives, like A,C");
            bootOrder = args[i];
            break;

        case "--mouse":
            if (++i >= args.Length || args[i] is not ("ps2" or "serial"))
                return Fail("--mouse needs ps2 or serial");
            mouse = args[i];
            break;

        case "--net":
            if (++i >= args.Length || args[i] is not ("nat" or "none"))
                return Fail("--net needs nat or none");
            network = args[i];
            break;

        case "--forward":
        {
            // tcp:8080:80 -- host port 8080 reaches guest port 80.
            if (++i >= args.Length) return Fail("--forward needs proto:hostport:guestport");
            string[] parts = args[i].Split(':');
            if (parts.Length != 3 || parts[0] is not ("tcp" or "udp")
                || !int.TryParse(parts[1], out int hostPort) || !int.TryParse(parts[2], out int guestPort))
                return Fail($"--forward: '{args[i]}' is not proto:hostport:guestport");
            forwards.Add(new VmConfiguration.PortForward { Protocol = parts[0], HostPort = hostPort, GuestPort = guestPort });
            break;
        }

        case "--snapshot":
            if (++i >= args.Length) return Fail("--snapshot needs a path");
            snapshot = args[i];
            break;

        case "--doslog":
            if (++i >= args.Length) return Fail("--doslog needs a path");
            dosLogPath = args[i];
            break;

        case "--writelog":
            if (++i >= args.Length) return Fail("--writelog needs a path");
            writeLogPath = args[i];
            break;

        default:
            // A bare path with no option is taken as a floppy image: it is by
            // far the most common case, so it is worth saving the typing.
            if (!args[i].StartsWith('-')) floppies.Add(args[i]);
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
    if (floppies.Count == 0 && hardDisk is null) return 0;
    hardDisk ??= createDisk;
}

if (ramMegabytes is < 1 or > 64)
    return Fail("la RAM deve essere fra 1 e 64 megabyte");

// A machine folder describes the whole machine; anything else on the
// command line is added on top of what the file says.
if (vmFolder is not null && !File.Exists(Path.Combine(vmFolder, VmConfiguration.FileName)))
    return Fail($"{vmFolder} has no {VmConfiguration.FileName} in it");

using var machine = vmFolder is null
    ? new Machine(ramMegabytes * 1024 * 1024)
    : VmConfiguration.Load(vmFolder).BuildMachine();
if (vmFolder is not null) Console.WriteLine($"machine: {vmFolder}");

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

if (sharedFolder is not null)
{
    if (!Directory.Exists(sharedFolder)) return Fail($"cartella non trovata: {sharedFolder}");
    machine.Redirector.Mount(4, new HostDirectoryFileSystem(sharedFolder));
    Console.WriteLine($"drive E: {sharedFolder} (shared; needs LASTDRIVE=E or more in CONFIG.SYS)");
}

if (network == "nat" || (network is null && forwards.Count > 0))
{
    machine.ConnectNetwork(VmConfiguration.BuildNat(forwards));
    Console.WriteLine($"network: NAT (guest 10.0.2.15, gateway 10.0.2.2, dns 10.0.2.3)"
                      + (forwards.Count > 0 ? $", {forwards.Count} port(s) forwarded" : ""));
}
else if (network == "none")
{
    machine.ConnectNetwork(new Aurora386.Core.Devices.Network.LoopbackBackend());
}

if (bootOrder is not null)
{
    // Letters, the way a BIOS setup screen shows them: A and B are the
    // floppies, C onwards the hard disks by number.
    var order = new List<byte>();
    foreach (string piece in bootOrder.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        string letter = piece.Trim();
        if (letter.Length != 1 || char.ToUpperInvariant(letter[0]) is < 'A' or > 'Z')
            return Fail($"--boot: '{piece}' is not a drive letter");
        char drive = char.ToUpperInvariant(letter[0]);
        order.Add(drive < 'C' ? (byte)(drive - 'A') : (byte)(0x80 + drive - 'C'));
    }
    machine.BootDrives = order;
    Console.WriteLine($"boot order: {bootOrder.ToUpperInvariant()}");
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
        Aurora386 -- an 80386 PC emulator

        USAGE
          aurora386 [options] [floppy-image]

        OPTIONS
          -f, --floppy <file>      floppy image (up to two: drives A: and B:)
          -d, --hdd <file>         hard disk image (drive C:)
              --create-hdd <file>  create a new partitioned hard disk
              --size <MB>          size of the disk to create (default 100)
              --ram <MB>           installed memory (default 16)
              --vm <folder>        a machine folder (vm.json); other options add to it
              --hdd2 <file>        second hard disk (81h; DOS gives it the next letter)
              --cd <file>          CD-ROM image (ISO)
              --share <folder>     a folder of this computer as drive E: (LASTDRIVE=E)
              --boot <list>        boot order, like A,C or C,A (default: every drive by number)
              --mouse ps2|serial   which port the mouse is on
              --net nat|none       put the NE2000 on a NAT network (guest 10.0.2.15)
              --forward p:h:g      reach guest port g from host port h (p is tcp or udp)
              --snapshot <file>    restore a saved state after attaching everything
              --doslog <file>      log the DOS calls that move files, and the faults
              --writelog <file>    log the files the guest writes on drive C:
          -h, --help               this message

        EXAMPLES
          Boot from a floppy:
            aurora386 disks/dos622-1.img

          Create a 200 MB hard disk:
            aurora386 --create-hdd hdd.img --size 200

          Install DOS from floppy onto the hard disk:
            aurora386 -f disks/dos622-1.img -d hdd.img

        KEYS WHILE RUNNING
          Cmd+Q  quit        Cmd+P  pause
          Cmd+R  reset       Cmd+F  full screen
        """);
}
