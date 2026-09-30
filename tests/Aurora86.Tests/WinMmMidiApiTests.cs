using System.Runtime.InteropServices;
using Aurora86.Core.Host.Midi;

namespace Aurora86.Tests;

/// <summary>
/// The WinMM declarations are checked without opening a native endpoint:
/// pointer-sized fields must retain the SDK's packed wire layout.
/// </summary>
public sealed class WinMmMidiApiTests
{
    [Fact]
    public void MidiHeaderUsesTheSdkPackedPointerSizedLayout()
    {
        int pointerSize = IntPtr.Size;
        int expectedSize = pointerSize == 8 ? 112 : 64;

        Assert.Equal(expectedSize, Marshal.SizeOf<MidiHeader>());
        Assert.Equal(0, OffsetOf<MidiHeader>(nameof(MidiHeader.Data)));
        Assert.Equal(pointerSize, OffsetOf<MidiHeader>(nameof(MidiHeader.BufferLength)));
        Assert.Equal(pointerSize + 4, OffsetOf<MidiHeader>(nameof(MidiHeader.BytesRecorded)));
        Assert.Equal(pointerSize + 8, OffsetOf<MidiHeader>(nameof(MidiHeader.User)));
        Assert.Equal(pointerSize * 2 + 8, OffsetOf<MidiHeader>(nameof(MidiHeader.Flags)));
        Assert.Equal(pointerSize * 2 + 12, OffsetOf<MidiHeader>(nameof(MidiHeader.Next)));
        Assert.Equal(pointerSize * 3 + 12, OffsetOf<MidiHeader>(nameof(MidiHeader.Reserved)));
        Assert.Equal(pointerSize * 4 + 12, OffsetOf<MidiHeader>(nameof(MidiHeader.Offset)));
        Assert.Equal(pointerSize * 4 + 16, OffsetOf<MidiHeader>(nameof(MidiHeader.Reserved0)));
        Assert.Equal(pointerSize * 11 + 16, OffsetOf<MidiHeader>(nameof(MidiHeader.Reserved7)));
    }

    [Fact]
    public void MidiOutCapsUsesTheUnicodeSdkLayout()
    {
        Assert.Equal(84, Marshal.SizeOf<MidiOutCaps>());
        Assert.Equal(0, OffsetOf<MidiOutCaps>(nameof(MidiOutCaps.ManufacturerId)));
        Assert.Equal(8, OffsetOf<MidiOutCaps>(nameof(MidiOutCaps.Name)));
        Assert.Equal(72, OffsetOf<MidiOutCaps>(nameof(MidiOutCaps.Technology)));
        Assert.Equal(80, OffsetOf<MidiOutCaps>(nameof(MidiOutCaps.Support)));
    }

    [Fact]
    public void HeaderFlagsAndWinMmResultsUseTheSdkNumericValues()
    {
        Assert.Equal(0u, WinMmResult.Success);
        Assert.Equal(64u, WinMmResult.Unprepared);
        Assert.Equal(65u, WinMmResult.StillPlaying);
        Assert.Equal(67u, WinMmResult.NotReady);
        Assert.Equal(1u, MidiHeaderFlags.Done);
        Assert.Equal(2u, MidiHeaderFlags.Prepared);
        Assert.Equal(4u, MidiHeaderFlags.InQueue);
    }

    [Fact]
    public void InventoryDoesNotLoadWinMmOnUnsupportedOperatingSystems()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(WinMmMidiApi.GetDevices());
        }
    }

    private static int OffsetOf<T>(string fieldName)
    {
        return Marshal.OffsetOf<T>(fieldName).ToInt32();
    }
}
