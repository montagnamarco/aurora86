namespace Aurora86.Tests;

/// <summary>
/// Runs the small, straight-line subset of AML that <c>AcpiTables</c>'s PCI
/// interrupt link devices' <c>_CRS</c>, <c>_SRS</c> and <c>_DIS</c> method
/// bodies use (work package P4.5, coordinator decision C3: "keep the method
/// bodies minimal"). It is not a general AML interpreter -- there is no If,
/// While or anything reentrant in any method this DSDT declares, so none of
/// that is implemented -- only Store, And, Or, ShiftLeft, Decrement,
/// FindSetRightBit, CreateWordField/CreateByteField and Return, over
/// Local0-7, Arg0-6, a handful of named buffers (<c>BUF</c>) and one named
/// Field this test wires to a host-supplied byte (<c>RIRQ</c>, standing in
/// for the emulator's own PIIX4 60h-63h register).
/// </summary>
public sealed class AmlMethodExecutor
{
    private const byte NameOp = 0x08;
    private const byte BytePrefix = 0x0A;
    private const byte WordPrefix = 0x0B;
    private const byte DWordPrefix = 0x0C;
    private const byte BufferOp = 0x11;
    private const byte StoreOp = 0x70;
    private const byte DecrementOp = 0x76;
    private const byte ShiftLeftOp = 0x79;
    private const byte AndOp = 0x7B;
    private const byte OrOp = 0x7D;
    private const byte FindSetRightBitOp = 0x82;
    private const byte CreateWordFieldOp = 0x8B;
    private const byte CreateByteFieldOp = 0x8C;
    private const byte ReturnOp = 0xA4;

    private readonly record struct Alias(byte[] Buffer, int Offset, int Width);

    private readonly Dictionary<string, byte[]> _buffers = new();
    private readonly Dictionary<string, Alias> _aliases = new();
    private readonly object?[] _locals = new object?[8];
    private readonly object?[] _args = new object?[7];
    private readonly Func<byte> _readField;
    private readonly Action<byte> _writeField;

    /// <summary>
    /// <paramref name="arg0Buffer"/> stands in for the resource buffer an
    /// ACPI OS would pass to <c>_SRS</c>; <paramref name="readField"/> and
    /// <paramref name="writeField"/> stand in for the PIIX4's own 60h-63h
    /// register byte (<c>RIRQ</c>'s Field) that the emulator's real
    /// OperationRegion would otherwise back.
    /// </summary>
    public AmlMethodExecutor(byte[]? arg0Buffer, Func<byte> readField, Action<byte> writeField)
    {
        _args[0] = arg0Buffer;
        _readField = readField;
        _writeField = writeField;
    }

    /// <summary>Runs a method body to its Return (or its end, for _DIS which has none) and gives back whatever it returned: a <see cref="ulong"/> or a <see cref="byte[]"/>.</summary>
    public object? Execute(byte[] body)
    {
        int pos = 0;
        while (pos < body.Length)
        {
            byte opcode = body[pos];
            switch (opcode)
            {
                case NameOp:
                {
                    pos++;
                    string name = LastSegment(AcpiAmlReader.ReadNameString(body, ref pos));
                    object value = ReadValue(body, ref pos);
                    _buffers[name] = (byte[])value;
                    break;
                }

                case CreateWordFieldOp:
                case CreateByteFieldOp:
                {
                    int width = opcode == CreateWordFieldOp ? 2 : 1;
                    pos++;
                    byte[] buffer = (byte[])ReadValue(body, ref pos);
                    ulong byteIndex = ReadSourceValue(body, ref pos);
                    string newName = LastSegment(AcpiAmlReader.ReadNameString(body, ref pos));
                    _aliases[newName] = new Alias(buffer, (int)byteIndex, width);
                    break;
                }

                case StoreOp:
                {
                    pos++;
                    ulong source = ReadSourceValue(body, ref pos);
                    Ref target = ReadRef(body, ref pos);
                    SetInt(target, source);
                    break;
                }

                case AndOp:
                case OrOp:
                {
                    pos++;
                    ulong a = ReadSourceValue(body, ref pos);
                    ulong b = ReadSourceValue(body, ref pos);
                    Ref target = ReadRef(body, ref pos);
                    SetInt(target, opcode == AndOp ? a & b : a | b);
                    break;
                }

                case ShiftLeftOp:
                {
                    pos++;
                    ulong source = ReadSourceValue(body, ref pos);
                    ulong count = ReadSourceValue(body, ref pos);
                    Ref target = ReadRef(body, ref pos);
                    SetInt(target, source << (int)count);
                    break;
                }

                case DecrementOp:
                {
                    pos++;
                    Ref target = ReadRef(body, ref pos);
                    SetInt(target, GetInt(target) - 1);
                    break;
                }

                case FindSetRightBitOp:
                {
                    pos++;
                    ulong source = ReadSourceValue(body, ref pos);
                    Ref target = ReadRef(body, ref pos);
                    ulong result = source == 0
                        ? 0UL
                        : (ulong)(System.Numerics.BitOperations.TrailingZeroCount((uint)source) + 1);
                    SetInt(target, result);
                    break;
                }

                case ReturnOp:
                    pos++;
                    return ReadValue(body, ref pos);

                default:
                    throw new NotSupportedException(
                        $"AmlMethodExecutor does not know opcode 0x{opcode:X2} at position {pos}.");
            }
        }

        return null;
    }

    // ---- operand and target decoding ----

    private readonly record struct Ref(int Kind, int Index, string Name);   // Kind: 0 Local, 1 Arg, 2 Name.

    private Ref ReadRef(byte[] body, ref int pos)
    {
        byte b = body[pos];
        if (b is >= 0x60 and <= 0x67) { pos++; return new Ref(0, b - 0x60, ""); }
        if (b is >= 0x68 and <= 0x6E) { pos++; return new Ref(1, b - 0x68, ""); }
        string name = LastSegment(AcpiAmlReader.ReadNameString(body, ref pos));
        return new Ref(2, 0, name);
    }

    private ulong GetInt(Ref reference) => reference.Kind switch
    {
        0 => Convert.ToUInt64(_locals[reference.Index] ?? 0UL),
        1 => Convert.ToUInt64(_args[reference.Index] ?? 0UL),
        _ => ReadNamedInteger(reference.Name),
    };

    private void SetInt(Ref reference, ulong value)
    {
        switch (reference.Kind)
        {
            case 0: _locals[reference.Index] = value; break;
            case 1: _args[reference.Index] = value; break;
            default: WriteNamedInteger(reference.Name, value); break;
        }
    }

    private ulong ReadNamedInteger(string name)
    {
        if (name == "RIRQ") return _readField();
        if (_aliases.TryGetValue(name, out Alias alias))
        {
            return alias.Width == 2
                ? (ulong)(alias.Buffer[alias.Offset] | (alias.Buffer[alias.Offset + 1] << 8))
                : alias.Buffer[alias.Offset];
        }

        throw new NotSupportedException($"AmlMethodExecutor does not know the named integer '{name}'.");
    }

    private void WriteNamedInteger(string name, ulong value)
    {
        if (name == "RIRQ") { _writeField((byte)value); return; }
        if (_aliases.TryGetValue(name, out Alias alias))
        {
            alias.Buffer[alias.Offset] = (byte)value;
            if (alias.Width == 2) alias.Buffer[alias.Offset + 1] = (byte)(value >> 8);
            return;
        }

        throw new NotSupportedException($"AmlMethodExecutor does not know the named integer target '{name}'.");
    }

    /// <summary>A TermArg that must resolve to an integer: an immediate constant, Local/Arg, or a named integer.</summary>
    private ulong ReadSourceValue(byte[] body, ref int pos)
    {
        object value = ReadValue(body, ref pos);
        return value is byte[] ? throw new InvalidOperationException("Expected an integer TermArg, found a buffer.") : (ulong)value;
    }

    /// <summary>A TermArg that could resolve to either an integer or a buffer (Return's own operand, and CreateWordField/CreateByteField's source buffer).</summary>
    private object ReadValue(byte[] body, ref int pos)
    {
        byte b = body[pos];
        switch (b)
        {
            case 0x00: pos++; return 0UL;
            case 0x01: pos++; return 1UL;
            case BytePrefix: pos++; return (ulong)body[pos++];
            case WordPrefix:
            {
                pos++;
                ulong value = (ulong)body[pos] | ((ulong)body[pos + 1] << 8);
                pos += 2;
                return value;
            }
            case DWordPrefix:
            {
                pos++;
                ulong value = (ulong)body[pos] | ((ulong)body[pos + 1] << 8)
                    | ((ulong)body[pos + 2] << 16) | ((ulong)body[pos + 3] << 24);
                pos += 4;
                return value;
            }
            case BufferOp:
            {
                pos++;
                int contentEnd = AcpiAmlReader.ReadPkgLengthEnd(body, ref pos);
                ulong size = ReadSourceValue(body, ref pos);
                byte[] bytes = body[pos..(pos + (int)size)];
                pos = contentEnd;
                return bytes;
            }
            default:
                if (b is >= 0x60 and <= 0x67) { pos++; return _locals[b - 0x60] ?? 0UL; }
                if (b is >= 0x68 and <= 0x6E) { pos++; return _args[b - 0x68] ?? 0UL; }
                string name = LastSegment(AcpiAmlReader.ReadNameString(body, ref pos));
                return _buffers.TryGetValue(name, out byte[]? buffer) ? buffer : ReadNamedInteger(name);
        }
    }

    private static string LastSegment(string path) => path.Length == 0 ? path : path[(path.LastIndexOf('.') + 1)..];
}
