using Aurora86.Core.Cpu.Sse;

namespace Aurora86.Core.Cpu;

/// <summary>
/// The Pentium 4 Prescott's SSE3 XMM instructions: horizontal arithmetic,
/// alternating add/subtract, duplicate moves, and LDDQU.  Compilers use the
/// arithmetic forms to reduce complex values and the duplicate forms while
/// preparing packed data; all leave the x87 stack alone.
/// </summary>
public sealed partial class Cpu
{
    /// <summary>
    /// Dispatches SSE3's mandatory-prefix encodings after the two-byte
    /// decoder has identified their second opcode byte.
    /// </summary>
    private void ExecuteSse3(byte opcode)
    {
        RequireSseInstruction(CpuFeature.Sse3);
        ValidateSse3Prefix(opcode);

        ModRm operand = DecodeModRm();
        if (opcode == 0xF0 && operand.IsRegister)
        {
            throw new CpuException(6);
        }

        if (SseTaskSwitched())
        {
            return;
        }

        switch (opcode)
        {
            case 0x7C:
                ExecuteHorizontalAdd(operand, packedDouble: _prefixes.OperandSize);
                return;

            case 0x7D:
                ExecuteHorizontalSubtract(operand, packedDouble: _prefixes.OperandSize);
                return;

            case 0xD0:
                ExecuteAddSubtract(operand, packedDouble: _prefixes.OperandSize);
                return;

            case 0xF0:
                ExecuteLddqu(operand);
                return;

            case 0x12:
                ExecuteDuplicateMove(operand, doublePrecision: _prefixes.RepNe);
                return;

            case 0x16:
                ExecuteMoveShdup(operand);
                return;
        }
    }

    /// <summary>Rejects every prefix pair which does not name one SSE3 instruction.</summary>
    private void ValidateSse3Prefix(byte opcode)
    {
        int prefixCount = (_prefixes.OperandSize ? 1 : 0)
            + (_prefixes.RepNe ? 1 : 0)
            + (_prefixes.Rep ? 1 : 0);

        bool valid;
        switch (opcode)
        {
            case 0x7C:
            case 0x7D:
            case 0xD0:
                valid = prefixCount == 1 && (_prefixes.OperandSize || _prefixes.RepNe);
                break;

            case 0xF0:
                valid = prefixCount == 1 && _prefixes.RepNe;
                break;

            case 0x12:
                valid = prefixCount == 1 && (_prefixes.RepNe || _prefixes.Rep);
                break;

            case 0x16:
                valid = prefixCount == 1 && _prefixes.Rep;
                break;

            default:
                valid = false;
                break;
        }

        if (!valid)
        {
            throw new CpuException(6);
        }
    }

    /// <summary>HADDPS/HADDPD add adjacent destination lanes, then source lanes.</summary>
    private void ExecuteHorizontalAdd(in ModRm operand, bool packedDouble)
    {
        XmmValue destination = Sse.Xmm(operand.Reg);
        XmmValue source = ReadXmmSourceAligned(operand);
        RoundingMode roundingMode = CurrentRoundingMode();
        bool flushToZero = CurrentFlushToZero();
        bool underflowMasked = CurrentUnderflowMasked();
        bool daz = CurrentDaz();

        if (packedDouble)
        {
            (ulong low, FpFlags lowFlags) = DoubleFloat.Add(
                destination.Low, destination.High, roundingMode, flushToZero, underflowMasked, daz);
            (ulong high, FpFlags highFlags) = DoubleFloat.Add(
                source.Low, source.High, roundingMode, flushToZero, underflowMasked, daz);
            if (RaiseIfUnmasked(lowFlags | highFlags))
            {
                return;
            }

            Sse.Xmm(operand.Reg) = XmmValue.FromHalves(low, high);
            return;
        }

        (uint destinationPair0, FpFlags destinationPair0Flags) = SingleFloat.Add(
            destination.D0, destination.D1, roundingMode, flushToZero, underflowMasked, daz);
        (uint destinationPair1, FpFlags destinationPair1Flags) = SingleFloat.Add(
            destination.D2, destination.D3, roundingMode, flushToZero, underflowMasked, daz);
        (uint sourcePair0, FpFlags sourcePair0Flags) = SingleFloat.Add(
            source.D0, source.D1, roundingMode, flushToZero, underflowMasked, daz);
        (uint sourcePair1, FpFlags sourcePair1Flags) = SingleFloat.Add(
            source.D2, source.D3, roundingMode, flushToZero, underflowMasked, daz);
        if (RaiseIfUnmasked(
            destinationPair0Flags | destinationPair1Flags | sourcePair0Flags | sourcePair1Flags))
        {
            return;
        }

        Sse.Xmm(operand.Reg) = new XmmValue
        {
            D0 = destinationPair0,
            D1 = destinationPair1,
            D2 = sourcePair0,
            D3 = sourcePair1,
        };
    }

    /// <summary>HSUBPS/HSUBPD subtract adjacent destination lanes, then source lanes.</summary>
    private void ExecuteHorizontalSubtract(in ModRm operand, bool packedDouble)
    {
        XmmValue destination = Sse.Xmm(operand.Reg);
        XmmValue source = ReadXmmSourceAligned(operand);
        RoundingMode roundingMode = CurrentRoundingMode();
        bool flushToZero = CurrentFlushToZero();
        bool underflowMasked = CurrentUnderflowMasked();
        bool daz = CurrentDaz();

        if (packedDouble)
        {
            (ulong low, FpFlags lowFlags) = DoubleFloat.Subtract(
                destination.Low, destination.High, roundingMode, flushToZero, underflowMasked, daz);
            (ulong high, FpFlags highFlags) = DoubleFloat.Subtract(
                source.Low, source.High, roundingMode, flushToZero, underflowMasked, daz);
            if (RaiseIfUnmasked(lowFlags | highFlags))
            {
                return;
            }

            Sse.Xmm(operand.Reg) = XmmValue.FromHalves(low, high);
            return;
        }

        (uint destinationPair0, FpFlags destinationPair0Flags) = SingleFloat.Subtract(
            destination.D0, destination.D1, roundingMode, flushToZero, underflowMasked, daz);
        (uint destinationPair1, FpFlags destinationPair1Flags) = SingleFloat.Subtract(
            destination.D2, destination.D3, roundingMode, flushToZero, underflowMasked, daz);
        (uint sourcePair0, FpFlags sourcePair0Flags) = SingleFloat.Subtract(
            source.D0, source.D1, roundingMode, flushToZero, underflowMasked, daz);
        (uint sourcePair1, FpFlags sourcePair1Flags) = SingleFloat.Subtract(
            source.D2, source.D3, roundingMode, flushToZero, underflowMasked, daz);
        if (RaiseIfUnmasked(
            destinationPair0Flags | destinationPair1Flags | sourcePair0Flags | sourcePair1Flags))
        {
            return;
        }

        Sse.Xmm(operand.Reg) = new XmmValue
        {
            D0 = destinationPair0,
            D1 = destinationPair1,
            D2 = sourcePair0,
            D3 = sourcePair1,
        };
    }

    /// <summary>ADDSUBPS/ADDSUBPD subtract even lanes and add odd lanes.</summary>
    private void ExecuteAddSubtract(in ModRm operand, bool packedDouble)
    {
        XmmValue destination = Sse.Xmm(operand.Reg);
        XmmValue source = ReadXmmSourceAligned(operand);
        RoundingMode roundingMode = CurrentRoundingMode();
        bool flushToZero = CurrentFlushToZero();
        bool underflowMasked = CurrentUnderflowMasked();
        bool daz = CurrentDaz();

        if (packedDouble)
        {
            (ulong low, FpFlags lowFlags) = DoubleFloat.Subtract(
                destination.Low, source.Low, roundingMode, flushToZero, underflowMasked, daz);
            (ulong high, FpFlags highFlags) = DoubleFloat.Add(
                destination.High, source.High, roundingMode, flushToZero, underflowMasked, daz);
            if (RaiseIfUnmasked(lowFlags | highFlags))
            {
                return;
            }

            Sse.Xmm(operand.Reg) = XmmValue.FromHalves(low, high);
            return;
        }

        (uint lane0, FpFlags lane0Flags) = SingleFloat.Subtract(
            destination.D0, source.D0, roundingMode, flushToZero, underflowMasked, daz);
        (uint lane1, FpFlags lane1Flags) = SingleFloat.Add(
            destination.D1, source.D1, roundingMode, flushToZero, underflowMasked, daz);
        (uint lane2, FpFlags lane2Flags) = SingleFloat.Subtract(
            destination.D2, source.D2, roundingMode, flushToZero, underflowMasked, daz);
        (uint lane3, FpFlags lane3Flags) = SingleFloat.Add(
            destination.D3, source.D3, roundingMode, flushToZero, underflowMasked, daz);
        if (RaiseIfUnmasked(lane0Flags | lane1Flags | lane2Flags | lane3Flags))
        {
            return;
        }

        Sse.Xmm(operand.Reg) = new XmmValue
        {
            D0 = lane0,
            D1 = lane1,
            D2 = lane2,
            D3 = lane3,
        };
    }

    /// <summary>MOVDDUP/MOVSLDUP duplicate the selected source lanes without arithmetic.</summary>
    private void ExecuteDuplicateMove(in ModRm operand, bool doublePrecision)
    {
        if (doublePrecision)
        {
            ulong low = ReadXmmSourceScalarQword(operand);
            Sse.Xmm(operand.Reg) = XmmValue.FromHalves(low, low);
            return;
        }

        XmmValue source = ReadXmmSourceAligned(operand);
        Sse.Xmm(operand.Reg) = new XmmValue
        {
            D0 = source.D0,
            D1 = source.D0,
            D2 = source.D2,
            D3 = source.D2,
        };
    }

    /// <summary>MOVSHDUP duplicates the high single-precision lane from each pair.</summary>
    private void ExecuteMoveShdup(in ModRm operand)
    {
        XmmValue source = ReadXmmSourceAligned(operand);
        Sse.Xmm(operand.Reg) = new XmmValue
        {
            D0 = source.D1,
            D1 = source.D1,
            D2 = source.D3,
            D3 = source.D3,
        };
    }

    /// <summary>LDDQU loads exactly sixteen unaligned bytes and commits XMM only after both halves read.</summary>
    private void ExecuteLddqu(in ModRm operand)
    {
        CheckLimit(operand.Segment, operand.Offset, 16);
        ulong low = ReadMemQword(operand.Segment, operand.Offset);
        ulong high = ReadMemQword(operand.Segment, operand.Offset + 8);
        Sse.Xmm(operand.Reg) = XmmValue.FromHalves(low, high);
    }
}
