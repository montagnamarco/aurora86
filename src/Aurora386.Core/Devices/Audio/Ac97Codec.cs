namespace Aurora386.Core.Devices.Audio;

/// <summary>
/// A generic AC'97 2.1 codec register file, reached only through the ES1371's
/// own indirect CODEC port (see <see cref="Es1371"/>'s summary for that
/// protocol) -- there is no direct bus of its own here, exactly as a real
/// AC'97 codec sits behind its host controller's "AC-link" rather than on the
/// PCI bus itself.
///
/// "Generic" is the coordinator's decision E5 in <c>plans/specs/es1371.md</c>:
/// nothing here models a specific codec's own mixer topology or quirks (a
/// SigmaTel STAC97xx and a Crystal CS42xx differ in plenty of detail), only
/// the fixed identity and rate registers every AC'97 codec of the era exposes
/// the same way and that a driver actually probes -- variable rate audio
/// (VRA), the front DAC and ADC rate registers, and the vendor ID pair.
/// Every other register is plain read/write storage, read back exactly as
/// written, which is enough for a driver that only ever writes a volume or a
/// power-down bit and never reads it back to notice.
/// </summary>
public sealed class Ac97Codec
{
    /// <summary>64 word registers, at byte offsets 00h-7Eh -- the whole AC'97 "mixer" register space.</summary>
    public const int RegisterCount = 64;

    // --- special-cased registers; everything else is generic RW storage ----

    /// <summary>Any write here, regardless of the value, reloads every register's power-on default -- real AC'97 hardware behaves the same way.</summary>
    private const int ResetRegister = 0x00;

    private const int MasterVolumeRegister = 0x02;
    private const int PcmOutVolumeRegister = 0x18;

    /// <summary>Extended Audio ID: bit 0 is VRA. Fixed at 1 (E5) -- this codec always advertises variable rate audio.</summary>
    private const int ExtendedAudioIdRegister = 0x28;

    /// <summary>Extended Audio Control/Status: bit 0 enables variable rate audio once the ID register above has advertised it.</summary>
    private const int ExtendedAudioControlRegister = 0x2A;

    private const int FrontDacRateRegister = 0x2C;
    private const int AdcRateRegister = 0x32;
    private const int VendorId1Register = 0x7C;
    private const int VendorId2Register = 0x7E;

    private const ushort ExtendedAudioIdValue = 0x0001; // VRA, fixed (E5)
    private const ushort VariableRateAudioEnableBit = 0x0001; // 2Ah bit 0

    /// <summary>48000 Hz, the AC'97 power-on default for both rate registers, and what they report whenever VRA is not enabled (E5).</summary>
    private const ushort DefaultSampleRate = 0xBB80;

    /// <summary>
    /// SigmaTel STAC9721, the codec Creative fitted to its own SB PCI 128
    /// boards -- the coordinator's decision E5 notes this identity is
    /// uncertain and cosmetic (no driver behaviour depends on it), kept only
    /// because reading all-zero or all-one vendor ID words is itself a
    /// plausible "no codec present" signal some drivers act on.
    /// </summary>
    private const ushort VendorId1Value = 0x8384;
    private const ushort VendorId2Value = 0x7609;

    /// <summary>Every register not special-cased above: plain storage, indexed by byte offset / 2.</summary>
    private readonly ushort[] _registers = new ushort[RegisterCount];

    public Ac97Codec()
    {
        Reset();
    }

    /// <summary>Whether the driver has asked for variable rate audio: only then do the rate registers hold what was written rather than the fixed 48000 Hz default (E5).</summary>
    private bool VariableRateEnabled => (_registers[ExtendedAudioControlRegister >> 1] & VariableRateAudioEnableBit) != 0;

    /// <summary>Puts every register back to its power-on default -- what a write to <see cref="ResetRegister"/> also does.</summary>
    public void Reset()
    {
        Array.Clear(_registers);

        // Both channels muted at maximum attenuation is the AC'97-documented
        // power-on default for the two volume registers; nothing else here
        // has a documented non-zero default besides the rate registers below.
        _registers[MasterVolumeRegister >> 1] = 0x8000;
        _registers[PcmOutVolumeRegister >> 1] = 0x8000;

        _registers[FrontDacRateRegister >> 1] = DefaultSampleRate;
        _registers[AdcRateRegister >> 1] = DefaultSampleRate;
    }

    /// <summary>Reads one AC'97 register by its byte offset (an even value 00h-7Eh; the low bit is ignored, as real hardware's own register addressing never sets it).</summary>
    public ushort ReadRegister(byte byteOffset)
    {
        if (byteOffset == ExtendedAudioIdRegister)
        {
            return ExtendedAudioIdValue;
        }

        if (byteOffset == VendorId1Register)
        {
            return VendorId1Value;
        }

        if (byteOffset == VendorId2Register)
        {
            return VendorId2Value;
        }

        if ((byteOffset == FrontDacRateRegister || byteOffset == AdcRateRegister) && !VariableRateEnabled)
        {
            // Fixed-rate mode: the codec is not being asked to run its DAC or
            // ADC at anything but the standard 48 kHz, whatever was last
            // written into the register while VRA was off.
            return DefaultSampleRate;
        }

        return _registers[(byteOffset & 0x7E) >> 1];
    }

    /// <summary>Writes one AC'97 register by its byte offset. See <see cref="ReadRegister"/> for the addressing convention.</summary>
    public void WriteRegister(byte byteOffset, ushort value)
    {
        if (byteOffset == ResetRegister)
        {
            Reset();
            return;
        }

        _registers[(byteOffset & 0x7E) >> 1] = value;
    }

    // --- snapshot: embedded in Es1371.Capture/Restore, not a section of its own ---

    /// <summary>Writes every register in offset order. See <see cref="Es1371.Capture"/> for the enclosing payload.</summary>
    public void Capture(BinaryWriter writer)
    {
        foreach (ushort register in _registers)
        {
            writer.Write(register);
        }
    }

    /// <summary>Restores what <see cref="Capture"/> wrote. See <see cref="Es1371.Restore"/>.</summary>
    public void Restore(BinaryReader reader)
    {
        for (int i = 0; i < _registers.Length; i++)
        {
            _registers[i] = reader.ReadUInt16();
        }
    }
}
