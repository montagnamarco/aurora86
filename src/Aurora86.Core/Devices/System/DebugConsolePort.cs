using System.Text;

using Aurora86.Core.IO;

namespace Aurora86.Core.Devices.System;

/// <summary>
/// The debug console port (0xE9), by which a guest writes diagnostics
/// straight to the host.
///
/// On a real PC port 0xE9 is unassigned: nothing drives it, a write goes
/// nowhere, and a read returns the idle bus. Bochs took advantage of exactly
/// that and made writes to it appear on the host's console; QEMU and others
/// followed. Nobody ever standardised the arrangement, which is precisely
/// what makes it useful -- the same guest produces diagnostics on every
/// emulator that honours the custom, and on real hardware the writes are
/// harmlessly discarded rather than faulting.
///
/// Why it earns its place here. A guest operating system under development
/// can otherwise only be judged by looking at its screen, which is slow,
/// subjective, and impossible to put in an automated test: reading text back
/// out of video memory means knowing the video mode, the font and the
/// scrollback, and it tells you nothing once the guest switches to graphics.
/// A byte stream costs the guest one OUT instruction and gives the host
/// something it can compare against an expectation.
///
/// The reading side is deliberately not the idle bus. A guest that wants to
/// know whether anybody is listening writes nothing and reads: this port
/// answers with its own number, 0xE9, which is the signature Bochs
/// established and the one guests already probe for. An unattached port
/// would read 0xFF, so a guest can tell the two apart and fall back to its
/// screen when it is running somewhere that does not implement this.
/// </summary>
public sealed class DebugConsolePort : IIoDevice
{
    /// <summary>
    /// The port of the Bochs custom. Named rather than written as a bare
    /// number at each use, in the house rule for hardware constants.
    /// </summary>
    public const ushort Port = 0xE9;

    /// <summary>
    /// What a read returns: the port's own number. This is the established
    /// signature, and a guest uses it to distinguish an emulator that
    /// forwards these bytes from one that does not.
    /// </summary>
    public const byte Signature = 0xE9;

    private readonly TextWriter? _destination;

    /// <summary>
    /// Everything the guest has written, kept in memory.
    ///
    /// A test harness wants the whole stream at the end of a run so it can
    /// look for its markers; it does not want to reopen and parse a file it
    /// just watched being written. Keeping the text costs a few kilobytes
    /// for the runs this is used in.
    /// </summary>
    public StringBuilder Text { get; } = new();

    /// <summary>Number of bytes the guest has written to the port.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>
    /// Raised for each byte, so a caller can watch the stream as it arrives
    /// rather than only after the run -- which is what lets a harness stop
    /// early on an end marker instead of always running to its limit.
    /// </summary>
    public event Action<byte>? ByteWritten;

    /// <param name="destination">
    /// Where to mirror the bytes as they arrive, or null to keep them only in
    /// <see cref="Text"/>. A harness passes null; an interactive run passes a
    /// file or the console.
    /// </param>
    public DebugConsolePort(TextWriter? destination = null) => _destination = destination;

    public void Register(IoBus io) => io.Register(Port, this);

    public byte ReadByte(ushort port) => Signature;

    public void WriteByte(ushort port, byte value)
    {
        BytesWritten++;

        // The guest sends bytes, not characters. Treating them as Latin-1 is
        // what Bochs does and keeps every byte reversible: each of the 256
        // values maps to exactly one character and back, so a guest writing
        // UTF-8 still survives the trip and can be decoded by the reader.
        Text.Append((char)value);

        if (_destination is not null)
        {
            _destination.Write((char)value);

            // Flushed at the end of each line rather than each byte: a guest
            // that halts or faults mid-run must still leave its last
            // complete message on disk, and that message is usually the one
            // that says why.
            if (value == (byte)'\n') _destination.Flush();
        }

        ByteWritten?.Invoke(value);
    }
}
