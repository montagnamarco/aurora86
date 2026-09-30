using Aurora86.Core.Devices.Video;

namespace Aurora86.Core;

public sealed partial class Machine
{
    /// <summary>The video output contract consumed by emulator frontends.</summary>
    public IVideoOutput Display { get; }
}
