namespace Aurora386.Core.Cpu.Sse;

/// <summary>
/// MXCSR's bit layout (SDM Vol.1 §11.6.2, plans/specs/sse.md §3): six sticky
/// exception flags at bits 0-5 (mirrored one for one by <see cref="FpFlags"/>,
/// so a computed <see cref="FpFlags"/> value can be OR'd into MXCSR directly),
/// DAZ at bit 6 (never settable on this model -- see
/// <see cref="SseUnit.MxcsrMask"/>), the matching six exception masks at bits
/// 7-12, the two-bit rounding control at 13-14, and flush-to-zero at 15.
/// Bits 16-31 are reserved and always read/write as zero.
/// </summary>
public static class MxcsrBits
{
    public const uint InvalidFlag = 1u << 0;
    public const uint DenormalFlag = 1u << 1;
    public const uint DivideByZeroFlag = 1u << 2;
    public const uint OverflowFlag = 1u << 3;
    public const uint UnderflowFlag = 1u << 4;
    public const uint PrecisionFlag = 1u << 5;
    public const uint AllFlags = InvalidFlag | DenormalFlag | DivideByZeroFlag | OverflowFlag | UnderflowFlag | PrecisionFlag;

    public const uint Daz = 1u << 6;

    public const uint InvalidMask = 1u << 7;
    public const uint DenormalMask = 1u << 8;
    public const uint DivideByZeroMask = 1u << 9;
    public const uint OverflowMask = 1u << 10;
    public const uint UnderflowMask = 1u << 11;
    public const uint PrecisionMask = 1u << 12;
    public const uint AllMasks = InvalidMask | DenormalMask | DivideByZeroMask | OverflowMask | UnderflowMask | PrecisionMask;

    /// <summary>Bits 13-14: 00 nearest, 01 down (toward -infinity), 10 up (toward +infinity), 11 toward zero -- the same encoding <see cref="RoundingMode"/> uses.</summary>
    public const int RoundingControlShift = 13;
    public const uint RoundingControlMask = 0b11u << RoundingControlShift;

    public const uint FlushToZero = 1u << 15;
}
