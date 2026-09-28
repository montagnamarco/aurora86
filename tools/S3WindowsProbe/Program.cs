using System.Text;
using Aurora386.Core;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Storage;

// The S3TRIO.DRV window-move regression: cold boot, run Windows, drag a
// title bar down/right, and save both pictures plus the engine commands.
// The default title position matches images/hdd.img; another desktop layout
// can supply its own coordinates. The source image is never written.
if (args.Length is not (2 or 4) || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: S3WindowsProbe <S3-configured DOS image> <output folder> [title-x title-y]");
    return 2;
}
int titleX = args.Length == 4 ? int.Parse(args[2]) : 300;
int titleY = args.Length == 4 ? int.Parse(args[3]) : 60;
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
string copy = Path.Combine(Path.GetTempPath(), $"aurora-s3-{Guid.NewGuid():N}.img");
try
{
    File.Copy(args[0], copy);
    using var machine = new Machine();
    machine.AttachDisk(DiskImage.OpenHardDisk(copy));
    void Run(long count)
    {
        for (long done = 0; done < count; done += 500_000)
        {
            machine.RunInstructions(Math.Min(500_000, count - done));
            machine.Video.Render(); // exercise the GUI's incremental path
        }
    }
    void Key(byte code)
    {
        machine.Keyboard.PushScanCode(code);
        Run(1_000_000);
        machine.Keyboard.PushScanCode((byte)(code | 0x80));
        Run(3_000_000);
    }
    void Shot(string name)
    {
        machine.Video.Render();
        uint[] incremental = machine.Video.Framebuffer.ToArray();
        machine.Video.Render(true);
        uint[] pixels = machine.Video.Framebuffer.ToArray();
        int differences = incremental.Zip(pixels).Count(pair => pair.First != pair.Second);
        Console.WriteLine($"{name}: {machine.Video.Width}x{machine.Video.Height}, incremental differences: {differences}");
        if (differences != 0) throw new InvalidOperationException("Incremental rendering differs from a full redraw.");
        using var file = File.Create(Path.Combine(output, name + ".ppm"));
        file.Write(Encoding.ASCII.GetBytes($"P6\n{machine.Video.Width} {machine.Video.Height}\n255\n"));
        foreach (uint pixel in pixels)
        {
            file.WriteByte((byte)(pixel >> 16));
            file.WriteByte((byte)(pixel >> 8));
            file.WriteByte((byte)pixel);
        }
    }
    void MoveTo(int x, int y)
    {
        for (int attempt = 0; attempt < 80; attempt++)
        {
            var position = machine.S3Registers.CursorPosition;
            int dx = x - position.X, dy = y - position.Y;
            if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
            machine.Mouse.Move(Math.Clamp(dx / 2, -10, 10), Math.Clamp(dy / 2, -10, 10));
            Run(1_000_000);
        }
        throw new InvalidOperationException("The S3 hardware cursor did not reach the title bar.");
    }
    Run(400_000_000);
    foreach (byte code in new byte[] { 0x11, 0x17, 0x31, 0x1C }) Key(code); // win + Enter
    Run(500_000_000);
    if (!machine.S3Registers.CursorEnabled || machine.Video.Width < 800)
        throw new InvalidOperationException("Windows did not enable the expected S3 mode and hardware cursor.");
    Shot("before");
    using var log = new StreamWriter(Path.Combine(output, "commands.log"));
    machine.Accelerator.CommandTrace = (command, x, y, width, height, colour, mix, control) =>
        log.WriteLine($"{command:X4} src={x},{y} size={width},{height} fg={colour:X4} mix={mix:X4} pc={control:X4}");
    MoveTo(titleX, titleY);
    machine.Mouse.SetButton(MouseButtons.Left, true);
    Run(5_000_000);
    MoveTo(titleX + 80, titleY + 80);
    machine.Mouse.SetButton(MouseButtons.Left, false);
    Run(30_000_000);
    Shot("moved");
    return 0;
}
finally
{
    if (File.Exists(copy)) File.Delete(copy);
}
