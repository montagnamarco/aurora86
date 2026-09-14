using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Storage;

return VirgeProbe.Run(args);

/// <summary>
/// Observes the 1996 S3d DOS cube sample through its original DOS/4GW runtime.
/// The guest submits all commands; this service only supplies keyboard input
/// and records CPU, bus and display evidence on a private disk copy.
/// </summary>
internal static class VirgeProbe
{
    private const long BootBudget = 400_000_000;
    private const long SampleBudget = 80_000_000;
    private const int TraceLimitPerCategory = 256;
    private const int MmioWriteLimit = 8192;
    private const int LowMmioWriteLimit = 64;
    private const uint MmioRegisterStart = 0x8000;

    public static int Run(string[] args)
    {
        if (args.Length is not (2 or 3) || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: Virge3DProbe <prepared DOS image> <new output folder> [paired prompt snapshot]");
            return 2;
        }

        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output))
        {
            Console.Error.WriteLine("The output folder must be new; existing evidence is never overwritten.");
            return 2;
        }

        if (args.Length == 3)
        {
            string manifestPath = Path.ChangeExtension(args[2], ".json");
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement metadata = manifest.RootElement;
            if (metadata.GetProperty("diskSha256").GetString() != HashFile(args[0])
                || metadata.GetProperty("snapshotSha256").GetString() != HashFile(args[2])
                || metadata.GetProperty("coreMvid").GetString() != typeof(Machine).Module.ModuleVersionId.ToString())
            {
                Console.Error.WriteLine("The disk, snapshot or Core build does not match the checkpoint manifest.");
                return 2;
            }
        }

        Directory.CreateDirectory(output);
        string disk = Path.Combine(output, "run.img");
        File.Copy(args[0], disk);
        using var log = new StreamWriter(Path.Combine(output, "trace.log")) { AutoFlush = true };
        using var mmioLog = new StreamWriter(Path.Combine(output, "mmio-writes.log"));
        using var machine = new Machine(videoModel: VmVideoModel.S3Virge325);
        machine.CpuModel = CpuModel.Intel486;
        machine.AttachDisk(DiskImage.OpenHardDisk(disk));
        log.WriteLine($"source-sha256={HashFile(args[0])}");
        log.WriteLine($"core-mvid={typeof(Machine).Module.ModuleVersionId} cpu=Intel486 ram=16MB video=S3Virge325");

        if (args.Length == 3)
        {
            MachineSnapshot.Load(args[2]).RestoreTo(machine);
        }
        else
        {
            machine.RunInstructions(BootBudget);
        }

        string prompt = ReadScreen(machine);
        File.WriteAllText(Path.Combine(output, "prompt.txt"), prompt);
        if (!prompt.Contains(@"C:\>"))
        {
            log.WriteLine("No root DOS prompt found. Sample was not launched.");
            return 3;
        }

        // A checkpoint always travels with the disk at the same boundary.
        // Subsequent runs receive prompt.img and prompt.snapshot together.
        string snapshotPath = Path.Combine(output, "prompt.snapshot");
        string promptDiskPath = Path.Combine(output, "prompt.img");
        MachineSnapshot.Capture(machine).Save(snapshotPath);
        File.Copy(disk, promptDiskPath);
        var checkpoint = new
        {
            diskSha256 = HashFile(promptDiskPath),
            snapshotSha256 = HashFile(snapshotPath),
            coreMvid = typeof(Machine).Module.ModuleVersionId.ToString()
        };
        File.WriteAllText(Path.Combine(output, "prompt.json"),
            JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true }));
        log.WriteLine($"prompt-disk-sha256={checkpoint.diskSha256}");

        var traceCounts = new Dictionary<string, long>();
        var observedAccesses = new HashSet<string>();
        long interruptCount = 0;
        long faultCount = 0;
        long unsupportedCount = 0;
        long mmioWriteCount = 0;
        long lowMmioWriteCount = 0;
        long registerWriteCount = 0;
        machine.Virge!.MmioWriteObserved += access =>
        {
            mmioWriteCount++;
            // Image uploads in the lower window must not exhaust the
            // register budget before the first triangle submission.
            bool retain;
            if (access.Register < MmioRegisterStart)
            {
                lowMmioWriteCount++;
                retain = lowMmioWriteCount <= LowMmioWriteLimit;
            }
            else
            {
                registerWriteCount++;
                retain = registerWriteCount <= MmioWriteLimit;
            }
            if (retain)
            {
                CpuState state = machine.Cpu.State;
                mmioLog.WriteLine($"{mmioWriteCount} {state.Cs.Selector:X4}:{state.Eip:X8} register={access.Register:X4} width={access.Width} value={access.Value:X8}");
            }
        };
        void Trace(string category, string message)
        {
            long count = traceCounts.GetValueOrDefault(category);
            traceCounts[category] = count + 1;
            if (count < TraceLimitPerCategory)
            {
                CpuState state = machine.Cpu.State;
                log.WriteLine($"{state.Cs.Selector:X4}:{state.Eip:X8} {message}");
            }
        }

        machine.Cpu.SoftwareInterruptIssued += vector =>
        {
            CpuState state = machine.Cpu.State;
            bool videoCall = vector == 0x10 && (state.Ax >> 8) != 0x0E;
            bool dpmiCall = vector == 0x31 && state.Ax is 0x0300 or 0x0501 or 0x0800;
            bool dosProcessCall = vector == 0x21 && (state.Ax >> 8) is 0x4B or 0x4C;
            if (videoCall || dpmiCall || dosProcessCall)
            {
                interruptCount++;
                Trace($"INT{vector:X2}", $"INT {vector:X2} AX={state.Ax:X4} BX={state.Bx:X4} CX={state.Cx:X4} DX={state.Dx:X4} SI={state.Esi:X8} DI={state.Edi:X8}");
            }
        };
        machine.Cpu.ProcessorException += (vector, error, address, protectedMode) =>
        {
            faultCount++;
            Trace("FAULT", $"FAULT {vector:X2} error={error:X8} at={address:X8} protected={protectedMode}");
        };
        machine.Virge!.UnsupportedAccess += access =>
        {
            unsupportedCount++;
            string key = $"unsupported:{machine.Cpu.State.Eip:X8}:{access.Reason}:{access.Offset >> 12:X}";
            if (observedAccesses.Add(key))
            {
                Trace("UNSUPPORTED", $"UNSUPPORTED offset={access.Offset:X8} width={access.Width} write={access.Write} reason={access.Reason}");
            }
        };
        machine.Virge.Engine.UnsupportedCommandIssued += command =>
        {
            unsupportedCount++;
            Trace("COMMAND", $"UNSUPPORTED CMD_SET={command:X8}");
        };

        // Do not install PhysicalMemoryBus write watches: they split DWORD
        // accesses into bytes, which changes the engine's command semantics.
        // Card diagnostics above retain the transaction width actually received.

        // CUBE and Enter use identical scan codes on Italian and US layouts.
        foreach (byte scanCode in new byte[] { 0x2E, 0x16, 0x30, 0x12, 0x1C })
        {
            machine.Keyboard.PushScanCode(scanCode);
            machine.RunInstructions(500_000);
            machine.Keyboard.PushScanCode((byte)(scanCode | 0x80));
            machine.RunInstructions(500_000);
        }

        machine.RunInstructions(SampleBudget);
        File.WriteAllText(Path.Combine(output, "after.txt"), ReadScreen(machine));
        SaveFrame(machine, Path.Combine(output, "after.ppm"), log);
        log.WriteLine($"sample-boundary={machine.Cpu.State.Cs.Selector:X4}:{machine.Cpu.State.Eip:X8}");

        // Esc is the unmodified sample's exit command. Preserve the earlier
        // frame separately so returning to text cannot masquerade as rendering.
        machine.Keyboard.PushScanCode(0x01);
        machine.RunInstructions(500_000);
        machine.Keyboard.PushScanCode(0x81);
        machine.RunInstructions(20_000_000);
        File.WriteAllText(Path.Combine(output, "exit.txt"), ReadScreen(machine));
        log.WriteLine($"observed-interrupts={interruptCount} faults={faultCount} unsupported={unsupportedCount}");
        mmioLog.WriteLine($"observed-writes={mmioWriteCount} register-writes={registerWriteCount} retained-registers={Math.Min(registerWriteCount, MmioWriteLimit)} register-limit={MmioWriteLimit}");
        mmioLog.WriteLine($"low-writes={lowMmioWriteCount} retained-low={Math.Min(lowMmioWriteCount, LowMmioWriteLimit)} low-limit={LowMmioWriteLimit}");
        CpuState final = machine.Cpu.State;
        log.WriteLine($"final={final.Cs.Selector:X4}:{final.Eip:X8} EAX={final.Eax:X8} ECX={final.Ecx:X8} EDI={final.Edi:X8} ESbase={final.Es.Base:X8}");
        log.WriteLine($"BAR={machine.LinearFrameBufferBase:X8} CR31={machine.VideoRegisters.Crtc(0x31):X2} CR36={machine.VideoRegisters.Crtc(0x36):X2} CR53={machine.VideoRegisters.Crtc(0x53):X2} CR58={machine.VideoRegisters.Crtc(0x58):X2} CR66={machine.VideoRegisters.Crtc(0x66):X2}");
        foreach ((string category, long count) in traceCounts)
        {
            log.WriteLine($"trace-category={category} events={count} limit={TraceLimitPerCategory}");
        }
        log.WriteLine($"source-sha256-after={HashFile(args[0])}");
        Console.WriteLine($"Observation saved to {output}; completion of the sample is not inferred from this probe's exit code.");
        return 0;
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ReadScreen(Machine machine)
    {
        var text = new StringBuilder();
        for (int row = 0; row < 25; row++)
        {
            var line = new char[80];
            for (int column = 0; column < 80; column++)
            {
                // Text plane access also remains readable if the guest disables PCI decoding.
                byte value = machine.VideoMemory.ReadPlane(0,
                    (uint)(0x8000 + machine.VideoRegisters.StartAddress + row * 80 + column));
                line[column] = value is >= 32 and < 127 ? (char)value : ' ';
            }
            text.AppendLine(new string(line).TrimEnd());
        }
        return text.ToString();
    }

    private static void SaveFrame(Machine machine, string path, StreamWriter log)
    {
        machine.Display.Render(true);
        byte[] rgb = new byte[machine.Display.Width * machine.Display.Height * 3];
        int offset = 0;
        foreach (uint pixel in machine.Display.Framebuffer)
        {
            rgb[offset++] = (byte)pixel;
            rgb[offset++] = (byte)(pixel >> 8);
            rgb[offset++] = (byte)(pixel >> 16);
        }
        using FileStream file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"P6\n{machine.Display.Width} {machine.Display.Height}\n255\n"));
        file.Write(rgb);
        log.WriteLine($"frame={machine.Display.Width}x{machine.Display.Height} rgb-sha256={Convert.ToHexString(SHA256.HashData(rgb))}");
    }
}
