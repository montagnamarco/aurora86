namespace Aurora86.Core.Cpu.Sse;

/// <summary>
/// One 128-bit XMM register, held as four 32-bit lanes -- <see cref="D0"/> is
/// bits 31:0 (element 0, the one every scalar (SS) instruction reads and
/// writes), through <see cref="D3"/> at bits 127:96. Every SSE instruction
/// Aurora implements works either on the whole 128 bits, on 64-bit halves
/// (<see cref="Low"/>/<see cref="High"/> -- the shapes MOVLPS/MOVHPS/
/// MOVHLPS/MOVLHPS and the packed MMX-register conversions use) or on the
/// four 32-bit lanes individually (the packed arithmetic, UNPCK, SHUFPS and
/// the logical instructions), never on any other width -- so lane-indexed
/// storage is the natural representation, the same choice Cpu.Mmx.cs makes
/// for the 64-bit MM register file.
/// </summary>
public struct XmmValue : IEquatable<XmmValue>
{
    public uint D0, D1, D2, D3;

    public readonly ulong Low => D0 | ((ulong)D1 << 32);
    public readonly ulong High => D2 | ((ulong)D3 << 32);

    public static XmmValue FromHalves(ulong low, ulong high) => new()
    {
        D0 = (uint)low,
        D1 = (uint)(low >> 32),
        D2 = (uint)high,
        D3 = (uint)(high >> 32),
    };

    /// <summary>The 32-bit lane at <paramref name="index"/>, 0 (bits 31:0) through 3 (bits 127:96).</summary>
    public readonly uint Lane(int index) => index switch
    {
        0 => D0,
        1 => D1,
        2 => D2,
        3 => D3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public void SetLane(int index, uint value)
    {
        switch (index)
        {
            case 0: D0 = value; break;
            case 1: D1 = value; break;
            case 2: D2 = value; break;
            case 3: D3 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public readonly bool Equals(XmmValue other) => D0 == other.D0 && D1 == other.D1 && D2 == other.D2 && D3 == other.D3;
    public override readonly bool Equals(object? obj) => obj is XmmValue other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(D0, D1, D2, D3);
}
