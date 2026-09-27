namespace Aurora386.Core.Host.Midi;

/// <summary>
/// Receives complete MIDI wire messages from the synchronous UART byte
/// framer. Implementations consume short messages directly and must copy a
/// SysEx span before this callback returns if they need to retain it.
/// Callbacks must not reenter the framer. If a callback throws, its owner
/// must reset or discard the framer before supplying any more input.
/// </summary>
public interface IMidiMessageSink
{
    /// <summary>Receives one packed short message with its status in bits zero through seven.</summary>
    void WriteShort(uint message);

    /// <summary>Receives one complete SysEx message, including F0 and F7.</summary>
    void WriteSysEx(ReadOnlySpan<byte> message);
}
