using Emulator.Core;
using Emulator.Core.Bios;
using Emulator.Sdl.Frontend;

// Punto di ingresso dell'emulatore. Gli argomenti indicano quali immagini
// collegare e come configurare la macchina.

if (args.Length == 0 || args.Contains("--aiuto") || args.Contains("-h"))
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
            if (++i >= args.Length) return Fail("--floppy richiede un percorso");
            floppies.Add(args[i]);
            break;

        case "--disco" or "-d":
            if (++i >= args.Length) return Fail("--disco richiede un percorso");
            hardDisk = args[i];
            break;

        case "--crea-disco":
            if (++i >= args.Length) return Fail("--crea-disco richiede un percorso");
            createDisk = args[i];
            break;

        case "--dimensione":
            if (++i >= args.Length || !long.TryParse(args[i], out createSizeMb))
                return Fail("--dimensione richiede un numero di megabyte");
            break;

        case "--ram":
            if (++i >= args.Length || !uint.TryParse(args[i], out ramMegabytes))
                return Fail("--ram richiede un numero di megabyte");
            break;

        default:
            // Un percorso senza opzione viene trattato come floppy: e' il caso
            // piu' comune e risparmia di digitare l'opzione.
            if (!args[i].StartsWith('-')) floppies.Add(args[i]);
            else return Fail($"opzione sconosciuta: {args[i]}");
            break;
    }
}

// Creazione di un nuovo disco fisso, se richiesta.
if (createDisk is not null)
{
    if (File.Exists(createDisk))
        return Fail($"il file {createDisk} esiste gia': rimuovilo o scegli un altro nome");

    var geometry = HardDiskImage.Create(createDisk, createSizeMb * 1024 * 1024);
    Console.WriteLine($"creato {createDisk}: {geometry.TotalBytes / 1024 / 1024} MB " +
                      $"({geometry.Cylinders} cilindri, {geometry.Heads} testine, " +
                      $"{geometry.SectorsPerTrack} settori)");
    Console.WriteLine("il disco e' partizionato e formattato FAT16, pronto per l'installazione.");

    // Se non e' stato chiesto altro, l'operazione finisce qui.
    if (floppies.Count == 0 && hardDisk is null) return 0;
    hardDisk ??= createDisk;
}

if (ramMegabytes is < 1 or > 64)
    return Fail("la RAM deve essere fra 1 e 64 megabyte");

using var machine = new Machine(ramMegabytes * 1024 * 1024);

// I floppy occupano le unita' 0x00 e 0x01, il disco fisso la 0x80.
for (int i = 0; i < floppies.Count && i < 2; i++)
{
    if (!File.Exists(floppies[i])) return Fail($"immagine non trovata: {floppies[i]}");
    try
    {
        machine.AttachDisk(DiskImage.OpenFloppy(floppies[i], (byte)i));
        Console.WriteLine($"unita' {(char)('A' + i)}: {Path.GetFileName(floppies[i])}");
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
    Console.WriteLine($"unita' C: {Path.GetFileName(hardDisk)}");
}

Console.WriteLine($"memoria: {ramMegabytes} MB");
Console.WriteLine();
Console.WriteLine("comandi:  Cmd+Q esci   Cmd+P pausa   Cmd+R riavvia   Cmd+F schermo intero");
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
    Console.Error.WriteLine($"errore: {message}");
    Console.Error.WriteLine("usa --aiuto per l'elenco delle opzioni");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Emulatore di PC 80386

        USO
          emulator [opzioni] [immagine-floppy]

        OPZIONI
          -f, --floppy <file>      immagine floppy (fino a due: unita' A: e B:)
          -d, --disco <file>       immagine di disco fisso (unita' C:)
              --crea-disco <file>  crea un nuovo disco fisso partizionato
              --dimensione <MB>    dimensione del disco da creare (default 100)
              --ram <MB>           memoria installata (default 16)
          -h, --aiuto              questo messaggio

        ESEMPI
          Avviare da un floppy:
            emulator dischi/dos622-1.img

          Creare un disco fisso da 200 MB:
            emulator --crea-disco hdd.img --dimensione 200

          Installare DOS dal floppy sul disco fisso:
            emulator -f dischi/dos622-1.img -d hdd.img

        COMANDI DURANTE L'ESECUZIONE
          Cmd+Q  esci        Cmd+P  pausa
          Cmd+R  riavvia     Cmd+F  schermo intero
        """);
}
