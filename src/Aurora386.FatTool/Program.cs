using Aurora386.FatTool;

// Puts files into a disk image, and takes them out again.
//
// A guest with no network and no shared folder still has to be given things:
// a driver to load, a program to run, a line to change in CONFIG.SYS. Doing
// it while the machine is off is the simplest way, and the only one that
// cannot leave the file system half-written.

if (args.Length < 2)
{
    Console.Error.WriteLine("""
        aurora-fat -- put files into a DOS disk image

          aurora-fat list   <image>
          aurora-fat put    <image> <file> [name-on-disk]
          aurora-fat get    <image> <name-on-disk> [destination]
          aurora-fat append <image> <name-on-disk> <line>
          aurora-fat rmdir  <image> <directory>

        Examples:
          aurora-fat list hdd.img
          aurora-fat put hdd.img ~/Downloads/oakcdrom.sys
          aurora-fat put hdd.img mydriver.sys DRIVER.SYS
          aurora-fat append hdd.img CONFIG.SYS "LASTDRIVE=Z"

        Only the root directory is handled, which is where a driver or a
        configuration file belongs. Names must fit 8.3, and a name that does
        not is refused rather than shortened into something you did not ask
        for.
        """);

    return 1;
}

string command = args[0].ToLowerInvariant();
string imagePath = args[1];

if (!File.Exists(imagePath))
{
    Console.Error.WriteLine($"No such image: {imagePath}");
    return 1;
}

try
{
    FatVolume volume = FatVolume.Open(imagePath);

    switch (command)
    {
        case "list":
        {
            Console.WriteLine(volume.Describe());
            Console.WriteLine();

            foreach ((string name, uint size, bool isDirectory) in volume.List())
                Console.WriteLine(isDirectory
                    ? $"  {name,-14} <DIR>"
                    : $"  {name,-14} {size,10}");

            return 0;
        }

        case "put":
        {
            if (args.Length < 3) { Console.Error.WriteLine("put needs a file."); return 1; }

            string source = ExpandHome(args[2]);
            if (!File.Exists(source))
            {
                Console.Error.WriteLine($"No such file: {source}");
                return 1;
            }

            string name = args.Length > 3 ? args[3] : Path.GetFileName(source);
            byte[] contents = File.ReadAllBytes(source);

            volume.PutFile(name, contents);
            volume.Save(imagePath);

            Console.WriteLine($"Put {name} ({contents.Length} bytes) into {Path.GetFileName(imagePath)}");
            return 0;
        }

        case "get":
        {
            if (args.Length < 3) { Console.Error.WriteLine("get needs a name."); return 1; }

            byte[]? contents = volume.GetFile(args[2]);
            if (contents is null)
            {
                Console.Error.WriteLine($"{args[2]} is not in the image.");
                return 1;
            }

            string destination = args.Length > 3 ? ExpandHome(args[3]) : args[2];
            File.WriteAllBytes(destination, contents);

            Console.WriteLine($"Wrote {destination} ({contents.Length} bytes)");
            return 0;
        }

        case "rmdir":
        {
            if (args.Length < 3) { Console.Error.WriteLine("rmdir needs a name."); return 1; }

            if (!volume.RemoveDirectory(args[2]))
            {
                Console.Error.WriteLine($"{args[2]} is not in the image.");
                return 1;
            }

            volume.Save(imagePath);
            Console.WriteLine($"Removed {args[2]} and everything in it");
            return 0;
        }

        case "append":
        {
            if (args.Length < 4)
            {
                Console.Error.WriteLine("append needs a name and a line.");
                return 1;
            }

            byte[]? existing = volume.GetFile(args[2]);
            if (existing is null)
            {
                Console.Error.WriteLine($"{args[2]} is not in the image.");
                return 1;
            }

            // DOS text files end their lines with carriage return and line
            // feed, and a file that does not is read as one enormous line.
            string text = System.Text.Encoding.ASCII.GetString(existing);
            if (text.Length > 0 && !text.EndsWith('\n'))
            {
                text += "\r\n";
            }

            text += args[3] + "\r\n";

            volume.PutFile(args[2], System.Text.Encoding.ASCII.GetBytes(text));
            volume.Save(imagePath);

            Console.WriteLine($"Added to {args[2]}: {args[3]}");
            return 0;
        }

        default:
            Console.Error.WriteLine($"Unknown command: {command}");
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static string ExpandHome(string path) =>
    path.StartsWith('~')
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                       path.TrimStart('~', '/'))
        : path;
