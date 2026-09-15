using Aurora386.Core.Devices.Pci;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// The read-only identity shared by PCI enumeration and S3's extended CRTC
/// registers on the Trio64V+ and original 1996 ViRGE 86C325. Display drivers
/// inspect both paths before deciding which register protocol to use.
/// This describes identity only; the card owner supplies its apertures,
/// command decoding, memory, firmware and drawing engine separately.
/// </summary>
public sealed class S3VideoIdentity
{
    private const ushort VendorId = 0x5333;
    private const uint DisplayClassCode = 0x030000;

    private S3VideoIdentity(ushort deviceId, byte pciRevision, byte crtcRevision, byte chipId)
    {
        DeviceId = deviceId;
        PciRevision = pciRevision;
        CrtcRevision = crtcRevision;
        ChipId = chipId;
    }

    /// <summary>
    /// Preserves the existing, verified Trio path's PCI revision 54h and
    /// CRTC revision 00h. Unifying identity must not silently change either
    /// value in the Windows drivers' established detection path.
    /// </summary>
    public static S3VideoIdentity Trio64VPlus { get; } = new(0x8811, 0x54, 0x00, 0xE1);

    /// <summary>
    /// S3's August 1996 ViRGE databook identifies the 86C325 as 5631h
    /// (sections 18-1 and 25-2). Revision zero follows the original ViRGE
    /// profile in 86Box, commit 51a3f7683e9691832e423a90ad9cf8b39cd7053e;
    /// it is a selected reference stepping, not a new silicon measurement.
    /// </summary>
    public static S3VideoIdentity Virge325 { get; } = new(0x5631, 0x00, 0x00, 0xE1);

    public ushort DeviceId { get; }
    public byte PciRevision { get; }
    public byte CrtcRevision { get; }
    public byte ChipId { get; }

    /// <summary>
    /// Creates the PCI identity registers. The owner defines BARs and decode
    /// callbacks before attaching the function; identity alone is not a card.
    /// </summary>
    public PciFunction CreatePciFunction(ushort address, ushort commandMask = 0)
    {
        var function = new PciFunction(address, VendorId, DeviceId, DisplayClassCode,
            commandMask: commandMask);
        function.Define(8, (DisplayClassCode << 8) | PciRevision, 4);
        return function;
    }
}
