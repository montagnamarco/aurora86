using System.Text;
using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Configuration;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Devices.Input;
using Aurora86.Core.Devices.Video;
using Aurora86.Core.Diagnostics;

// Mini-Windows loads and discards segments while preparing Windows 95 Setup.
// Record protection faults without stopping normal demand loading, then retain
// screenshots and paired disk/snapshot checkpoints around the failing action.
if (args.Length is not (3 or 4))
{
    Console.Error.WriteLine("usage: Win95SetupProbe <DOS disk> <Windows 95 ISO> <new output directory> [snapshot]");
    Console.Error.WriteLine("   or: Win95SetupProbe --vm <VM directory> <new output directory> [snapshot]");
    return 2;
}

VmConfiguration configuration;
if (args[0] == "--vm")
{
    configuration = VmConfiguration.Load(args[1]);
}
else
{
    configuration = new VmConfiguration
    {
        Disks = [new() { Path = Path.GetFullPath(args[0]), Drive = 0x80 }],
        CompactDisc = Path.GetFullPath(args[1])
    };
}
if (configuration.Disks.Count != 1 || configuration.Disks[0].Drive != 0x80)
{
    throw new ArgumentException("This probe requires one primary hard disk.");
}

string outputDirectory = Path.GetFullPath(args[2]);
Directory.CreateDirectory(outputDirectory);
string workingDiskPath = Path.Combine(outputDirectory, "disk.img");
File.Copy(configuration.Disks[0].Path, workingDiskPath, overwrite: false);
configuration.Disks[0].Path = workingDiskPath;
for (int index = 0; index < configuration.Floppies.Count; index++)
{
    string floppyCopy = Path.Combine(outputDirectory, $"floppy-{index}.img");
    File.Copy(configuration.Floppies[index], floppyCopy, overwrite: false);
    configuration.Floppies[index] = floppyCopy;
}
configuration.Save(outputDirectory);
using Machine machine = configuration.BuildMachine();
if (args.Length == 4)
{
    MachineSnapshot.Load(args[3]).RestoreTo(machine);
}

using var faultLog = new StreamWriter(Path.Combine(outputDirectory, "faults.log"));
faultLog.AutoFlush = true;
var recordedFaults = new HashSet<string>();
long instructionsRun = 0;
machine.Cpu.Trace = new InstructionTrace();

// Task-file tracing excludes the data ports so sector transfers do not
// bury initialization commands. Polling the same status is recorded once.
using var ideLog = new StreamWriter(Path.Combine(outputDirectory, "ide.log"));
ideLog.AutoFlush = true;
if (Environment.GetEnvironmentVariable("AURORA_IDE_LOG") == "1")
{
    (ushort Port, bool Write, byte Value)? previousAccess = null;
    int recordedAccesses = 0;
    bool traceReadProbe = Environment.GetEnvironmentVariable("AURORA_IDE_TRACE") == "1";
    bool tracingRead = false;
    uint driverCodeBase = 0;
    machine.Io.PortAccessed += (port, write, value) =>
    {
        if (traceReadProbe && write && port == 0x1F7 && value == 0x20)
        {
            driverCodeBase = machine.Cpu.State.Eip & 0xFFFF0000;
            machine.Cpu.Trace = new InstructionTrace(524_288) { Enabled = true };
            tracingRead = true;
        }
        else if (tracingRead && write && port == 0x1F6)
        {
            foreach (InstructionTrace.Entry entry in machine.Cpu.Trace.Entries())
            {
                if (entry.Cs == machine.Cpu.State.Cs.Selector
                    && entry.Eip >= driverCodeBase && entry.Eip - driverCodeBase < 0x10000)
                {
                    ideLog.WriteLine(entry);
                }
            }
            machine.Cpu.Trace.Enabled = false;
            tracingRead = false;
            traceReadProbe = false;
        }
        bool taskFilePort = port is >= 0x1F1 and <= 0x1F7
            or >= 0x171 and <= 0x177 or 0x3F6 or 0x376;
        if (!taskFilePort || recordedAccesses >= 100_000)
        {
            return;
        }
        var access = (port, write, value);
        if (!write && previousAccess == access)
        {
            return;
        }
        previousAccess = access;
        recordedAccesses++;
        CpuState state = machine.Cpu.State;
        string direction = write ? "W" : "R";
        ideLog.WriteLine($"{state.Cs.Selector:X4}:{state.Eip:X8} {direction} {port:X4}={value:X2}");
    };
}

using var videoLog = new StreamWriter(Path.Combine(outputDirectory, "video.log"));
videoLog.AutoFlush = true;
bool traceVideo = Environment.GetEnvironmentVariable("AURORA_VIDEO_LOG") == "1";
int videoAccesses = 0;
machine.Cpu.SoftwareInterruptIssued += interrupt =>
{
    if (traceVideo && interrupt == 0x10)
    {
        CpuState state = machine.Cpu.State;
        videoLog.WriteLine($"INT10 {state.Cs.Selector:X4}:{state.Eip:X8} AX={state.Ax:X4} BX={state.Bx:X4} CX={state.Cx:X4} DX={state.Dx:X4}");
    }
};
if (machine.TrioAccelerator is { } trioAccelerator)
{
    trioAccelerator.CommandTrace = (command, x, y, width, height, colour, mix, control) =>
    {
        if (traceVideo)
        {
            videoLog.WriteLine($"S3 {command:X4} src={x},{y} size={width},{height} fg={colour:X4} mix={mix:X4} pc={control:X4}");
        }
    };
}
machine.Io.PortAccessed += (port, write, value) =>
{
    if (!traceVideo || port < 0x3B0 || port > 0x3DF)
    {
        return;
    }
    if (!write && port is 0x3DA or 0x3BA)
    {
        return;
    }
    if (videoAccesses >= 100_000)
    {
        return;
    }
    videoAccesses++;
    string direction = write ? "W" : "R";
    videoLog.WriteLine($"{machine.Cpu.State.Cs.Selector:X4}:{machine.Cpu.State.Eip:X8} {direction} {port:X4}={value:X2}");
};

if (Environment.GetEnvironmentVariable("AURORA_VIDEO_MEMORY") == "1")
{
    int memoryAccesses = 0;
    machine.Memory.SetWatchRanges([(0xA0000, 80 * 34)]);
    machine.Memory.Watched += (address, value) =>
    {
        uint column = (address - 0xA0000) % 80;
        if (!traceVideo || column < 2 || column > 7 || memoryAccesses++ >= 5000)
        {
            return;
        }
        CpuState state = machine.Cpu.State;
        var registers = machine.VideoRegisters;
        videoLog.WriteLine($"MEM {state.Cs.Selector:X4}:{state.Eip:X8} {address:X8}={value:X2} AX={state.Eax:X8} SI={state.Esi:X8} DI={state.Edi:X8} DS={state.Ds.Base:X8} ES={state.Es.Base:X8} GC={Convert.ToHexString(Enumerable.Range(0, 9).Select(registers.Graphics).ToArray())} SEQ={registers.MapMask:X2} CODE={ReadBytes(state.Cs.Base + state.Eip - 8, 24)} SOURCE={ReadBytes(state.Ds.Base + state.Si, 16)}");
    };
}

string ReadBytes(uint linearAddress, int length)
{
    var bytes = new byte[length];
    for (int index = 0; index < length; index++)
    {
        bytes[index] = machine.Cpu.PeekLinear(linearAddress + (uint)index);
    }
    return Convert.ToHexString(bytes);
}

void RecordSegment(string name, SegmentCache segment)
{
    faultLog.WriteLine($"{name}={segment.Selector:X4} base={segment.Base:X8} limit={segment.Limit:X8} DPL={segment.Dpl} big={segment.Big}");
}

machine.Cpu.ProcessorException += (vector, errorCode, instructionOffset, protectedMode) =>
{
    CpuState state = machine.Cpu.State;
    if (!protectedMode || state.Virtual8086Mode || vector is not (6 or 11 or 12 or 13 or 14))
    {
        return;
    }

    // Repeated faults in an idle guest should not fill the host disk.
    string identity = $"{vector}:{errorCode}:{state.Cs.Selector}:{instructionOffset}:{state.Cr3}";
    if (recordedFaults.Count >= 2000 || !recordedFaults.Add(identity))
    {
        return;
    }

    faultLog.WriteLine($"#{vector} error={errorCode:X8} {state.Cs.Selector:X4}:{instructionOffset:X8} approxInstructions={instructionsRun} CR0={state.Cr0:X8} CR3={state.Cr3:X8}");
    faultLog.WriteLine($"AX={state.Eax:X8} BX={state.Ebx:X8} CX={state.Ecx:X8} DX={state.Edx:X8} SI={state.Esi:X8} DI={state.Edi:X8} BP={state.Ebp:X8} SP={state.Esp:X8} FLAGS={machine.Cpu.Flags.Value:X8}");
    RecordSegment("CS", state.Cs);
    RecordSegment("SS", state.Ss);
    RecordSegment("DS", state.Ds);
    RecordSegment("ES", state.Es);
    RecordSegment("FS", state.Fs);
    RecordSegment("GS", state.Gs);
    uint stackOffset = state.Ss.Big ? state.Esp : state.Sp;
    faultLog.WriteLine($"CODE {ReadBytes(state.Cs.Base + instructionOffset, 32)} STACK {ReadBytes(state.Ss.Base + stackOffset, 64)}");
    if (vector == 14)
    {
        faultLog.WriteLine($"CR2={state.Cr2:X8}");
    }
    if (machine.Cpu.Trace.Enabled)
    {
        foreach (InstructionTrace.Entry entry in machine.Cpu.Trace.Last(128))
        {
            faultLog.WriteLine(entry);
        }
    }
    faultLog.Flush();
};

void Run(long instructionCount)
{
    for (long completed = 0; completed < instructionCount; completed += 500_000)
    {
        long batchSize = Math.Min(500_000, instructionCount - completed);
        machine.RunInstructions(batchSize);
        instructionsRun += batchSize;
        machine.Video.Render();
    }
}

void PressKey(byte scanCode, bool shift = false)
{
    if (shift)
    {
        machine.Keyboard.PushScanCode(0x2A);
    }
    machine.Keyboard.PushScanCode(scanCode);
    Run(200_000);
    machine.Keyboard.PushScanCode((byte)(scanCode | 0x80));
    if (shift)
    {
        machine.Keyboard.PushScanCode(0xAA);
    }
    Run(500_000);
}

bool TypeText(string command)
{
    const string letters = "qwertyuiopasdfghjklzxcvbnm";
    byte[] scanCodes =
    [
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19,
        0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26,
        0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32
    ];
    // Preserve the reference DOS disk's KEYB IT punctuation by default.
    // Windows guests with a US layout can select it without changing the disk.
    bool usKeyboard = string.Equals(Environment.GetEnvironmentVariable("AURORA_KEYBOARD_LAYOUT"),
        "us", StringComparison.OrdinalIgnoreCase);
    var keys = new List<(byte ScanCode, bool Shift)>();
    foreach (char character in command.ToLowerInvariant())
    {
        int letterIndex = letters.IndexOf(character);
        if (letterIndex >= 0)
        {
            keys.Add((scanCodes[letterIndex], false));
        }
        else if (character is >= '1' and <= '9')
        {
            keys.Add(((byte)(2 + character - '1'), false));
        }
        else
        {
            (byte ScanCode, bool Shift)? key = character switch
            {
                '0' => (0x0B, false),
                ' ' => (0x39, false),
                ':' => ((byte)(usKeyboard ? 0x27 : 0x34), true),
                '.' => (0x34, false),
                '\\' => ((byte)(usKeyboard ? 0x2B : 0x29), false),
                '/' => ((byte)(usKeyboard ? 0x35 : 0x08), !usKeyboard),
                _ => null
            };
            if (key is null)
            {
                Console.Error.WriteLine($"Unsupported character: {character}. Text was not sent to the guest.");
                return false;
            }
            keys.Add(key.Value);
        }
    }

    // Validate the complete string before injecting any key. A typo must
    // leave the running guest and its unsaved checkpoint available.
    foreach ((byte scanCode, bool shift) in keys)
    {
        PressKey(scanCode, shift);
    }
    return true;
}

string OutputPath(string name, string extension)
{
    if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name))
    {
        throw new ArgumentException("Use a plain filename.", nameof(name));
    }
    return Path.Combine(outputDirectory, name + extension);
}

void SaveScreenshot(string name)
{
    machine.Video.Render();
    uint[] incremental = machine.Video.Framebuffer.ToArray();
    machine.Video.Render(true);
    int differences = incremental.Zip(machine.Video.Framebuffer.ToArray()).Count(pair => pair.First != pair.Second);
    Console.WriteLine($"Video: {machine.Video.Width}x{machine.Video.Height}, mode={machine.VideoRegisters.EffectiveMode}, Trio operations={machine.TrioAccelerator?.OperationsPerformed.ToString() ?? "n/a"}, incremental differences={differences}");
    using var screenshot = File.Create(OutputPath(name, ".ppm"));
    screenshot.Write(Encoding.ASCII.GetBytes($"P6\n{machine.Video.Width} {machine.Video.Height}\n255\n"));
    foreach (uint pixel in machine.Video.Framebuffer)
    {
        screenshot.WriteByte((byte)pixel);
        screenshot.WriteByte((byte)(pixel >> 8));
        screenshot.WriteByte((byte)(pixel >> 16));
    }

    // Text memory remains useful at the DOS prompt and preliminary setup steps.
    for (int row = 0; row < 25; row++)
    {
        var line = new char[80];
        for (int column = 0; column < line.Length; column++)
        {
            byte character = machine.Memory.ReadByte((uint)(0xB8000 + (row * 80 + column) * 2));
            line[column] = character is >= 32 and < 127 ? (char)character : ' ';
        }
        Console.WriteLine(new string(line).TrimEnd());
    }
    Console.WriteLine($"image: {name}.ppm");
}

void SaveCheckpoint(string name)
{
    // No guest instructions run while these files are written. Restoring just
    // the snapshot would leave the installer looking at later disk contents.
    MachineSnapshot.Capture(machine).Save(OutputPath(name, ".snapshot"));
    File.Copy(workingDiskPath, OutputPath(name, ".img"), overwrite: false);
}

void PrintVgaState()
{
    S3VgaRegisters registers = machine.S3Registers;
    string mode = registers.VesaMode?.Number.ToString("X4") ?? "none";
    Console.WriteLine($"VGA: mode={mode}, bank={registers.VesaWindowBank}, "
        + $"cpu-window={registers.CpuWindowOffset:X8}, "
        + $"cursor-pattern={registers.CursorPatternAddress:X8}, "
        + $"trio-operations={machine.TrioAccelerator?.OperationsPerformed.ToString() ?? "n/a"}");
    // A CR45 hardware read resets the colour stacks' shared pointer. Read
    // that one byte from the snapshot record so diagnostics preserve state.
    const int hardwareCursorModeRegister = 0x45;
    byte hardwareCursorMode = registers.CaptureRegisters()[hardwareCursorModeRegister];
    for (int registerIndex = 0x30; registerIndex <= 0x6F; registerIndex++)
    {
        byte value = registerIndex == hardwareCursorModeRegister
            ? hardwareCursorMode
            : registers.Crtc(registerIndex);
        Console.WriteLine($"CRTC[{registerIndex:X2}]={value:X2}");
    }

    uint cursorPatternAddress = registers.CursorPatternAddress;
    var cursorPattern = new byte[32];
    for (int index = 0; index < cursorPattern.Length; index++)
    {
        cursorPattern[index] = machine.VideoMemory.ReadLinear(cursorPatternAddress + (uint)index);
    }
    Console.WriteLine($"cursor-pattern-bytes={Convert.ToHexString(cursorPattern)}");
}

Console.WriteLine("Commands: run, type, text, key, move, drag, click, "
    + "video on/off, watch, dump, vram, vga state, trace, shot, save, quit");
string? input;
while ((input = Console.ReadLine()) is not null)
{
    string[] parts = input.Split(' ', 2);
    if (parts[0] == "quit")
    {
        return 0;
    }
    if (parts.Length != 2)
    {
        Console.WriteLine("This command requires an argument.");
        continue;
    }
    switch (parts[0])
    {
        case "run": Run(long.Parse(parts[1])); break;
        case "watch":
        {
            string[] values = parts[1].Split(' ');
            uint linear = Convert.ToUInt32(values[0], 16);
            uint length = Convert.ToUInt32(values[1], 16);
            var ranges = new List<(uint Start, uint Length)>();
            for (uint offset = 0; offset < length; offset += 4096)
            {
                uint address = linear + offset;
                uint directory = machine.Memory.ReadDword((machine.Cpu.State.Cr3 & 0xFFFFF000) + ((address >> 22) * 4));
                uint table = machine.Memory.ReadDword((directory & 0xFFFFF000) + (((address >> 12) & 0x3FF) * 4));
                ranges.Add(((table & 0xFFFFF000) | (address & 0xFFF), Math.Min(4096u, length - offset)));
                Console.WriteLine($"Watch {address:X8} -> {ranges[^1].Start:X8}");
            }
            machine.Memory.SetWatchRanges(ranges);
            var counts = new Dictionary<string, int>();
            machine.Memory.Watched += (address, value) =>
            {
                CpuState state = machine.Cpu.State;
                string location = $"{state.Cs.Selector:X4}:{state.Eip:X8}";
                counts.TryGetValue(location, out int count);
                counts[location] = count + 1;
                if (count >= 32)
                {
                    return;
                }
                videoLog.WriteLine($"RAM {location} {address:X8}={value:X2} AX={state.Eax:X8} BX={state.Ebx:X8} CX={state.Ecx:X8} DX={state.Edx:X8} SI={state.Esi:X8} DI={state.Edi:X8} DS={state.Ds.Base:X8} ES={state.Es.Base:X8} CODE={ReadBytes(state.Cs.Base + state.Eip - 16, 32)} SOURCE={ReadBytes(state.Ds.Base + state.Si, 32)}");
            };
            break;
        }
        case "dump":
        {
            string[] values = parts[1].Split(' ');
            uint address = Convert.ToUInt32(values[0], 16);
            int length = Convert.ToInt32(values[1], 16);
            File.WriteAllBytes(OutputPath("memory-" + values[0], ".bin"), Convert.FromHexString(ReadBytes(address, length)));
            break;
        }
        case "vram":
        {
            string[] values = parts[1].Split(' ');
            uint address = Convert.ToUInt32(values[0], 16);
            int length = Convert.ToInt32(values[1], 16);
            var bytes = new byte[length];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = machine.VideoMemory.ReadLinear(address + (uint)index);
            }
            File.WriteAllBytes(OutputPath("vram-" + values[0], ".bin"), bytes);
            break;
        }
        case "video":
            traceVideo = parts[1] == "on";
            videoAccesses = 0;
            break;
        case "vga" when parts[1] == "state":
            PrintVgaState();
            break;
        case "trace":
            machine.Cpu.Trace.Enabled = parts[1] switch
            {
                "on" => true,
                "off" => false,
                _ => throw new ArgumentException("Use trace on or trace off.")
            };
            break;
        case "type":
            if (TypeText(parts[1]))
            {
                PressKey(0x1C);
            }
            break;
        case "text": TypeText(parts[1]); break;
        case "key": PressKey(Convert.ToByte(parts[1], 16)); break;
        case "move":
        {
            string[] coordinates = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (coordinates.Length != 2)
            {
                throw new ArgumentException("Use move <dx> <dy> in mouse units.");
            }
            machine.MoveMouse(int.Parse(coordinates[0]), int.Parse(coordinates[1]));
            Run(700_000);
            break;
        }
        case "drag":
        {
            string[] coordinates = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (coordinates.Length != 2)
            {
                throw new ArgumentException("Use drag <dx> <dy> in mouse units.");
            }
            machine.SetMouseButton(MouseButtons.Left, true);
            Run(200_000);
            machine.MoveMouse(int.Parse(coordinates[0]), int.Parse(coordinates[1]));
            Run(700_000);
            machine.SetMouseButton(MouseButtons.Left, false);
            Run(500_000);
            break;
        }
        case "click":
        {
            MouseButtons button = parts[1] switch
            {
                "left" => MouseButtons.Left,
                "right" => MouseButtons.Right,
                _ => throw new ArgumentException("Use click left or click right.")
            };
            machine.SetMouseButton(button, true);
            Run(200_000);
            machine.SetMouseButton(button, false);
            Run(500_000);
            break;
        }
        case "shot": SaveScreenshot(parts[1]); break;
        case "save": SaveCheckpoint(parts[1]); break;
        default: Console.WriteLine("Unknown command."); break;
    }
    Console.WriteLine($"ran {instructionsRun}; {machine.Cpu.State.Cs.Selector:X4}:{machine.Cpu.State.Eip:X8}; faults={recordedFaults.Count}");
}
return 0;
