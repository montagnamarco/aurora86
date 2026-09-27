using System.Collections.Concurrent;
using System.IO.Compression;
using Aurora386.Core.Cpu;
using Aurora386.Core.Memory;
using RealModeSuite;

// Runs the SingleStepTests 80386 real-mode vectors -- register-level
// captures taken from an actual Intel 386EX via the ArduinoX86 hardware
// interface -- against the emulated CPU, and reports every disagreement.
// This is the real-mode oracle the plan calls O1; the protected-mode oracle
// has its own runner in tools/HardwareSuite.
//
//   dotnet run --project tools/RealModeSuite -c Release -- \
//       [vector directory] [name,name,...]
//
// Vectors: https://github.com/dbalsom/SingleStepTests_80386 (v1_ex_real_mode,
// gzip-compressed MOO files) plus a revocation_list.txt of SHA-1 hashes for
// tests later found to be wrong, which are skipped rather than reported. The
// binary MOO format is parsed by MooReader.cs; see its header comment and
// https://github.com/dbalsom/moo for the chunk layout.
//
// Only the bus and the CPU are used, not a Machine: a test supplies its own
// full 16 MB of real-mode-addressable memory and does not want a BIOS in the
// way.
//
// --- What "final EIP" means, established from the vectors themselves ------
// Every test is the instruction under test followed by a HALT (0xF4) that
// the hardware used as a trigger to dump its post-instruction state (it
// enters System Management Mode on the HALT bus cycle). For straight-line
// code the HALT sits in RAM right after the instruction; for a jump, a call,
// or a fault the generator instead injected a HALT at the first fetch after
// the transfer, so the instruction bytes recorded there are not necessarily
// F4 -- only the one at the final EIP is.
//
// Comparing recorded final EIP against the instruction's own length shows it
// is not the address of that HALT but one past it, i.e. the HALT has itself
// been "executed" (its own single byte counted) before the state was
// captured:
//   - 90.MOO.gz (NOP;HLT): initial EIP -> +2 (1 for NOP, 1 for HLT).
//   - EB.MOO.gz (JMP short;HLT at target): final EIP is (target address)+1.
//   - CD.MOO.gz (INT n;HLT in the handler): final EIP is (handler entry)+1.
//   - F7.6.MOO.gz (DIV that faults;HLT in the #DE handler): same pattern.
//   - F4.MOO.gz (HLT is itself the tested instruction): final EIP is only
//     initial+1, i.e. no second HLT runs -- there is nothing after it to run.
// So the rule needs no per-instruction special case: run the tested
// instruction (REP looped the way HardwareSuite already does, one Step()
// per iteration), then call Step() exactly once more. That second call either
// executes the injected terminating HALT, advancing EIP one further, or -- if
// the tested instruction already halted the processor -- returns immediately
// without moving EIP at all, because Step() checks State.Halted first.
if (args.Length > 0 && (args[0] == "-h" || args[0] == "--help"))
{
    Console.WriteLine("usage: real-mode-suite [directory with v1_ex_real_mode/*.MOO.gz] [filter,filter,...]");
    return 0;
}

string directory = args.Length > 0 ? args[0] : @"D:\repositories\vectors\SingleStepTests_80386\v1_ex_real_mode";
string[] filters = args.Length > 1 ? args[1].Split(',') : [];

HashSet<string> revoked = LoadRevocationList(directory);

string[] files = Directory.GetFiles(directory, "*.MOO.gz").OrderBy(f => f).ToArray();
if (filters.Length > 0)
    files = files.Where(f => filters.Any(filter => Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();

var results = new ConcurrentBag<FileResult>();

Parallel.ForEach(files, file => results.Add(RunFile(file, revoked)));

long totalPassed = 0, totalFailed = 0, totalFlagsOnly = 0, totalSkipped = 0;
foreach (FileResult result in results.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
{
    Console.Write(result.Report);
    totalPassed += result.Passed;
    totalFailed += result.Failed;
    totalFlagsOnly += result.FlagsOnlyFailed;
    totalSkipped += result.Skipped;
}

Console.WriteLine($"\nTOTAL: {totalPassed} passed, {totalFailed} failed ({totalFlagsOnly} flags-only), {totalSkipped} revoked/skipped");
return totalFailed == 0 ? 0 : 1;

// Runs every test in one MOO file against a single, file-private Cpu, and
// renders the whole file's report as one string so parallel files never
// interleave their output.
static FileResult RunFile(string path, HashSet<string> revoked)
{
    string name = Path.GetFileName(path).Replace(".MOO.gz", "");
    byte[] data = Decompress(path);
    MooFile file;
    try
    {
        file = MooReader.Load(data);
    }
    catch (Exception ex)
    {
        return new FileResult(name, 0, 0, 0, 0, $"{name,-16} FAILED TO PARSE: {ex.Message}\n");
    }

    // The 386EX drives all its address lines: there is no A20 gate between it
    // and the RAM, so FFFF:0010 reaches 0x100000. Our bus powers up with A20
    // off, as an AT does, and left that way every access straddling 1 MB
    // wrapped its upper bytes to address zero.
    using var memory = new PhysicalMemoryBus(16 * 1024 * 1024, 16 * 1024 * 1024);
    memory.A20Enabled = true;
    var cpu = new Cpu(memory);
    cpu.State.SelectModel(CpuModel.Intel386); // the vectors were captured on a 386EX, not the default 486 model

    // The vector the test recorded is whichever transfer through the IVT
    // happened first, whether the processor raised it (ProcessorException)
    // or the tested instruction requested it itself (SoftwareInterruptIssued
    // for INT n) -- the hardware capture cannot tell those apart, and the
    // vectors' EXCP chunk is present for both.
    byte lastVector = 255;
    cpu.ProcessorException += (v, _, _, _) => { if (lastVector == 255) lastVector = v; };
    cpu.SoftwareInterruptIssued += v => { if (lastVector == 255) lastVector = v; };

    long passed = 0, failed = 0, flagsOnly = 0, skipped = 0;
    var firstFailures = new List<string>();
    var touched = new List<uint>(64);

    foreach (MooTest test in file.Tests)
    {
        if (revoked.Contains(test.Hash)) { skipped++; continue; }

        cpu.Reset();
        ApplyInitialState(cpu, test.InitRegs);

        foreach ((uint address, byte value) in test.InitRam)
        {
            memory.WriteByte(address, value);
            touched.Add(address);
        }

        // A REP prefix: Step() runs one string-instruction iteration per
        // call (legitimate, since REP is restartable), but the vector
        // photographs the whole instruction. Loop until EIP moves away from
        // where the instruction started or a fault redirects it -- the same
        // approach tools/HardwareSuite uses for the protected-mode vectors.
        bool rep = test.Bytes.TakeWhile(b => b is 0x66 or 0x67 or 0xF2 or 0xF3 or 0x26 or 0x2E or 0x36 or 0x3E or 0x64 or 0x65)
                              .Any(b => b is 0xF2 or 0xF3);
        uint initialEip = cpu.State.Eip;

        lastVector = 255;
        cpu.Step();
        if (rep)
        {
            int rounds = 0;
            while (lastVector == 255 && cpu.State.Eip == initialEip && rounds++ < 200_000) cpu.Step();
        }

        // The terminating HALT: see the file header comment for why this is
        // unconditional and not special-cased per instruction. The hardware
        // put it on the bus at the first fetch after the instruction, so it
        // is placed at CS:EIP for this one step and the RAM byte underneath
        // is put back afterwards: after a jump or a fault the RAM there may
        // hold something else, and the vectors' final RAM records that byte.
        //
        // Fetching that HALT can itself fault: an instruction ending at IP
        // FFFF puts the HALT at 10000, past the CS limit, and the #GP it
        // raises belongs to the fetch rather than to the instruction. The
        // generator then injected another HALT at the handler's first fetch,
        // so the injection repeats until the processor has halted.
        const int maximumInjections = 3;

        for (int injection = 0; injection < maximumInjections && !cpu.State.Halted; injection++)
        {
            uint haltAddress = (cpu.State.Cs.Base + cpu.State.Eip) & 0xFFFFFF;
            byte underneath = memory.ReadByte(haltAddress);

            memory.WriteByte(haltAddress, 0xF4);
            cpu.Step();
            memory.WriteByte(haltAddress, underneath);
            touched.Add(haltAddress);
        }

        List<string> errors = Compare(cpu, memory, test, file.TopMask, lastVector);

        if (errors.Count == 0)
        {
            passed++;
        }
        else
        {
            failed++;
            if (errors.All(e => e.StartsWith("eflags ", StringComparison.Ordinal) || e.StartsWith("flags-ram[", StringComparison.Ordinal)))
                flagsOnly++;
            if (firstFailures.Count < 3)
                firstFailures.Add($"{test.Name}: {string.Join(", ", errors.Take(3))}"
                                   + (lastVector != 255 ? $"  [fault {lastVector:X2}]" : ""));
        }

        // Clear only the bytes this test touched, rather than the whole 16
        // MB bus, so the next test starts from a clean slate without paying
        // for a fresh allocation every time.
        foreach (uint address in touched) memory.WriteByte(address, 0);
        foreach ((uint address, _) in test.FinalRam) memory.WriteByte(address, 0);
        touched.Clear();
    }

    var report = new System.Text.StringBuilder();
    report.Append($"{name,-16} {passed,6} passed  {failed,6} failed");
    if (flagsOnly > 0) report.Append($"  ({flagsOnly} flags-only)");
    if (skipped > 0) report.Append($"  ({skipped} revoked)");
    report.Append('\n');
    foreach (string line in firstFailures) report.Append($"       {line}\n");

    return new FileResult(name, passed, failed, flagsOnly, skipped, report.ToString());
}

// Loads the test's initial register file into the CPU exactly as a real
// 386's RESET-then-jump would: general registers and EIP set directly, and
// every segment loaded the real-mode way (base = selector * 16, limit
// forced to 0xFFFF), which is what the vectors' README says the hardware's
// descriptor cache holds for every real-mode test.
static void ApplyInitialState(Cpu cpu, in MooRegisters init)
{
    CpuState state = cpu.State;
    state.Cr0 = init.Cr0;
    state.Cr3 = init.Cr3;
    state.Eax = init.Eax; state.Ebx = init.Ebx; state.Ecx = init.Ecx; state.Edx = init.Edx;
    state.Esi = init.Esi; state.Edi = init.Edi; state.Ebp = init.Ebp; state.Esp = init.Esp;
    state.Eip = init.Eip;
    state.Dr[6] = init.Dr6;
    state.Dr[7] = init.Dr7;
    cpu.Flags.SetValue(init.EFlags);

    state.LoadSegmentRealModeReset(SegReg.Cs, (ushort)init.Cs);
    state.LoadSegmentRealModeReset(SegReg.Ds, (ushort)init.Ds);
    state.LoadSegmentRealModeReset(SegReg.Es, (ushort)init.Es);
    state.LoadSegmentRealModeReset(SegReg.Fs, (ushort)init.Fs);
    state.LoadSegmentRealModeReset(SegReg.Gs, (ushort)init.Gs);
    state.LoadSegmentRealModeReset(SegReg.Ss, (ushort)init.Ss);
}

// Compares every one of the twenty register slots, not just the ones FINA
// lists: a register FINA omits is required to still equal its initial
// value, and reporting that here is how a regression that touches a
// register it should have left alone gets caught. Undefined bits are
// dropped from both sides before comparing, using the file-wide mask and
// this test's own mask (either may be absent, in which case that source
// simply imposes no restriction). RAM is compared byte for byte with no
// masking, since the format defines none for memory. The exception vector,
// if any, is whatever the ProcessorException handler captured first while
// running the test.
static List<string> Compare(Cpu cpu, PhysicalMemoryBus memory, MooTest test, MooRegisters? topMask, byte actualVector)
{
    var errors = new List<string>();
    CpuState state = cpu.State;

    for (int bit = 0; bit < MooReader.SlotCount; bit++)
    {
        uint expected = test.FinalRegs.Has(bit) ? MooReader.Get(test.FinalRegs, bit) : MooReader.Get(test.InitRegs, bit);
        uint ours = OursValue(state, cpu.Flags, bit);

        uint mask = 0xFFFFFFFFu;
        if (topMask is { } tm && tm.Has(bit))
        {
            mask &= MooReader.Get(tm, bit);
        }
        if (test.FinalMask is { } fm && fm.Has(bit))
        {
            mask &= MooReader.Get(fm, bit);
        }
        if (bit == MooReader.EFlagsBit)
        {
            mask &= ~(UndefinedFlags.For(test.Bytes, test.InitRegs.Ecx) | UndefinedFlags.Reserved386);
        }

        if (((ours ^ expected) & mask) != 0)
            errors.Add($"{MooReader.SlotName(bit)} {ours:X}!={expected:X}");
    }

    // Any transfer through the IVT pushes FLAGS to the stack, at the address
    // the EXCP chunk names. When eflags itself carries undefined bits, the
    // pushed copy carries the same undefined bits, at those two byte
    // addresses -- this is exactly what the README's "flag_address" field
    // is for, so the same eflags mask applies there, byte-sliced.
    uint eflagsMask = 0xFFFFFFFFu;
    if (topMask is { } tmFlags && tmFlags.Has(MooReader.EFlagsBit))
    {
        eflagsMask &= MooReader.Get(tmFlags, MooReader.EFlagsBit);
    }
    if (test.FinalMask is { } fmFlags && fmFlags.Has(MooReader.EFlagsBit))
    {
        eflagsMask &= MooReader.Get(fmFlags, MooReader.EFlagsBit);
    }
    eflagsMask &= ~UndefinedFlags.For(test.Bytes, test.InitRegs.Ecx);
    uint? flagAddress = test.Exception?.FlagAddress;

    foreach ((uint address, byte expected) in test.FinalRam)
    {
        byte ours = memory.ReadByte(address);
        byte byteMask = 0xFF;
        bool isFlagsByte = false;
        if (flagAddress == address) { byteMask = (byte)(eflagsMask & 0xFF); isFlagsByte = true; }
        else if (flagAddress + 1 == address) { byteMask = (byte)((eflagsMask >> 8) & 0xFF); isFlagsByte = true; }

        if (((ours ^ expected) & byteMask) != 0)
            errors.Add(isFlagsByte ? $"flags-ram[{address:X}] {ours:X2}!={expected:X2}" : $"ram[{address:X}] {ours:X2}!={expected:X2}");
    }

    int expectedVector = test.Exception?.Vector ?? -1;
    if (expectedVector >= 0 && actualVector != expectedVector)
        errors.Add($"fault {(actualVector == 255 ? "none" : actualVector.ToString("X2"))}!=expected {expectedVector:X2}");
    else if (expectedVector < 0 && actualVector != 255)
        errors.Add($"unexpected fault {actualVector:X2}");

    return errors;
}

// The twenty register slots MOO defines, read back from the CPU the same
// way ApplyInitialState wrote them in: segments as their selector only,
// since real mode never gives a segment override an independent base.
static uint OursValue(CpuState state, LazyFlags flags, int bit) => bit switch
{
    MooReader.Cr0Bit => state.Cr0, MooReader.Cr3Bit => state.Cr3,
    MooReader.EaxBit => state.Eax, MooReader.EbxBit => state.Ebx,
    MooReader.EcxBit => state.Ecx, MooReader.EdxBit => state.Edx,
    MooReader.EsiBit => state.Esi, MooReader.EdiBit => state.Edi,
    MooReader.EbpBit => state.Ebp, MooReader.EspBit => state.Esp,
    MooReader.CsBit => state.Cs.Selector, MooReader.DsBit => state.Ds.Selector,
    MooReader.EsBit => state.Es.Selector, MooReader.FsBit => state.Fs.Selector,
    MooReader.GsBit => state.Gs.Selector, MooReader.SsBit => state.Ss.Selector,
    MooReader.EipBit => state.Eip, MooReader.EFlagsBit => flags.Value,
    MooReader.Dr6Bit => state.Dr[6], MooReader.Dr7Bit => state.Dr[7],
    _ => 0,
};

// Loads the revocation list next to the vector directory (as the README
// ships it) and lower-cases every hash, since MOO hashes are hex-encoded
// with Convert.ToHexStringLower.
static HashSet<string> LoadRevocationList(string vectorDirectory)
{
    string? candidate = Path.Combine(vectorDirectory, "..", "revocation_list.txt");
    if (!File.Exists(candidate))
    {
        candidate = Path.Combine(vectorDirectory, "revocation_list.txt");
    }
    if (!File.Exists(candidate))
    {
        return [];
    }

    return File.ReadAllLines(candidate)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#'))
        .Select(line => line.ToLowerInvariant())
        .ToHashSet();
}

static byte[] Decompress(string path)
{
    using var file = File.OpenRead(path);
    using var gzip = new GZipStream(file, CompressionMode.Decompress);
    using var memory = new MemoryStream();
    gzip.CopyTo(memory);
    return memory.ToArray();
}

internal sealed record FileResult(string Name, long Passed, long Failed, long FlagsOnlyFailed, long Skipped, string Report);
