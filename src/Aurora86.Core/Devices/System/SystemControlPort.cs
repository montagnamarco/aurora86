using Aurora86.Core.IO;
using Aurora86.Core.Memory;

namespace Aurora86.Core.Devices.System;

/// <summary>
/// System control port (0x92), introduced with the PS/2.
///
/// Toggling A20 through the keyboard controller means polling until its input
/// buffer drains and costs thousands of cycles -- an absurd price for
/// flipping one address line. This port does the same job in a single OUT,
/// and it is the route DOS 6.22 and Windows actually take. Bit 1 is A20,
/// bit 0 triggers a fast CPU reset.
/// </summary>
public sealed class SystemControlPort : IIoDevice
{
    private readonly PhysicalMemoryBus _memory;
    // Mirrors the last value written; A20 itself starts out disabled at power-on.
    private byte _value;

    /// <summary>Set when a fast CPU reset has been requested.</summary>
    public bool ResetRequested { get; set; }

    public SystemControlPort(PhysicalMemoryBus memory) => _memory = memory;

    public void Register(IoBus io) => io.Register(0x92, this);

    public byte ReadByte(ushort port)
    {
        // Bit 1 reports the line's real state rather than what was last
        // written here, since the keyboard controller may have changed it
        // behind our back.
        return (byte)((_value & ~0x02) | (_memory.A20Enabled ? 0x02 : 0x00));
    }

    public void WriteByte(ushort port, byte value)
    {
        _value = value;

        _memory.A20Enabled = (value & 0x02) != 0;

        // Bit 0 is the fast reset, and note the inverted sense compared to
        // the keyboard controller's output port: here a 1 triggers it, there
        // it was a 0.
        if ((value & 0x01) != 0)
        {
            ResetRequested = true;
        }
    }
}
