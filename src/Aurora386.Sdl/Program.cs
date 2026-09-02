using Aurora386.Core;
using Aurora386.Core.Bios;
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

using var machine = new Machine(ramMegabytes * 1024 * 1024);

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
