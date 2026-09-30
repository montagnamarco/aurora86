namespace Aurora86.Core.Devices.Audio;

/// <summary>
/// A silent MIDI destination for an MPU-401 UART with no host endpoint.
/// It accepts every byte so DOS software still observes a responsive UART.
/// </summary>
public sealed class NullMidiSink : IMidiSink
{
    /// <summary>The shared stateless sink used when the caller supplies no endpoint.</summary>
    public static NullMidiSink Instance { get; } = new();

    private NullMidiSink()
    {
    }

    /// <inheritdoc />
    public bool TryWriteByte(byte value)
    {
        return true;
    }

    /// <inheritdoc />
    public void Reset()
    {
    }
}
