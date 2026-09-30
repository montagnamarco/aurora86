namespace Aurora86.Tests;

/// <summary>
/// A small, test-side AML reader for the DSDT <c>AcpiTables.BuildDsdt</c>
/// produces (work package P4.5, coordinator decision C3: "no external
/// tool... tests decode the produced AML with a small test-side reader").
///
/// It is not a general ACPI interpreter. It understands exactly the
/// container and value shapes <c>AmlWriter</c> emits -- Scope, Device,
/// Processor, Name (of an Integer, a Package or a Buffer) and Method (kept
/// as raw bytes, for <see cref="AmlMethodExecutor"/> to run) -- and nothing
/// else; anything else in the byte stream would mean the writer had changed
/// underneath it, which is exactly the failure this reader exists to catch.
/// </summary>
public sealed class AmlNode
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public List<AmlNode> Children { get; } = [];
    public object? Value { get; set; }
    public byte[]? MethodBody { get; set; }
    public int MethodArgCount { get; set; }

    public AmlNode? Find(string relativePath)
    {
        AmlNode current = this;
        foreach (string segment in relativePath.Split('.'))
        {
            AmlNode? next = current.Children.FirstOrDefault(child => child.Name == segment);
            if (next is null) return null;
            current = next;
        }

        return current;
    }
}

/// <summary>A named reference inside a Package element (ACPI §6.2.13's own \_PRT Source, for instance): kept as a string rather than resolved, since the test only needs to compare it against an expected path.</summary>
public readonly record struct AmlNameReference(string Path);

public static class AcpiAmlReader
{
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
    private const byte ExtOpPrefix = 0x5B;
    private const byte ExtOpRegionOp = 0x80;
    private const byte ExtFieldOp = 0x81;
    private const byte ExtDeviceOp = 0x82;
    private const byte ExtProcessorOp = 0x83;

    /// <summary>Parses a whole DSDT body (the bytes after the 36-byte SDT header) into a synthetic root node named "\\".</summary>
    public static AmlNode ParseDefinitionBlock(byte[] amlBody)
    {
        var root = new AmlNode { Name = "\\", Kind = "Scope" };
        int pos = 0;
        ParseTermList(amlBody, ref pos, amlBody.Length, root);
        return root;
    }

    private static void ParseTermList(byte[] data, ref int pos, int end, AmlNode parent)
    {
        while (pos < end)
        {
            byte opcode = data[pos];
            if (opcode == NameOp)
            {
                pos++;
                string name = ReadNameString(data, ref pos);
                object value = ReadDataRefObject(data, ref pos);
                parent.Children.Add(new AmlNode { Name = LastSegment(name), Kind = "Name", Value = value });
                continue;
            }

            if (opcode == ScopeOp)
            {
                pos++;
                int contentEnd = ReadPkgLengthEnd(data, ref pos);
                string name = ReadNameString(data, ref pos);
                var node = new AmlNode { Name = LastSegment(name), Kind = "Scope" };
                ParseTermList(data, ref pos, contentEnd, node);
                parent.Children.Add(node);
                pos = contentEnd;
                continue;
            }

            if (opcode == MethodOp)
            {
                pos++;
                int contentEnd = ReadPkgLengthEnd(data, ref pos);
                string name = ReadNameString(data, ref pos);
                byte flags = data[pos++];
                var node = new AmlNode
                {
                    Name = LastSegment(name),
                    Kind = "Method",
                    MethodArgCount = flags & 0x07,
                    MethodBody = data[pos..contentEnd],
                };
                parent.Children.Add(node);
                pos = contentEnd;
                continue;
            }

            if (opcode == ExtOpPrefix)
            {
                byte extOpcode = data[pos + 1];
                if (extOpcode == ExtDeviceOp || extOpcode == ExtProcessorOp)
                {
                    pos += 2;
                    int contentEnd = ReadPkgLengthEnd(data, ref pos);
                    string name = ReadNameString(data, ref pos);
                    string kind = extOpcode == ExtDeviceOp ? "Device" : "Processor";
                    if (kind == "Processor")
                    {
                        pos += 6;   // ProcID(1) + PBLKAddress(4) + PBLKLength(1): this reader does not need them.
                    }

                    var node = new AmlNode { Name = LastSegment(name), Kind = kind };
                    ParseTermList(data, ref pos, contentEnd, node);
                    parent.Children.Add(node);
                    pos = contentEnd;
                    continue;
                }

                if (extOpcode == ExtOpRegionOp)
                {
                    // OperationRegion(name, space, offset, length): flat, no PkgLength.
                    pos += 2;
                    ReadNameString(data, ref pos);
                    pos++;                              // RegionSpace.
                    ReadDataRefObject(data, ref pos);    // RegionOffset.
                    ReadDataRefObject(data, ref pos);    // RegionLen.
                    continue;
                }

                if (extOpcode == ExtFieldOp)
                {
                    // Field(regionName, flags){ NameSeg PkgLength ... }: not needed by any test case's own assertions, so its contents are skipped rather than modelled.
                    pos += 2;
                    int contentEnd = ReadPkgLengthEnd(data, ref pos);
                    pos = contentEnd;
                    continue;
                }

                throw new NotSupportedException($"AcpiAmlReader does not know ExtOp 0x{extOpcode:X2}.");
            }

            throw new NotSupportedException($"AcpiAmlReader does not know opcode 0x{opcode:X2} at position {pos}.");
        }
    }

    /// <summary>A TermArg that is a data object: Integer, Package or Buffer -- everything a Name term's own value, or a Package element, can be.</summary>
    private static object ReadDataRefObject(byte[] data, ref int pos)
    {
        byte opcode = data[pos];
        switch (opcode)
        {
            case ZeroOp: pos++; return 0UL;
            case OneOp: pos++; return 1UL;
            case BytePrefix: pos++; return (ulong)data[pos++];
            case WordPrefix:
            {
                pos++;
                ulong value = (ulong)data[pos] | ((ulong)data[pos + 1] << 8);
                pos += 2;
                return value;
            }
            case DWordPrefix:
            {
                pos++;
                ulong value = (ulong)data[pos] | ((ulong)data[pos + 1] << 8)
                    | ((ulong)data[pos + 2] << 16) | ((ulong)data[pos + 3] << 24);
                pos += 4;
                return value;
            }
            case PackageOp:
            {
                pos++;
                int contentEnd = ReadPkgLengthEnd(data, ref pos);
                int numElements = data[pos++];
                var elements = new List<object>(numElements);
                for (int i = 0; i < numElements; i++)
                {
                    elements.Add(ReadDataRefObject(data, ref pos));
                }

                pos = contentEnd;
                return elements;
            }
            case BufferOp:
            {
                pos++;
                int contentEnd = ReadPkgLengthEnd(data, ref pos);
                ulong size = (ulong)ReadDataRefObject(data, ref pos);
                byte[] bytes = data[pos..(pos + (int)size)];
                pos = contentEnd;
                return bytes;
            }
            default:
                // A Package element that names another object (\_SB.PCI0.ISA.LNKA, for _PRT's own Source), rather than holding a value of its own.
                return new AmlNameReference(ReadNameString(data, ref pos));
        }
    }

    /// <summary>Reads a PkgLength and returns the absolute end position of the term it introduces, leaving <paramref name="pos"/> just past the PkgLength's own bytes. Shared with <see cref="AmlMethodExecutor"/>, which needs it for a literal Buffer term inside a method body.</summary>
    internal static int ReadPkgLengthEnd(byte[] data, ref int pos)
    {
        int start = pos;
        byte lead = data[pos];
        int extraBytes = lead >> 6;
        int value;
        if (extraBytes == 0)
        {
            value = lead & 0x3F;
            pos += 1;
        }
        else
        {
            value = lead & 0x0F;
            for (int i = 0; i < extraBytes; i++)
            {
                value |= data[pos + 1 + i] << (4 + 8 * i);
            }

            pos += 1 + extraBytes;
        }

        return start + value;
    }

    /// <summary>Reads a NameString (§20.2.2): an optional root, then dual/multi/single name segments. Returns it dot-joined, root included as a leading empty segment (so a root path always starts with '.').</summary>
    public static string ReadNameString(byte[] data, ref int pos)
    {
        var prefix = new List<string>();
        if (data[pos] == RootChar)
        {
            prefix.Add(string.Empty);
            pos++;
        }

        while (data[pos] == 0x5E)   // ParentPrefixChar: not used by this DSDT, but skip correctly if it appears.
        {
            prefix.Add("^");
            pos++;
        }

        var segments = new List<string>();
        if (data[pos] == DualNamePrefix)
        {
            pos++;
            segments.Add(ReadSegment(data, ref pos));
            segments.Add(ReadSegment(data, ref pos));
        }
        else if (data[pos] == MultiNamePrefix)
        {
            pos++;
            int count = data[pos++];
            for (int i = 0; i < count; i++) segments.Add(ReadSegment(data, ref pos));
        }
        else if (data[pos] == 0x00 && prefix.Count > 0)
        {
            // NullName: a bare root or parent prefix with nothing following.
            pos++;
        }
        else
        {
            segments.Add(ReadSegment(data, ref pos));
        }

        return string.Join('.', prefix.Concat(segments));
    }

    private static string ReadSegment(byte[] data, ref int pos)
    {
        string segment = System.Text.Encoding.ASCII.GetString(data, pos, 4);
        pos += 4;
        return segment.TrimEnd('_');
    }

    private static string LastSegment(string path) => path.Length == 0 ? path : path[(path.LastIndexOf('.') + 1)..];

    /// <summary>
    /// Converts an absolute path written the way plans/specs/acpi.md's own
    /// prose does ("\_SB.PCI0.ISA.LNKA") into this reader's own
    /// root-relative representation (".{_SB.PCI0.ISA.LNKA}"), so a test can
    /// compare an <see cref="AmlNameReference"/>'s path directly against one.
    /// </summary>
    public static string NormalizeAbsolutePath(string aslStylePath)
        => aslStylePath.StartsWith('\\') ? "." + aslStylePath[1..] : aslStylePath;
}
