using Aurora386.Core;
using Aurora386.Core.Devices.Storage;

// Cold-boot an installed DOS image, ask MEM, and inspect the EMS vector.
// Always work on a copy: even a diagnostic boot can write to the guest disk.
// The image must load HIMEM and EMM386 in CONFIG.SYS and stop at the prompt.
if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: dotnet run --project tools/EmmBootProbe -c Release -- <DOS image>");
    return 2;
}

string copy = Path.Combine(Path.GetTempPath(), $"aurora-emm-{Guid.NewGuid():N}.img");
try
{
    File.Copy(args[0], copy);
    using var machine = new Machine();
    machine.AttachDisk(DiskImage.OpenHardDisk(copy));
    machine.RunInstructions(400_000_000);
    uint vector = machine.Memory.ReadDword(0x67 * 4);
    ushort segment = (ushort)(vector >> 16);
    uint address = (uint)segment * 16;
    string signature = new(Enumerable.Range(10, 8)
        .Select(i => (char)machine.Memory.ReadByte(address + (uint)i)).ToArray());
    Console.WriteLine($"INT 67h = {segment:X4}:{(ushort)vector:X4}; signature = '{signature}'");

    // MEM and Enter have the same scancodes with the Italian and US layouts.
    foreach (byte key in new byte[] { 0x32, 0x12, 0x32, 0x1C })
    {
        machine.Keyboard.PushScanCode(key);
        machine.RunInstructions(1_000_000);
        machine.Keyboard.PushScanCode((byte)(key | 0x80));
        machine.RunInstructions(4_000_000);
    }
    machine.RunInstructions(100_000_000);
    for (int row = 0; row < 25; row++)
    {
        var line = new char[80];
        for (int column = 0; column < 80; column++)
        {
            byte value = machine.Memory.ReadByte((uint)(0xB8000 + (row * 80 + column) * 2));
            line[column] = value is >= 32 and < 127 ? (char)value : ' ';
        }
        Console.WriteLine(new string(line).TrimEnd());
    }
    // This proves installation; read MEM's output separately for UMB totals.
    // Microsoft EMM386 uses EMMQXXX0 when configured with NOEMS.
    return signature is "EMMXXXX0" or "EMMQXXX0" ? 0 : 1;
}
finally
{
    if (File.Exists(copy)) File.Delete(copy);
}
