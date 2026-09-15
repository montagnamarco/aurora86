namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The original 1996 ViRGE 86C325's CRTC address decoders. Drivers unlock
/// these registers through VGA ports and PCI writes share their address bits.
/// The card owner subscribes to changes to rebuild its physical mappings.
/// Latches live in the VGA register bank so its existing snapshot saves them.
/// </summary>
public sealed class S3VirgeApertureControls
{
    private const byte ConfigurationRegister = 0x36;
    private const byte ExtendedLockRegister = 0x39;
    private const byte MemoryControlRegister = 0x53;
    private const byte LinearControlRegister = 0x58;
    private const byte BaseHighRegister = 0x59;
    private const byte BaseLowRegister = 0x5A;
    private readonly byte[] _crtc;

    internal S3VirgeApertureControls(byte[] crtc)
    {
        _crtc = crtc;

        // ViRGE databook sections 18-7, 18-17 and 18-20/21: PCI, 2 MB,
        // new MMIO selected, linear access disabled, address 70000000h.
        _crtc[ConfigurationRegister] = 0x82;
        _crtc[MemoryControlRegister] = 0x08;
        _crtc[LinearControlRegister] = 0;
        _crtc[BaseHighRegister] = 0x70;
        _crtc[BaseLowRegister] = 0;
    }

    public event Action? Changed;

    public uint RawLinearBase => ((uint)_crtc[BaseHighRegister] << 24)
        | ((uint)_crtc[BaseLowRegister] << 16);
    public uint PciBase => RawLinearBase & 0xFC000000;
    public uint LinearWindowSize => (_crtc[LinearControlRegister] & 3) switch
    {
        0 => 0x10000,
        1 => 0x100000,
        2 => 0x200000,
        _ => 0x400000
    };
    public bool LinearEnabled => (_crtc[LinearControlRegister] & 0x10) != 0;
    public bool NewMmioEnabled => (_crtc[MemoryControlRegister] & 0x08) != 0;
    public bool OldMmioEnabled => (_crtc[MemoryControlRegister] & 0x10) != 0;
    public bool OldMmioAtB8000 => (_crtc[MemoryControlRegister] & 0x20) != 0;

    /// <summary>
    /// Updates the PCI BAR's six address bits. Preserving the lower ten CRTC
    /// address bits follows 86Box commit 51a3f7683e9691832e423a90ad9cf8b39cd7053e,
    /// s3_virge_pci_write case 13h. The databook calls those bits indeterminate
    /// after a PCI write; this is a deterministic reference choice.
    /// </summary>
    public void SetPciBaseAddress(uint value)
    {
        uint preservedAddressBits = (uint)_crtc[BaseHighRegister] & 3;
        byte high = (byte)(preservedAddressBits | ((value >> 24) & 0xFC));
        SetRawLinearBase(((uint)high << 24) | ((uint)_crtc[BaseLowRegister] << 16));
    }

    /// <summary>Sets both host-supplied address latches before notifying the owner.</summary>
    internal void SetRawLinearBase(uint value)
    {
        uint aligned = value & 0xFFFF0000;
        if (RawLinearBase == aligned)
        {
            return;
        }

        _crtc[BaseHighRegister] = (byte)(aligned >> 24);
        _crtc[BaseLowRegister] = (byte)(aligned >> 16);
        Changed?.Invoke();
    }

    internal bool TryReadCrtc(byte index, out byte value)
    {
        value = _crtc[index];
        return IsControlRegister(index);
    }

    /// <summary>Applies chip-specific masks after the VGA owner's ordinary locks.</summary>
    internal bool TryWriteCrtc(byte index, byte value)
    {
        if (!IsControlRegister(index))
        {
            return false;
        }

        if (index == ConfigurationRegister)
        {
            if (_crtc[ExtendedLockRegister] != 0xA5)
            {
                return true;
            }

            // The PCI bus strap is read-only even with the configuration key.
            value = (byte)((value & 0xFC) | 0x02);
        }
        else if (index == MemoryControlRegister)
        {
            // Reserved bits read zero in this deterministic register model.
            value &= 0x7E;
        }
        else if (index == LinearControlRegister)
        {
            value &= 0x9B;
        }

        if (_crtc[index] != value)
        {
            _crtc[index] = value;
            Changed?.Invoke();
        }

        return true;
    }

    /// <summary>Notifies once after the entire VGA snapshot has been restored.</summary>
    internal void NotifyRestored()
    {
        Changed?.Invoke();
    }

    private static bool IsControlRegister(byte index)
    {
        return index is ConfigurationRegister or MemoryControlRegister
            or LinearControlRegister or BaseHighRegister or BaseLowRegister;
    }
}
