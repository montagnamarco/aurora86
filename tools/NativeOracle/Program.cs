using System.Text.Json;
using NativeOracle;

// Runs every instruction family (or the ones named on the command line)
// against both the real CPU and Aurora's interpreter, from identical random
// initial states, and reports every case where the two disagree.
//
//   dotnet run --project tools/NativeOracle -c Release --
//       [--cases N] [--seed S] [--family name,name,...] [--write <dir>]
//
// Deterministic for a given seed: each family draws from its own
// Random(seed + its position in FamilyCatalog.All), so running one family
// alone reproduces exactly the cases it would have drawn as part of the
// whole run.

// --probe is a one-off directed query, separate from the random-family loop
// below: it feeds one exact instruction and one exact XMM0/XMM1/MXCSR to the
// real CPU and prints back what it did, with no comparison against the
// emulator at all. This is how the masked review vectors named in the P4.1
// review (the overflow, tininess and RCP/RSQRT special-value cases) were
// actually read off real silicon rather than guessed from the SDM's prose --
// see SseReviewFindingsTests.cs's own comments for which invocation produced
// which expected value.
if (args.Length > 0 && args[0] == "--probe")
{
    return RunDirectedProbe(args[1..]);
}

int cases = 2000;
int seed = 1;
string[] familyFilter = [];
string? writeDirectory = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cases": cases = int.Parse(args[++i]); break;
        case "--seed": seed = int.Parse(args[++i]); break;
        case "--family": familyFilter = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
        case "--write": writeDirectory = args[++i]; break;
        default:
            Console.WriteLine($"unrecognised argument: {args[i]}");
            return 1;
    }
}

if (writeDirectory is not null)
{
    Directory.CreateDirectory(writeDirectory);
}

using var host = new HostRunner();
var comparer = new OracleComparer(host);

int totalPassed = 0, totalFailed = 0;
bool anyFamilySelected = false;

for (int familyIndex = 0; familyIndex < FamilyCatalog.All.Count; familyIndex++)
{
    Family family = FamilyCatalog.All[familyIndex];
    if (familyFilter.Length > 0 && !familyFilter.Any(f => string.Equals(f, family.Name, StringComparison.OrdinalIgnoreCase)))
    {
        continue;
    }

    anyFamilySelected = true;
    var context = new FamilyContext(new Random(seed + familyIndex), host.BufferAddress);
    int passed = 0, failed = 0;
    var firstFailures = new List<CaseResult>();
    var written = writeDirectory is null ? null : new List<CaseResult>();

    for (int i = 0; i < cases; i++)
    {
        TestCase testCase = family.Generate(context);
        CaseResult result = comparer.Run(testCase);

        if (result.Passed)
        {
            passed++;
        }
        else
        {
            failed++;
            if (firstFailures.Count < 5)
            {
                firstFailures.Add(result);
            }
        }

        written?.Add(result);
    }

    totalPassed += passed;
    totalFailed += failed;
    Console.WriteLine($"{family.Name,-12} {cases,5} cases  {passed,5} passed  {failed,5} failed");
    foreach (CaseResult failure in firstFailures)
    {
        Console.WriteLine($"    {failure.Case.Description}");
        Console.WriteLine($"        bytes: {Convert.ToHexString(failure.Case.Instruction)}");
        Console.WriteLine($"        {string.Join("; ", failure.Differences)}");
        if (failure.Case.InitialXmm is { } xmm)
        {
            for (int i = 0; i < xmm.Length; i++)
            {
                Console.WriteLine($"        XMM{i} initial = {xmm[i].D3:X8}{xmm[i].D2:X8}{xmm[i].D1:X8}{xmm[i].D0:X8}");
            }
            Console.WriteLine($"        MXCSR initial = {failure.Case.InitialMxcsr:X8}");
        }
    }

    if (written is not null)
    {
        string path = Path.Combine(writeDirectory!, $"{family.Name}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(written.Select(CaseRecord.From), new JsonSerializerOptions { WriteIndented = true }));
    }
}

if (!anyFamilySelected)
{
    Console.WriteLine($"no family matched: {string.Join(",", familyFilter)}");
    return 1;
}

Console.WriteLine($"\nTOTAL: {totalPassed} passed, {totalFailed} failed");
return totalFailed == 0 ? 0 : 1;

/// <summary>
/// <c>--probe &lt;instructionHex&gt; &lt;xmm0Hex32&gt; &lt;xmm1Hex32&gt; &lt;mxcsrHex8&gt;</c>:
/// loads XMM0/XMM1 and MXCSR through the same FXRSTOR path every SSE family
/// uses, runs the given raw instruction bytes once, and prints the real
/// CPU's XMM0 and MXCSR afterwards. <paramref name="args"/> excludes the
/// leading "--probe" token itself. Every masked review vector only masked
/// exceptions ever reach here -- an unmasked one traps the host thread with
/// no handler wrapped around this call, exactly as <see cref="SseFamilies"/>'s
/// own type summary warns.
/// </summary>
static int RunDirectedProbe(string[] args)
{
    byte[] instruction = Convert.FromHexString(args[0]);
    var xmm = new Aurora386.Core.Cpu.Sse.XmmValue[8];
    xmm[0] = ParseXmm(args[1]);
    xmm[1] = ParseXmm(args[2]);
    uint mxcsr = Convert.ToUInt32(args[3], 16);
    // Optional 5th argument: EAX before the probed instruction runs, for
    // directed questions about a GPR source/destination (e.g. CVT(T)SD2SI's
    // own DE-on-denormal-source question, which a pure XMM0/XMM1 probe
    // cannot answer since the result lands in a GPR, not XMM0).
    uint initialEax = args.Length > 4 ? Convert.ToUInt32(args[4], 16) : 0;

    using var host = new HostRunner();
    host.PrepareFxsaveDefaults();
    host.WriteXmmRegisters(xmm);
    host.WriteMxcsr(mxcsr);

    // EBX always carries the shared buffer's own address, and the buffer's
    // own first dword a known sentinel beforehand: harmless for every XMM-
    // only probe (nothing else here ever names EBX), and what a directed
    // question about a memory-destination, non-XMM instruction -- MOVNTI's
    // own 66-prefix question -- needs to read back afterwards.
    var gpr = new uint[8];
    gpr[(int)Aurora386.Core.Cpu.Reg.Eax] = initialEax;
    gpr[(int)Aurora386.Core.Cpu.Reg.Ebx] = host.BufferAddress;
    host.WriteBuffer(BitConverter.GetBytes(0xDEADBEEFu));
    (uint[] resultGpr, _) = host.Run(instruction, gpr, initialEflags: 0, useExtendedState: true);

    Aurora386.Core.Cpu.Sse.XmmValue[] result = host.ReadXmmRegisters();
    uint resultMxcsr = host.ReadMxcsr();
    uint bufferDword = BitConverter.ToUInt32(host.ReadBuffer()[..4]);

    Console.WriteLine($"XMM0 = {result[0].D3:X8}{result[0].D2:X8}{result[0].D1:X8}{result[0].D0:X8}");
    Console.WriteLine($"MXCSR = {resultMxcsr:X8}");
    Console.WriteLine($"EAX = {resultGpr[(int)Aurora386.Core.Cpu.Reg.Eax]:X8}");
    Console.WriteLine($"[EBX] = {bufferDword:X8}");
    return 0;

    static Aurora386.Core.Cpu.Sse.XmmValue ParseXmm(string hex32) => new()
    {
        D0 = Convert.ToUInt32(hex32[0..8], 16),
        D1 = Convert.ToUInt32(hex32[8..16], 16),
        D2 = Convert.ToUInt32(hex32[16..24], 16),
        D3 = Convert.ToUInt32(hex32[24..32], 16),
    };
}

/// <summary>The JSON shape --write saves: initial state, instruction, and outcome, SingleStepTests-like.</summary>
internal sealed record CaseRecord(string Family, string Description, string InstructionHex, uint[] InitialGpr, uint InitialEflags, bool Passed, IReadOnlyList<string> Differences)
{
    public static CaseRecord From(CaseResult result) => new(
        result.Case.Family,
        result.Case.Description,
        Convert.ToHexString(result.Case.Instruction),
        result.Case.InitialGpr,
        result.Case.InitialEflags,
        result.Passed,
        result.Differences);
}
