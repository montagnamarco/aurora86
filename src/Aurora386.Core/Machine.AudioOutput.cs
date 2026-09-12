using Aurora386.Core.Devices.Audio;

namespace Aurora386.Core;

public sealed partial class Machine
{
    /// <summary>The audio output contract consumed by emulator frontends.</summary>
    public IAudioOutput Audio => SoundBlaster;
}
