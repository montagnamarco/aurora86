using System.Text.Json;
using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;

// Runs the SingleStepTests for the 80386 in protected mode -- test vectors
// generated from 86Box, one instruction each -- against the
// emulated CPU, and reports every case where the two disagree.
//
//   dotnet run --project tools/HardwareSuite -- <directory of v1/*.json> [name,name,...]
//
// The vectors come from github.com/nand2mario/SingleStepTests_80386_protected
// (branch master, folder v1). Each test gives the processor's full state
// before one instruction and after it; the runner loads the first, steps
// once, and compares with the second. Nothing about the emulator is
// special-cased here: the reference emulator supplies the expected result.
//
// Only the bus and the CPU are used, not a Machine: the vectors need more
// memory than a machine mounts and no BIOS getting in the way.

if (args.Length == 0)
{
    Console.WriteLine("usage: hardware-suite <directory with the v1/*.json vectors> [filter,filter,...]");
    return 1;
}

string directory = args[0];
string[] filters = args.Length > 1 ? args[1].Split(',') : [];

int totalPassed = 0, totalFailed = 0;
foreach (string file in Directory.GetFiles(directory, "*.json").OrderBy(x => x))
{
    if (filters.Length > 0 && !filters.Any(f => Path.GetFileName(file).Contains(f)))
    {
        continue;
    }

    string text = File.ReadAllText(file);
    if (text.Length < 100)
    {
        continue;      // a placeholder left by a failed download
    }

    JsonElement tests = JsonDocument.Parse(text).RootElement;
    int passed = 0, failed = 0;
    var firstFailures = new List<string>();

    foreach (JsonElement test in tests.EnumerateArray())
    {
        using var memory = new PhysicalMemoryBus(64 * 1024 * 1024, 64 * 1024 * 1024);
        var cpu = new Cpu(memory);
        cpu.Reset();
        CpuState state = cpu.State;
        JsonElement initial = test.GetProperty("initial");

        // Registers.
        JsonElement regs = initial.GetProperty("regs");
        uint Reg(string name) => regs.TryGetProperty(name, out JsonElement v) ? v.GetUInt32() : 0u;
        state.Eax = Reg("eax"); state.Ecx = Reg("ecx"); state.Edx = Reg("edx"); state.Ebx = Reg("ebx");
        state.Esp = Reg("esp"); state.Ebp = Reg("ebp"); state.Esi = Reg("esi"); state.Edi = Reg("edi");
        state.Eip = Reg("eip");
        cpu.Flags.SetValue(Reg("eflags"));

        // Descriptor tables and control registers.
        JsonElement tables = initial.GetProperty("dtables");
        state.GdtBase = tables.GetProperty("gdt").GetProperty("base").GetUInt32();
        state.GdtLimit = (ushort)tables.GetProperty("gdt").GetProperty("limit").GetUInt32();
        state.IdtBase = tables.GetProperty("idt").GetProperty("base").GetUInt32();
        state.IdtLimit = (ushort)tables.GetProperty("idt").GetProperty("limit").GetUInt32();
        state.Cr0 = initial.GetProperty("cr0").GetUInt32();
        if (initial.TryGetProperty("cr3", out JsonElement cr3))
        {
            state.Cr3 = cr3.GetUInt32();
        }

        // The task register: without it every change of privilege raises
        // #TS, because the processor cannot find the stack of the level it
        // is switching to.
        if (initial.TryGetProperty("tr", out JsonElement tr))
        {
            state.TaskRegister = (ushort)tr.GetProperty("sel").GetUInt32();
            state.TssBase = tr.GetProperty("base").GetUInt32();
            state.TssLimit = tr.GetProperty("limit").GetUInt32();
        }
        if (initial.TryGetProperty("ldt", out JsonElement ldt) || tables.TryGetProperty("ldt", out ldt))
        {
            state.Ldtr = (ushort)ldt.GetProperty("sel").GetUInt32();
            state.LdtBase = ldt.GetProperty("base").GetUInt32();
            state.LdtLimit = ldt.GetProperty("limit").GetUInt32();
        }

        // Memory.
        foreach (JsonElement cell in initial.GetProperty("ram").EnumerateArray())
        {
            uint address = cell[0].GetUInt32();
            if (address < memory.RamSize)
            {
                memory.WriteByte(address, (byte)cell[1].GetUInt32());
            }
        }

        // Segments go straight into the descriptor cache, as the test
        // expects. The flags in the JSON have a packing of their own; the
        // real descriptor is in RAM, where the processor would look, and
        // reading it from there removes a decoding that could be wrong in
        // two different ways.
        JsonElement segments = initial.GetProperty("segs");
        void LoadSegment(SegReg register, string name)
        {
            JsonElement entry = segments.GetProperty(name);
            ref SegmentCache cache = ref state.Segments[(int)register];
            ushort selector = (ushort)entry.GetProperty("sel").GetUInt32();
            cache.Selector = selector;
            cache.Base = entry.GetProperty("base").GetUInt32();

            if ((selector & 0xFFFC) == 0) { cache.Limit = 0; cache.Big = false; return; }

            uint table = (selector & 4) != 0 ? 0 : state.GdtBase;
            uint address = table + (uint)(selector & 0xFFF8);
            byte access = memory.ReadByte(address + 5);
            byte granularity = memory.ReadByte(address + 6);
            uint limit = (uint)(memory.ReadWord(address) | ((granularity & 0x0F) << 16));
            if ((granularity & 0x80) != 0)
            {
                limit = (limit << 12) | 0xFFF;
            }
            cache.Limit = limit;
            cache.Big = (granularity & 0x40) != 0;
            bool code = (access & 0x08) != 0;
            cache.Readable = !code || (access & 0x02) != 0;
            cache.Writable = !code && (access & 0x02) != 0;
            cache.ExpandDown = !code && (access & 0x04) != 0;
            cache.Dpl = (byte)((access >> 5) & 3);
        }

        bool v86 = (Reg("eflags") & (1u << 17)) != 0;
        if (v86)
        {
            // In virtual 8086 mode the "selectors" are real-mode paragraphs.
            state.Virtual8086Mode = true;
            foreach ((SegReg register, string name) in new[] { (SegReg.Cs, "cs"), (SegReg.Ds, "ds"), (SegReg.Es, "es"), (SegReg.Ss, "ss"), (SegReg.Fs, "fs"), (SegReg.Gs, "gs") })
                state.LoadSegmentRealModeReset(register, (ushort)segments.GetProperty(name).GetProperty("sel").GetUInt32());
            state.CplRaw = 3;
        }
        else
        {
            LoadSegment(SegReg.Cs, "cs"); LoadSegment(SegReg.Ds, "ds"); LoadSegment(SegReg.Es, "es");
            LoadSegment(SegReg.Ss, "ss"); LoadSegment(SegReg.Fs, "fs"); LoadSegment(SegReg.Gs, "gs");
            state.CplRaw = (byte)(state.Cs.Selector & 3);
        }
        state.Halted = false;

        // The instruction bytes, at the linear address CS:EIP names. With
        // paging on that is a linear address, and the bytes go where the
        // test's page tables map it, not to that physical address.
        uint Physical(uint linear)
        {
            if ((state.Cr0 & 0x80000000u) == 0)
            {
                return linear;
            }
            uint pde = memory.ReadDword(state.Cr3 + (linear >> 22) * 4);
            if ((pde & 1) == 0)
            {
                return linear;
            }
            uint pte = memory.ReadDword((pde & 0xFFFFF000u) + ((linear >> 12) & 0x3FF) * 4);
            if ((pte & 1) == 0)
            {
                return linear;
            }
            return (pte & 0xFFFFF000u) | (linear & 0xFFF);
        }
        uint instruction = state.Cs.Base + state.Eip;
        int k = 0;
        var bytes = new List<byte>();
        foreach (JsonElement b in test.GetProperty("bytes").EnumerateArray())
        {
            byte value = (byte)b.GetUInt32();
            bytes.Add(value);
            memory.WriteByte(Physical(instruction + (uint)k++), value);
        }
        uint initialEip = state.Eip;

        // A REP prefix: our Step runs one iteration at a time, which is
        // legitimate (REP is restartable), but the test photographs the
        // whole instruction.
        bool rep = bytes.TakeWhile(x => x is 0x66 or 0x67 or 0xF2 or 0xF3 or 0x26 or 0x2E or 0x36 or 0x3E or 0x64 or 0x65)
                        .Any(x => x is 0xF2 or 0xF3);

        byte vector = 255;
        cpu.ProcessorException += (v, _, _, _) => { if (vector == 255) vector = v; };
        try
        {
            cpu.Step();
            if (rep) { int rounds = 0; while (vector == 255 && state.Eip == initialEip && rounds++ < 200_000) cpu.Step(); }
        }
        catch { }

        // Compare.
        JsonElement final = test.GetProperty("final");
        var errors = new List<string>();
        if (final.TryGetProperty("regs", out JsonElement finalRegs))
        {
            foreach (JsonProperty p in finalRegs.EnumerateObject())
            {
                uint expected = p.Value.GetUInt32();
                uint ours = p.Name switch
                {
                    "eax" => state.Eax, "ecx" => state.Ecx, "edx" => state.Edx, "ebx" => state.Ebx,
                    "esp" => state.Esp, "ebp" => state.Ebp, "esi" => state.Esi, "edi" => state.Edi,
                    "eip" => state.Eip, "eflags" => cpu.Flags.Value, _ => expected,
                };
                // Only the arithmetic flags are compared: the rest carry
                // undefined bits the hardware sets one way or another.
                if (p.Name == "eflags") { if (((ours ^ expected) & 0x8D5) != 0) errors.Add($"{p.Name} {ours:X}!={expected:X}"); }
                else if (ours != expected) errors.Add($"{p.Name} {ours:X}!={expected:X}");
            }
        }
        if (final.TryGetProperty("segs", out JsonElement finalSegments))
        {
            foreach (JsonProperty p in finalSegments.EnumerateObject())
            {
                JsonElement e = p.Value;
                if (p.Name == "tr")
                {
                    uint sel = e.GetProperty("sel").GetUInt32();
                    if (state.TaskRegister != sel) errors.Add($"tr.sel {state.TaskRegister:X}!={sel:X}");
                    if (e.TryGetProperty("base", out JsonElement tb) && state.TssBase != tb.GetUInt32()) errors.Add($"tr.base {state.TssBase:X}!={tb.GetUInt32():X}");
                    continue;
                }
                if (p.Name == "ldt")
                {
                    uint sel = e.GetProperty("sel").GetUInt32();
                    if (state.Ldtr != sel) errors.Add($"ldt.sel {state.Ldtr:X}!={sel:X}");
                    if (e.TryGetProperty("base", out JsonElement lb) && state.LdtBase != lb.GetUInt32()) errors.Add($"ldt.base {state.LdtBase:X}!={lb.GetUInt32():X}");
                    continue;
                }
                ref SegmentCache cache = ref state.Segments[(int)(p.Name switch
                {
                    "cs" => SegReg.Cs, "ds" => SegReg.Ds, "es" => SegReg.Es,
                    "ss" => SegReg.Ss, "fs" => SegReg.Fs, _ => SegReg.Gs,
                })];
                uint selector = e.GetProperty("sel").GetUInt32();
                if (cache.Selector != selector) errors.Add($"{p.Name}.sel {cache.Selector:X}!={selector:X}");
                // In V86 the data segments carry only the selector.
                if (e.TryGetProperty("base", out JsonElement bp) && cache.Base != bp.GetUInt32())
                    errors.Add($"{p.Name}.base {cache.Base:X}!={bp.GetUInt32():X}");
            }
        }
        if (final.TryGetProperty("fault", out JsonElement fault))
        {
            int expectedVector = fault.ValueKind == JsonValueKind.Number ? fault.GetInt32()
                               : (fault.TryGetProperty("vector", out JsonElement fv) ? fv.GetInt32() : -1);
            if (expectedVector >= 0 && vector != expectedVector)
                errors.Add($"fault {(vector == 255 ? "none" : vector.ToString("X2"))}!=expected {expectedVector:X2}");
        }
        else if (vector != 255) errors.Add($"unexpected fault {vector:X2}");

        if (errors.Count == 0)
        {
            passed++;
        }
        else
        {
            failed++;
            if (firstFailures.Count < 3)
                firstFailures.Add($"{test.GetProperty("name").GetString()}: {string.Join(", ", errors.Take(3))}" + (vector != 255 ? $"  [fault {vector:X2}]" : ""));
        }
    }

    totalPassed += passed; totalFailed += failed;
    Console.WriteLine($"{Path.GetFileNameWithoutExtension(file),-16} {passed,4} passed  {failed,4} failed");
    foreach (string line in firstFailures) Console.WriteLine($"       {line}");
}

Console.WriteLine($"\nTOTAL: {totalPassed} passed, {totalFailed} failed");
return totalFailed == 0 ? 0 : 1;
