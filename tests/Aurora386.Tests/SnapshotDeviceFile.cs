using System.Buffers.Binary;

namespace Aurora386.Tests;

/// <summary>
/// Locates the bounded DVS1 suffix in generated PC snapshot fixtures so tests
/// can construct historical files independently of current device payload sizes.
/// </summary>
internal static class SnapshotDeviceFile
{
    internal static int TrailerOffset(byte[] bytes)
    {
        for (int offset = bytes.Length - 12; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)) == 0x31535644
                && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)) == bytes.Length - offset - 4)
            {
                return offset;
            }
        }
        throw new InvalidDataException("Missing generated DVS1 trailer.");
    }

    internal static byte[] WithoutTrailer(byte[] bytes) => bytes[..TrailerOffset(bytes)];

    internal static int RecordOffset(byte[] bytes, ushort kind)
    {
        int trailer = TrailerOffset(bytes);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(trailer + 10));
        int cursor = trailer + 12;
        for (int index = 0; index < count; index++)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor)) == kind)
            {
                return cursor;
            }
            cursor += 12 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor + 8));
        }
        throw new InvalidDataException("Missing generated device record.");
    }
}
