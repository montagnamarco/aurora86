using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using Aurora86.Core;

namespace Aurora86.Tests;

/// <summary>
/// Reaches into one section of a <see cref="MachineSnapshot"/>, both while it
/// is still held in memory -- through its private <c>_sections</c> dictionary
/// -- and once it has been written to a sectioned file, so a test can corrupt
/// one section's payload, version or length without depending on the byte
/// offsets of the "DVS1"/"VID1" tails the emulator no longer writes. A file's
/// layout is: the 8-byte "AURASNAP" magic, then repeated sections of a
/// 4-letter ASCII name, a little-endian ushort version, a little-endian
/// int32 payload length, and the payload itself.
/// </summary>
internal static class SnapshotSectionFile
{
    private const int MagicLength = 8;
    private const int NameLength = 4;
    private const int VersionLength = sizeof(ushort);
    private const int LengthFieldLength = sizeof(int);
    private const int HeaderLength = NameLength + VersionLength + LengthFieldLength;

    private static readonly Type SectionType = typeof(MachineSnapshot)
        .GetNestedType("Section", BindingFlags.NonPublic)!;
    private static readonly FieldInfo SectionsField = typeof(MachineSnapshot)
        .GetField("_sections", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly PropertyInfo VersionProperty = SectionType.GetProperty("Version")!;
    private static readonly PropertyInfo PayloadProperty = SectionType.GetProperty("Payload")!;

    // ---- an in-memory snapshot, before it is ever saved ----------------------

    /// <summary>
    /// The live payload array backing one section of an in-memory snapshot.
    /// The array is the one the snapshot itself holds, not a copy, so a
    /// caller can corrupt one byte of it in place without calling <see
    /// cref="SetPayload"/> back.
    /// </summary>
    internal static byte[] GetPayload(MachineSnapshot snapshot, string sectionName)
        => (byte[])PayloadProperty.GetValue(GetSection(snapshot, sectionName))!;

    internal static int GetVersion(MachineSnapshot snapshot, string sectionName)
        => (int)VersionProperty.GetValue(GetSection(snapshot, sectionName))!;

    /// <summary>Replaces a section's payload, keeping its version, for a test that must resize the array.</summary>
    internal static void SetPayload(MachineSnapshot snapshot, string sectionName, byte[] payload)
        => GetSections(snapshot)[sectionName]
            = Activator.CreateInstance(SectionType, GetVersion(snapshot, sectionName), payload)!;

    private static object GetSection(MachineSnapshot snapshot, string sectionName)
        => GetSections(snapshot)[sectionName]!;

    private static IDictionary GetSections(MachineSnapshot snapshot)
        => (IDictionary)SectionsField.GetValue(snapshot)!;

    // ---- a snapshot already written to a sectioned file ----------------------

    private readonly record struct Location(int SectionOffset, int PayloadOffset, int PayloadLength);

    private static Location Locate(byte[] bytes, string sectionName)
    {
        int offset = MagicLength;
        while (offset + HeaderLength <= bytes.Length)
        {
            string name = Encoding.ASCII.GetString(bytes, offset, NameLength);
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
                bytes.AsSpan(offset + NameLength + VersionLength));
            int payloadOffset = offset + HeaderLength;

            if (name == sectionName)
            {
                return new Location(offset, payloadOffset, payloadLength);
            }

            offset = payloadOffset + payloadLength;
        }

        throw new InvalidDataException($"Section '{sectionName}' is not in this file.");
    }

    /// <summary>The bytes of one section's payload, as they sit in a saved file.</summary>
    internal static byte[] GetPayload(byte[] bytes, string sectionName)
    {
        Location location = Locate(bytes, sectionName);
        return bytes.AsSpan(location.PayloadOffset, location.PayloadLength).ToArray();
    }

    /// <summary>A copy of the file with one section's payload replaced and its length field updated to match.</summary>
    internal static byte[] WithPayload(byte[] bytes, string sectionName, byte[] newPayload)
    {
        Location location = Locate(bytes, sectionName);
        var result = new byte[bytes.Length - location.PayloadLength + newPayload.Length];

        bytes.AsSpan(0, location.PayloadOffset).CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(
            result.AsSpan(location.SectionOffset + NameLength + VersionLength), newPayload.Length);
        newPayload.CopyTo(result.AsSpan(location.PayloadOffset));
        bytes.AsSpan(location.PayloadOffset + location.PayloadLength)
            .CopyTo(result.AsSpan(location.PayloadOffset + newPayload.Length));

        return result;
    }

    /// <summary>A copy of the file with one byte of a section's payload flipped, leaving its length untouched.</summary>
    internal static byte[] WithCorruptedPayloadByte(byte[] bytes, string sectionName, int payloadOffset, byte xor = 0xFF)
    {
        Location location = Locate(bytes, sectionName);
        var result = (byte[])bytes.Clone();
        result[location.PayloadOffset + payloadOffset] ^= xor;
        return result;
    }

    /// <summary>A copy of the file with one section's declared version changed, its payload untouched.</summary>
    internal static byte[] WithVersion(byte[] bytes, string sectionName, ushort version)
    {
        Location location = Locate(bytes, sectionName);
        var result = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(location.SectionOffset + NameLength), version);
        return result;
    }

    /// <summary>A copy of the file with one section's declared length field changed, its payload bytes untouched.</summary>
    internal static byte[] WithLengthField(byte[] bytes, string sectionName, int length)
    {
        Location location = Locate(bytes, sectionName);
        var result = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(
            result.AsSpan(location.SectionOffset + NameLength + VersionLength), length);
        return result;
    }

    /// <summary>A copy of the file with one section removed entirely, as if it had never been captured.</summary>
    internal static byte[] WithoutSection(byte[] bytes, string sectionName)
    {
        Location location = Locate(bytes, sectionName);
        int sectionEnd = location.PayloadOffset + location.PayloadLength;
        var result = new byte[bytes.Length - (sectionEnd - location.SectionOffset)];

        bytes.AsSpan(0, location.SectionOffset).CopyTo(result);
        bytes.AsSpan(sectionEnd).CopyTo(result.AsSpan(location.SectionOffset));

        return result;
    }
}
