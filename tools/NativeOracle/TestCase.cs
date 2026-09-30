using Aurora86.Core.Cpu;
using Aurora86.Core.Cpu.Sse;

namespace NativeOracle;

/// <summary>
/// One generated instruction, its initial machine state, and the mask of
/// EFLAGS bits the SDM actually defines for it -- the bits outside that mask
/// are never compared, because a real processor's undefined bits and
/// Aurora's are both legitimate, independent choices (see
/// plans/specs/cpu-models.md §4).
/// </summary>
public sealed class TestCase
{
    public required string Family { get; init; }

    /// <summary>Family plus the operands actually chosen, for a failure report: e.g. "ADD r/m32,r32 EDI,[EBX+140]".</summary>
    public required string Description { get; init; }

    public required byte[] Instruction { get; init; }

    /// <summary>Indexed like Aurora86.Core.Cpu.Reg; index 4 (Esp) is never read.</summary>
    public required uint[] InitialGpr { get; init; }

    public required uint InitialEflags { get; init; }

    /// <summary>The shared buffer's contents before the instruction runs.</summary>
    public required byte[] InitialBuffer { get; init; }

    /// <summary>Which EFLAGS bits this instruction defines for these particular operands (see UndefinedFlags).</summary>
    public required uint DefinedFlagsMask { get; init; }

    /// <summary>
    /// Registers whose value is architecturally undefined for this specific
    /// case and so must not be compared -- today only BSF/BSR's destination
    /// when the source was zero (SDM Vol.2A, BSF/BSR "Operation").
    /// </summary>
    public Aurora86.Core.Cpu.Reg[] IgnoredRegisters { get; init; } = [];

    /// <summary>The processor model to select in the emulator: Pentium once a family needs CMPXCHG8B, Intel486 otherwise.</summary>
    public CpuModel Model { get; init; } = CpuModel.Intel486;

    /// <summary>
    /// MM0-7 before the instruction runs, or null for every family that is
    /// not about MMX. Non-null asks the comparer to seed both engines with
    /// these eight registers (already "in MMX use": TOP 0, every tag valid --
    /// see HostRunner.PrepareFxsaveDefaults and EmulatorRunner.Run) and, after
    /// the case runs, to compare MM0-7, the x87 TOP field and the tag word
    /// too, on top of everything an ordinary case already checks.
    /// </summary>
    public ulong[]? InitialMmx { get; init; }

    /// <summary>
    /// XMM0-7 before the instruction runs, or null for every family that is
    /// not about SSE. Non-null asks the comparer to seed both engines with
    /// these eight registers and MXCSR (<see cref="InitialMxcsr"/>) and,
    /// after the case runs, to compare XMM0-7 and MXCSR (flags included) too.
    /// A case with this set should also carry <c>Model = CpuModel.PentiumIII</c>.
    /// </summary>
    public XmmValue[]? InitialXmm { get; init; }

    /// <summary>MXCSR before the instruction runs -- the rounding mode and FZ bit a family is exercising -- read only when <see cref="InitialXmm"/> is set.</summary>
    public uint? InitialMxcsr { get; init; }
}
