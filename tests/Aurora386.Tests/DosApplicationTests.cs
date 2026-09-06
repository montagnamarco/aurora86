using Aurora386.Core;
using Aurora386.Core.Devices.Storage;

namespace Aurora386.Tests;

/// <summary>
/// Slow tests that boot a real installed DOS and drive a real program.
///
/// They exist because of a regression the unit tests could not have caught:
/// the mouse work made EDIT and QBASIC stop responding to the keyboard, and
/// QBASIC die with a corrupted far heap, while every unit test stayed green.
/// The faults were in the seams -- a vector reclaimed from a program that
/// wanted it, a callback delivered wherever the batch happened to end -- and
/// only a whole guest running a whole program shows those.
///
/// They are skipped when the installed image is not there, which is the usual
/// case on a fresh clone: the image is a gigabyte of somebody else's software
/// and does not belong in the repository.
/// </summary>
public class DosApplicationTests
{
    private const string ImagePath = "images/hdd-setup-completato.img";

    /// <summary>The image, found by walking up from wherever the tests run.</summary>
    private static string? FindImage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ImagePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private sealed class Rig : IDisposable
    {
        public Machine Machine { get; }
        private readonly string _copy;

        public Rig(string image)
        {
            // A copy, because the guest writes to its disk and a test must not
            // leave the reference image changed.
            _copy = Path.Combine(Path.GetTempPath(), $"aurora-dos-{Guid.NewGuid():N}.img");
            File.Copy(image, _copy);

            Machine = new Machine();
            Machine.AttachDisk(DiskImage.OpenHardDisk(_copy));
        }

        public void Run(long instructions)
        {
            for (long done = 0; done < instructions; done += 10_000)
                Machine.RunInstructions(10_000);
        }

        /// <summary>Presses and releases one key, then lets the guest react.</summary>
        public void Key(byte scanCode, long settle = 3_000_000)
        {
            Machine.Keyboard.PushScanCode(scanCode);
            Run(2_000_000);
            Machine.Keyboard.PushScanCode((byte)(scanCode | 0x80));
            Run(settle);
        }

        public void Type(params byte[] scanCodes)
        {
            foreach (byte code in scanCodes) Key(code);
        }

        public string Screen() => Machine.GetScreenText();

        public void Dispose()
        {
            Machine.Dispose();
            try { File.Delete(_copy); } catch (IOException) { }
        }
    }

    [Fact]
    public void EditOpensAndAcceptsTypingWithTheMouseDriverPresent()
    {
        string? image = FindImage();
        if (image is null) return;      // no installed image here: nothing to drive

        using var rig = new Rig(image);
        rig.Run(3_000_000_000);                                   // boot to the prompt
        Assert.Contains("C:\\>", rig.Screen());

        rig.Type(0x12, 0x20, 0x17, 0x14);                         // e d i t
        rig.Key(0x1C, settle: 400_000_000);                       // enter
        Assert.Contains("MS-DOS Editor", rig.Screen());

        // Escape dismisses the welcome box. This is the step that failed when
        // the machine was reclaiming INT 33h from under the program.
        rig.Key(0x01, settle: 120_000_000);
        Assert.DoesNotContain("Premere ESC", rig.Screen());

        rig.Key(0x2E, settle: 30_000_000);                        // c
        rig.Key(0x17, settle: 30_000_000);                        // i
        rig.Key(0x1E, settle: 30_000_000);                        // a
        rig.Key(0x18, settle: 30_000_000);                        // o

        // The characters reached the editor, and the cursor moved with them.
        string screen = rig.Screen();
        Assert.Contains("ciao", screen);
        Assert.Contains("00001:005", screen);
    }
}
