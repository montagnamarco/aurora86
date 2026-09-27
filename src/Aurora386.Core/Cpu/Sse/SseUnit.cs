namespace Aurora386.Core.Cpu.Sse;

/// <summary>
/// The Pentium III's own architectural state SSE adds beyond the integer and
/// x87/MMX register files: eight 128-bit XMM registers and the MXCSR
/// control/status register. Owned by the <c>Cpu</c> instance, exactly like
/// <see cref="Fpu.X87Unit"/> and <see cref="ModelSpecificRegisters"/> --
/// FXSAVE/FXRSTOR (Cpu.P6.cs) read and write it directly rather than through
/// the ordinary memory bus, and a machine snapshot carries it as a section
/// of its own ("SSE ", the coordinator's own container -- plans/specs/sse.md's
/// coordinator decisions).
///
/// A processor without CPUID.SSE still has this object (every <c>Cpu</c>
/// does), but nothing ever reaches it: every SSE opcode raises <c>#UD</c>
/// before touching a register or MXCSR (Cpu.Sse.cs's <c>RequireSseInstruction</c>),
/// and FXSAVE/FXRSTOR keep showing that silicon's own placeholder image
/// (Cpu.P6.cs) instead of this unit's real content.
/// </summary>
public sealed class SseUnit
{
    public const int RegisterCount = 8;

    private readonly XmmValue[] _registers = new XmmValue[RegisterCount];

    /// <summary>MXCSR's power-on value (SDM Vol.1 §11.6.4): every exception masked, round to nearest, FZ and DAZ off.</summary>
    public const uint MxcsrPowerOnDefault = 0x0000_1F80;

    /// <summary>
    /// MXCSR_MASK for the Pentium III model Aurora carries: every bit except
    /// DAZ (bit 6) and the reserved bits 16-31 (coordinator decision,
    /// plans/specs/sse.md -- "early Pentium III: MXCSR_MASK FFBFh (no DAZ);
    /// later steppings FFFFh (uncertain cutoff)", settled in favour of the
    /// earlier, narrower mask). LDMXCSR and FXRSTOR both reject any MXCSR
    /// value with a bit set outside this mask with <c>#GP(0)</c>, before
    /// loading anything.
    /// </summary>
    public const uint MxcsrMask = 0x0000FFBF;

    /// <summary>MXCSR itself: the six sticky exception flags, DAZ (never set here), the six exception masks, the rounding control and flush-to-zero.</summary>
    public uint Mxcsr = MxcsrPowerOnDefault;

    /// <summary>XMM0-7 by index, mutable in place: <c>Sse.Xmm(3).D0 = ...</c> writes lane 0 of XMM3 directly.</summary>
    public ref XmmValue Xmm(int index) => ref _registers[index];

    /// <summary>RESET's state: every XMM register zeroed, MXCSR at its power-on default (SDM Vol.1 §11.6.4, "Table 11-2").</summary>
    public void Reset()
    {
        Array.Clear(_registers);
        Mxcsr = MxcsrPowerOnDefault;
    }

    // --- snapshot ---------------------------------------------------------
    //
    // The coordinator's own MachineSnapshot.cs adds the four-letter "SSE "
    // section header and reads/writes this payload through it, the same
    // pattern Cpu.Fpu.X87Unit and ModelSpecificRegisters already follow.

    public const int SnapshotVersion = 1;
    public const int SnapshotLength = RegisterCount * 16 + 4;   // eight 128-bit registers, then MXCSR.

    public void WriteSnapshot(BinaryWriter writer)
    {
        for (int i = 0; i < RegisterCount; i++)
        {
            writer.Write(_registers[i].D0);
            writer.Write(_registers[i].D1);
            writer.Write(_registers[i].D2);
            writer.Write(_registers[i].D3);
        }

        writer.Write(Mxcsr);
    }

    public void ReadSnapshot(BinaryReader reader, int version)
    {
        if (version < 1 || version > SnapshotVersion)
        {
            throw new InvalidDataException($"SSE snapshot version {version} is not supported.");
        }

        for (int i = 0; i < RegisterCount; i++)
        {
            _registers[i] = new XmmValue
            {
                D0 = reader.ReadUInt32(),
                D1 = reader.ReadUInt32(),
                D2 = reader.ReadUInt32(),
                D3 = reader.ReadUInt32(),
            };
        }

        // Masked against MxcsrMask on the way in (a P4.1 review finding):
        // a snapshot has no guest-visible fault to raise on a reserved bit
        // the way LDMXCSR/FXRSTOR's own #GP(0) does, so it is cleaned up
        // silently instead, the same bits a live LDMXCSR would have
        // rejected outright.
        Mxcsr = reader.ReadUInt32() & MxcsrMask;
    }
}
