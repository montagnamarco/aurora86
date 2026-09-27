using System.Runtime.InteropServices;

namespace Aurora386.Core.Host.Midi;

/// <summary>
/// A host MIDI output discovered through WinMM without opening an endpoint.
/// The device ID is passed unchanged to the worker that eventually owns its
/// native handle.
/// </summary>
public sealed record MidiOutputDevice(uint DeviceId, string Name);

/// <summary>
/// The small native surface the WinMM worker needs. Keeping it injectable
/// lets lifetime tests model actual header ownership without loading a host
/// DLL or opening an endpoint.
/// </summary>
internal interface IWinMmApi
{
    uint Open(out nint handle, uint deviceId);
    uint SendShort(nint handle, uint message);
    uint Prepare(nint handle, nint header, uint size);
    uint SendLong(nint handle, nint header, uint size);
    uint Unprepare(nint handle, nint header, uint size);
    uint Reset(nint handle);
    uint Close(nint handle);
    uint GetNumDevs();
    uint GetDevCaps(nuint deviceId, out MidiOutCaps caps, uint size);
}

/// <summary>
/// The direct WinMM implementation of <see cref="IWinMmApi"/>. It has no
/// endpoint state: the output worker is the sole owner of opened handles.
/// </summary>
internal sealed class WinMmMidiApi : IWinMmApi
{
    private const uint CallbackNull = 0;

    /// <summary>The process-wide native API instance used by the MIDI worker.</summary>
    public static WinMmMidiApi Instance { get; } = new();

    private WinMmMidiApi()
    {
    }

    /// <summary>Returns the currently enumerable Windows MIDI outputs without opening any of them.</summary>
    public static IReadOnlyList<MidiOutputDevice> GetDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<MidiOutputDevice>();
        }

        uint deviceCount = Instance.GetNumDevs();
        var devices = new List<MidiOutputDevice>();
        uint capabilitiesSize = checked((uint)Marshal.SizeOf<MidiOutCaps>());
        for (uint deviceId = 0; deviceId < deviceCount; deviceId++)
        {
            uint result = Instance.GetDevCaps((nuint)deviceId, out MidiOutCaps capabilities, capabilitiesSize);
            if (result != WinMmResult.Success)
            {
                throw new IOException($"MIDI endpoint {deviceId} inventory failed with WinMM result {result}.");
            }

            devices.Add(new MidiOutputDevice(deviceId, capabilities.Name));
        }

        return devices;
    }

    /// <inheritdoc />
    public uint Open(out nint handle, uint deviceId)
    {
        return NativeOpen(out handle, deviceId, 0, 0, CallbackNull);
    }

    /// <inheritdoc />
    public uint SendShort(nint handle, uint message)
    {
        return NativeSendShort(handle, message);
    }

    /// <inheritdoc />
    public uint Prepare(nint handle, nint header, uint size)
    {
        return NativePrepare(handle, header, size);
    }

    /// <inheritdoc />
    public uint SendLong(nint handle, nint header, uint size)
    {
        return NativeSendLong(handle, header, size);
    }

    /// <inheritdoc />
    public uint Unprepare(nint handle, nint header, uint size)
    {
        return NativeUnprepare(handle, header, size);
    }

    /// <inheritdoc />
    public uint Reset(nint handle)
    {
        return NativeReset(handle);
    }

    /// <inheritdoc />
    public uint Close(nint handle)
    {
        return NativeClose(handle);
    }

    /// <inheritdoc />
    public uint GetNumDevs()
    {
        return NativeGetNumDevs();
    }

    /// <inheritdoc />
    public uint GetDevCaps(nuint deviceId, out MidiOutCaps caps, uint size)
    {
        return NativeGetDevCaps(deviceId, out caps, size);
    }

    [DllImport("winmm.dll", EntryPoint = "midiOutOpen")]
    private static extern uint NativeOpen(
        out nint handle,
        uint deviceId,
        nint callback,
        nuint instance,
        uint flags);

    [DllImport("winmm.dll", EntryPoint = "midiOutShortMsg")]
    private static extern uint NativeSendShort(nint handle, uint message);

    [DllImport("winmm.dll", EntryPoint = "midiOutPrepareHeader")]
    private static extern uint NativePrepare(nint handle, nint header, uint size);

    [DllImport("winmm.dll", EntryPoint = "midiOutLongMsg")]
    private static extern uint NativeSendLong(nint handle, nint header, uint size);

    [DllImport("winmm.dll", EntryPoint = "midiOutUnprepareHeader")]
    private static extern uint NativeUnprepare(nint handle, nint header, uint size);

    [DllImport("winmm.dll", EntryPoint = "midiOutReset")]
    private static extern uint NativeReset(nint handle);

    [DllImport("winmm.dll", EntryPoint = "midiOutClose")]
    private static extern uint NativeClose(nint handle);

    [DllImport("winmm.dll", EntryPoint = "midiOutGetNumDevs")]
    private static extern uint NativeGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "midiOutGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern uint NativeGetDevCaps(
        nuint deviceId,
        out MidiOutCaps caps,
        uint size);
}

/// <summary>WinMM return values that determine MIDI-header ownership and bounded retry behavior.</summary>
internal static class WinMmResult
{
    public const uint Success = 0;
    public const uint Unprepared = 64;
    public const uint StillPlaying = 65;
    public const uint NotReady = 67;
}

/// <summary>Flags WinMM sets on a MIDIHDR as it prepares, queues, and returns a long message.</summary>
internal static class MidiHeaderFlags
{
    public const uint Done = 0x00000001;
    public const uint Prepared = 0x00000002;
    public const uint InQueue = 0x00000004;
}

/// <summary>
/// WinMM's packed MIDIHDR. The Windows headers place this structure under
/// pack(1), so pointer-sized fields must not acquire the runtime's default
/// alignment padding on either supported process architecture.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct MidiHeader
{
    public nint Data;
    public uint BufferLength;
    public uint BytesRecorded;
    public nuint User;
    public uint Flags;
    public nint Next;
    public nuint Reserved;
    public uint Offset;
    public nuint Reserved0;
    public nuint Reserved1;
    public nuint Reserved2;
    public nuint Reserved3;
    public nuint Reserved4;
    public nuint Reserved5;
    public nuint Reserved6;
    public nuint Reserved7;
}

/// <summary>WinMM's Unicode MIDI output capability record, used only for endpoint inventory.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
internal struct MidiOutCaps
{
    public ushort ManufacturerId;
    public ushort ProductId;
    public uint DriverVersion;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string Name;

    public ushort Technology;
    public ushort Voices;
    public ushort Notes;
    public ushort ChannelMask;
    public uint Support;
}
