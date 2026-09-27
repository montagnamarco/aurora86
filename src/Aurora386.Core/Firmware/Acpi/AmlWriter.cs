namespace Aurora386.Core.Firmware.Acpi;

/// <summary>
/// A minimal AML (ACPI Machine Language) encoder for the DSDT this BIOS
/// builds at start-up (work package P4.5, coordinator decision C3): only the
/// handful of term encodings the generated namespace actually needs --
/// Scope, Device, Processor, Name, Method, Package, Buffer, OperationRegion
/// and Field -- plus the small set of executable operators the PCI
/// interrupt links' <c>_CRS</c>/<c>_SRS</c>/<c>_DIS</c> method bodies use
/// (Store, And, Or, ShiftLeft, Decrement, FindSetRightBit, and the two
/// CreateWordField/CreateByteField aliases).
///
/// There is no ASL compiler in this codebase and no <c>iasl</c> on this
/// machine (decision C3): every opcode value below is named and cited
/// against the section of the ACPI specification that defines it, since
/// nothing else checks the encoding. The opcode values themselves are
/// recalled from the specification without being able to fetch the primary
/// PDF (plans/specs/acpi.md's own sourcing note) but are cross-checked
/// against the ones plans/specs/acpi.md's coordinator decisions already
/// state outright (NameOp, ScopeOp, MethodOp, PackageOp, BufferOp, the
/// ExtOp-prefixed Device/OpRegion/Field/Processor opcodes, the name
/// prefixes and the PkgLength encoding all match); the plain arithmetic and
/// control opcodes this file adds on top of those are not independently
/// confirmed by that source and are called out below for the record.
///
/// The encoder builds inside out: every container term (Scope, Device,
/// Processor, Method, Package, Buffer and the field-unit list) opens a fresh
/// byte list, and closing it wraps whatever was written inside with the
/// term's own opcode and PkgLength before appending the whole thing to its
/// parent. That sidesteps the usual chicken-and-egg problem of not knowing a
/// PkgLength's own byte count until the length it describes is already
/// known -- the alternative, reserving a fixed number of length bytes and
/// patching them in later, only works if that number is fixed in advance,
/// and PkgLength's own encoding does not fix it.
/// </summary>
public sealed class AmlWriter
{
    // ---- AML opcodes confirmed by plans/specs/acpi.md §3 ----

    private const byte ZeroOp = 0x00;
    private const byte OneOp = 0x01;
    private const byte NameOp = 0x08;
    private const byte BytePrefix = 0x0A;
    private const byte WordPrefix = 0x0B;
    private const byte DWordPrefix = 0x0C;
    private const byte ScopeOp = 0x10;
    private const byte BufferOp = 0x11;
    private const byte PackageOp = 0x12;
    private const byte MethodOp = 0x14;
    private const byte DualNamePrefix = 0x2E;
    private const byte MultiNamePrefix = 0x2F;
    private const byte RootChar = 0x5C;
    private const byte ParentPrefixChar = 0x5E;
    private const byte ExtOpPrefix = 0x5B;
    private const byte ExtOpRegionOp = 0x80;
    private const byte ExtFieldOp = 0x81;
    private const byte ExtDeviceOp = 0x82;
    private const byte ExtProcessorOp = 0x83;

    // ---- plain opcodes recalled from the ACPI AML opcode table (ACPI 6.x
    // §20.3), not independently re-confirmed by plans/specs/acpi.md, and
    // used only for the small, fixed set of statements the PCI link
    // devices' method bodies need. Flagged per decision C6. ----

    private const byte Local0Op = 0x60;
    private const byte Arg0Op = 0x68;
    private const byte StoreOp = 0x70;
    private const byte DecrementOp = 0x76;
    private const byte ShiftLeftOp = 0x79;
    private const byte AndOp = 0x7B;
    private const byte OrOp = 0x7D;
    private const byte FindSetRightBitOp = 0x82;
    private const byte CreateWordFieldOp = 0x8B;
    private const byte CreateByteFieldOp = 0x8C;
    private const byte ReturnOp = 0xA4;

    private readonly List<byte> _root = [];
    private readonly Stack<List<byte>> _stack = new();

    /// <summary>The buffer currently receiving bytes: the innermost open container, or the definition block's own top level once nothing is open.</summary>
    private List<byte> Current => _stack.Count > 0 ? _stack.Peek() : _root;

    public byte[] ToArray() => _root.ToArray();

    // ---- raw bytes and integers ----

    public void Raw(byte value) => Current.Add(value);

    public void Raw(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) Current.Add(value);
    }

    /// <summary>
    /// An AML integer constant (ACPI §20.2.3, "Data Objects Encoding"): the
    /// smallest of Zero/One/Byte/Word/DWord that holds the value. This DSDT
    /// is revision 1 (32-bit integers throughout), so a QWord constant is
    /// never needed.
    /// </summary>
    public void Integer(uint value)
    {
        if (value == 0) { Raw(ZeroOp); return; }
        if (value == 1) { Raw(OneOp); return; }
        if (value <= 0xFF) { Raw(BytePrefix); Raw((byte)value); return; }
        if (value <= 0xFFFF)
        {
            Raw(WordPrefix);
            Raw((byte)value);
            Raw((byte)(value >> 8));
            return;
        }

        Raw(DWordPrefix);
        Raw((byte)value);
        Raw((byte)(value >> 8));
        Raw((byte)(value >> 16));
        Raw((byte)(value >> 24));
    }

    /// <summary>
    /// A name path (ACPI §20.2.2, "Name Objects Encoding"): an optional root
    /// or parent prefix, then a dual-, multi- or single-segment name, each
    /// segment four characters, padded with '_'. Used both to declare a
    /// name and to reference one as an operand.
    /// </summary>
    public void WritePath(string path)
    {
        int at = 0;
        if (path.Length > 0 && path[0] == '\\')
        {
            Raw(RootChar);
            at = 1;
        }

        while (at < path.Length && path[at] == '^')
        {
            Raw(ParentPrefixChar);
            at++;
        }

        string[] segments = path[at..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        switch (segments.Length)
        {
            case 0:
                // A bare '\' or '^' names the scope itself: no segment follows.
                break;
            case 1:
                WriteSegment(segments[0]);
                break;
            case 2:
                Raw(DualNamePrefix);
                WriteSegment(segments[0]);
                WriteSegment(segments[1]);
                break;
            default:
                Raw(MultiNamePrefix);
                Raw((byte)segments.Length);
                foreach (string segment in segments) WriteSegment(segment);
                break;
        }
    }

    private void WriteSegment(string segment)
    {
        if (segment.Length == 0 || segment.Length > 4)
        {
            throw new ArgumentException($"AML name segment '{segment}' must be one to four characters.");
        }

        for (int i = 0; i < 4; i++)
        {
            Raw(i < segment.Length ? (byte)segment[i] : (byte)'_');
        }
    }

    // ---- Local/Arg references, for the method bodies below ----

    public void Local(int index) => Raw((byte)(Local0Op + index));

    public void Arg(int index) => Raw((byte)(Arg0Op + index));

    // ---- containers -----------------------------------------------------

    private void Begin() => _stack.Push([]);

    /// <summary>Wraps the container just closed with a plain opcode and PkgLength (§20.2.4) and appends it to the parent.</summary>
    private void EndWithPkgLength(byte opcode)
    {
        List<byte> content = _stack.Pop();
        Current.Add(opcode);
        Current.AddRange(EncodePkgLength(content.Count));
        Current.AddRange(content);
    }

    /// <summary>The same wrapping, for the four ExtOp-prefixed container opcodes (Device, Processor, OpRegion's own Field, and nothing else this writer emits).</summary>
    private void EndWithExtPkgLength(byte extOpcode)
    {
        List<byte> content = _stack.Pop();
        Current.Add(ExtOpPrefix);
        Current.Add(extOpcode);
        Current.AddRange(EncodePkgLength(content.Count));
        Current.AddRange(content);
    }

    /// <summary>Scope(name){...}: ScopeOp PkgLength NameString TermList.</summary>
    public void BeginScope(string name)
    {
        Begin();
        WritePath(name);
    }

    public void EndScope() => EndWithPkgLength(ScopeOp);

    /// <summary>Device(name){...}: ExtOpPrefix DeviceOp PkgLength NameString TermList.</summary>
    public void BeginDevice(string name)
    {
        Begin();
        WritePath(name);
    }

    public void EndDevice() => EndWithExtPkgLength(ExtDeviceOp);

    /// <summary>
    /// Processor(name, procId, pblkAddress, pblkLength){...}: this DSDT's
    /// only processor object has no P_BLK (plans/specs/acpi.md §3), so
    /// callers pass zero for both.
    /// </summary>
    public void BeginProcessor(string name, byte procId, uint pblkAddress, byte pblkLength)
    {
        Begin();
        WritePath(name);
        Raw(procId);
        Raw((byte)pblkAddress);
        Raw((byte)(pblkAddress >> 8));
        Raw((byte)(pblkAddress >> 16));
        Raw((byte)(pblkAddress >> 24));
        Raw(pblkLength);
    }

    public void EndProcessor() => EndWithExtPkgLength(ExtProcessorOp);

    /// <summary>Method(name, argCount){...}: MethodOp PkgLength NameString MethodFlags TermList.</summary>
    public void BeginMethod(string name, int argCount)
    {
        Begin();
        WritePath(name);
        Raw((byte)(argCount & 0x07));   // bits 2:0 ArgCount; NotSerialized (bit 3 clear) throughout, since nothing here is reentrant
    }

    public void EndMethod() => EndWithPkgLength(MethodOp);

    /// <summary>Package(numElements){...}: PackageOp PkgLength NumElements PackageElementList.</summary>
    public void BeginPackage(int numElements)
    {
        Begin();
        Raw((byte)numElements);
    }

    public void EndPackage() => EndWithPkgLength(PackageOp);

    /// <summary>Buffer(){ literal bytes }: BufferOp PkgLength BufferSize ByteList. Used for every resource template, which is always fixed literal bytes here -- nothing computes a buffer's contents at AML run time.</summary>
    public void WriteBuffer(ReadOnlySpan<byte> data)
    {
        Begin();
        Integer((uint)data.Length);
        Raw(data);
        EndWithPkgLength(BufferOp);
    }

    /// <summary>Name(path, value): NameOp NameString, then whatever the caller writes for the value (an Integer, a Package, a Buffer...).</summary>
    public void BeginName(string path)
    {
        Raw(NameOp);
        WritePath(path);
    }

    // ---- OperationRegion and Field ----

    /// <summary>OperationRegion(name, space, offset, length): ExtOpPrefix OpRegionOp NameString RegionSpace RegionOffset RegionLen -- not itself a PkgLength container.</summary>
    public void OperationRegion(string name, byte regionSpace, uint offset, uint length)
    {
        Raw(ExtOpPrefix);
        Raw(ExtOpRegionOp);
        WritePath(name);
        Raw(regionSpace);
        Integer(offset);
        Integer(length);
    }

    /// <summary>Field(regionName, fieldFlags){...}: ExtOpPrefix FieldOp PkgLength NameString FieldFlags FieldElementList.</summary>
    public void BeginField(string regionName, byte fieldFlags)
    {
        Begin();
        WritePath(regionName);
        Raw(fieldFlags);
    }

    public void EndField() => EndWithExtPkgLength(ExtFieldOp);

    /// <summary>
    /// One named field unit inside a Field list (§20.2.5.2): NamedField :=
    /// NameSeg PkgLength, where the PkgLength-shaped value here is not a
    /// byte count at all but the field's own bit width -- the same
    /// variable-length encoding reused for an unrelated purpose, which is
    /// worth this note since it is easy to read as "this field is
    /// <paramref name="bitWidth"/> bytes long" instead.
    /// </summary>
    public void FieldUnit(string name, int bitWidth)
    {
        WriteSegment(name);
        Current.AddRange(EncodeVariableLength(bitWidth));
    }

    // ---- CreateWordField / CreateByteField ----

    /// <summary>CreateWordField(sourceBuffer, byteIndex, newName): aliases two bytes of an existing buffer under a new name, used by _CRS/_SRS to reach a resource template's own IRQ mask word.</summary>
    public void CreateWordField(Action writeSourceBuffer, uint byteIndex, string newName)
    {
        Raw(CreateWordFieldOp);
        writeSourceBuffer();
        Integer(byteIndex);
        WritePath(newName);
    }

    public void CreateByteField(Action writeSourceBuffer, uint byteIndex, string newName)
    {
        Raw(CreateByteFieldOp);
        writeSourceBuffer();
        Integer(byteIndex);
        WritePath(newName);
    }

    // ---- the executable operators _CRS/_SRS/_DIS need ----

    /// <summary>Store(source, target).</summary>
    public void Store(Action writeSource, Action writeTarget)
    {
        Raw(StoreOp);
        writeSource();
        writeTarget();
    }

    /// <summary>And(operandA, operandB, target).</summary>
    public void And(Action writeOperandA, Action writeOperandB, Action writeTarget)
    {
        Raw(AndOp);
        writeOperandA();
        writeOperandB();
        writeTarget();
    }

    /// <summary>Or(operandA, operandB, target).</summary>
    public void Or(Action writeOperandA, Action writeOperandB, Action writeTarget)
    {
        Raw(OrOp);
        writeOperandA();
        writeOperandB();
        writeTarget();
    }

    /// <summary>ShiftLeft(source, shiftCount, target).</summary>
    public void ShiftLeft(Action writeSource, Action writeShiftCount, Action writeTarget)
    {
        Raw(ShiftLeftOp);
        writeSource();
        writeShiftCount();
        writeTarget();
    }

    /// <summary>Decrement(target): modifies the named target in place.</summary>
    public void Decrement(Action writeTarget)
    {
        Raw(DecrementOp);
        writeTarget();
    }

    /// <summary>FindSetRightBit(operand, target): the 1-based bit position of the operand's lowest set bit, or 0 if it is zero.</summary>
    public void FindSetRightBit(Action writeOperand, Action writeTarget)
    {
        Raw(FindSetRightBitOp);
        writeOperand();
        writeTarget();
    }

    public void Return(Action writeValue)
    {
        Raw(ReturnOp);
        writeValue();
    }

    // ---- PkgLength -------------------------------------------------------

    /// <summary>
    /// The variable-length integer encoding ACPI §20.2.4 defines for
    /// PkgLength: six bits in a single byte when the value fits, otherwise a
    /// leading byte whose top two bits give one to three further bytes and
    /// whose low nibble holds the value's own low four bits.
    /// </summary>
    private static byte[] EncodeVariableLength(int value)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "AML variable-length values cannot be negative.");
        }

        if (value <= 0x3F) return [(byte)value];
        if (value <= 0xFFF) return [(byte)(0x40 | (value & 0x0F)), (byte)(value >> 4)];
        if (value <= 0xFFFFF) return [(byte)(0x80 | (value & 0x0F)), (byte)(value >> 4), (byte)(value >> 12)];
        if (value <= 0xFFFFFFF)
        {
            return
            [
                (byte)(0xC0 | (value & 0x0F)),
                (byte)(value >> 4),
                (byte)(value >> 12),
                (byte)(value >> 20),
            ];
        }

        throw new ArgumentOutOfRangeException(nameof(value), "AML variable-length value is too large to encode.");
    }

    /// <summary>
    /// PkgLength itself (§20.2.4): the same variable-length encoding, but
    /// counting its own byte length as part of the total it describes --
    /// which is why encoding it is a small fixed-point search rather than a
    /// direct call to <see cref="EncodeVariableLength"/>: the number of
    /// bytes PkgLength occupies depends on the total, and the total depends
    /// on how many bytes PkgLength occupies.
    /// </summary>
    private static byte[] EncodePkgLength(int contentLength)
    {
        for (int selfBytes = 1; selfBytes <= 4; selfBytes++)
        {
            byte[] encoded = EncodeVariableLength(contentLength + selfBytes);
            if (encoded.Length == selfBytes) return encoded;
        }

        throw new InvalidOperationException("AML PkgLength cannot encode a term this long.");
    }
}
