using System.Text;
using Aurora386.Core;
using Aurora386.Core.Devices.Input;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Devices.Video;

// Cold boot Windows Enhanced, open the MS-DOS Prompt icon, run MEM,
// and return with EXIT. Coordinates default to the current hdd.img desktop.
// Keep screenshots of each stage; the source disk is never written.
if (args.Length is not (2 or 4) || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: WindowsDosProbe <S3-configured DOS image> <output folder> [prompt-x prompt-y]");
    return 2;
}
int promptX = args.Length == 4 ? int.Parse(args[2]) : 415;
int promptY = args.Length == 4 ? int.Parse(args[3]) : 130;
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
string copy = Path.Combine(Path.GetTempPath(), $"aurora-windows-dos-{Guid.NewGuid():N}.img");
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
        Console.WriteLine($"{name}: CPU {machine.Cpu.State.Cs.Selector:X4}:{machine.Cpu.State.Eip:X8}, CR0={machine.Cpu.State.Cr0:X8}, CR3={machine.Cpu.State.Cr3:X8}, GC6={machine.VideoRegisters.Graphics(6):X2}, BIOS={machine.VideoRegisters.VideoMode:X2}");
        MachineSnapshot.Capture(machine).Save(Path.Combine(output, name + ".snapshot"));
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
            var position = machine.VideoRegisters.CursorPosition;
            int dx = x - position.X, dy = y - position.Y;
            if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
            machine.Mouse.Move(Math.Clamp(dx / 2, -10, 10), Math.Clamp(dy / 2, -10, 10));
            Run(1_000_000);
        }
        throw new InvalidOperationException("The S3 hardware cursor did not reach the prompt icon.");
    }
    using var trace = new StreamWriter(Path.Combine(output, "interrupts.log"));
    bool tracing = false;
    machine.Cpu.SoftwareInterruptIssued += vector =>
    {
        if (!tracing) return;
        var st = machine.Cpu.State;
        if ((vector == 0x10 && (st.Ax < 0x0100 || st.Ax == 0x4F02)) ||
            (vector == 0x21 && (st.Ax >> 8 is 0x4B or 0x4C or 0x31 or 0x00)))
            trace.WriteLine($"INT {vector:X2} {st.Cs.Selector:X4}:{st.Eip:X8} AX={st.Ax:X4} BX={st.Bx:X4} DS={st.Ds.Selector:X4} DX={st.Dx:X4} CR3={st.Cr3:X8}");
    };
    Run(400_000_000);
    foreach (byte code in new byte[] { 0x11, 0x17, 0x31, 0x1C }) Key(code); // win + Enter
    Run(500_000_000);
    if (!machine.VideoRegisters.CursorEnabled || machine.Video.Width < 800)
        throw new InvalidOperationException("Windows did not enable the expected S3 mode and hardware cursor.");
    Shot("before");
    tracing = true;
    MoveTo(promptX, promptY);
    machine.Mouse.SetButton(MouseButtons.Left, true);
    Run(2_000_000);
    machine.Mouse.SetButton(MouseButtons.Left, false);
    Run(2_000_000);
    Key(0x1C); // launch the selected MS-DOS Prompt icon
    Run(150_000_000);
    Shot("dos-open");
    if (machine.VideoRegisters.EffectiveMode.Layout != VgaMemoryLayout.Text)
        throw new InvalidOperationException("The full-screen DOS Prompt did not open. Check the icon coordinates and PIF settings.");
    foreach (byte code in new byte[] { 0x32, 0x12, 0x32, 0x1C }) Key(code); // mem
    Run(50_000_000);
    Shot("dos-mem");
    for (int row = 0; row < 25; row++)
    {
        var line = new char[80];
        for (int col = 0; col < 80; col++)
        {
            byte value = machine.Memory.ReadByte((uint)(0xB8000 + (row * 80 + col) * 2));
            line[col] = value is >= 32 and < 127 ? (char)value : ' ';
        }
        Console.WriteLine(new string(line).TrimEnd());
    }
    foreach (byte code in new byte[] { 0x12, 0x2D, 0x17, 0x14, 0x1C }) Key(code); // exit
    Run(150_000_000);
    Shot("returned");
    if (machine.VideoRegisters.EffectiveMode.Layout == VgaMemoryLayout.Text || !machine.VideoRegisters.CursorEnabled || machine.Video.Width < 800)
    {
        Console.Error.WriteLine("FAIL: EXIT did not restore the S3 Windows desktop.");
        return 1;
    }
    return 0;
}
finally
{
    if (File.Exists(copy)) File.Delete(copy);
}
