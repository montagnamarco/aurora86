using Aurora386.Core.Devices.Cards;
using Aurora386.Core.Devices.Storage;

namespace Aurora386.Core;

public sealed partial class Machine
{
    /// <summary>
    /// The AHCI SATA controller, or null on a machine whose configuration did
    /// not fit one (<c>vm.json</c>'s <c>hardware.sata</c>). See
    /// <see cref="AhciCard"/>.
    /// </summary>
    public AhciController? Sata => Cards.OfType<AhciCard>().FirstOrDefault()?.Controller;

    /// <summary>The number of SATA ports the controller has (AHCI CAP.NP + 1).</summary>
    public const int SataPortCount = 6;

    /// <summary>
    /// The images on the SATA ports, owned by the machine the way the IDE
    /// disks are: closed by <see cref="Dispose"/>, so a VM rebuilt in the
    /// same process can open the same files again.
    /// </summary>
    private readonly DiskImage?[] _sataDisks = new DiskImage?[SataPortCount];

    /// <summary>
    /// Connects a disk image to SATA port <paramref name="port"/> (0-5). The
    /// guest sees it once its driver resets the port, as with a disk plugged
    /// in before power-on. The BIOS does not boot from these disks: INT 13h
    /// knows only the IDE channels. A port already holding a disk is
    /// refused rather than silently replaced.
    /// </summary>
    public void AttachSataDisk(int port, DiskImage disk)
    {
        AhciController controller = Sata
            ?? throw new InvalidOperationException("This machine has no SATA controller: add hardware.sata to its configuration.");

        if (port is < 0 or >= SataPortCount)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, $"SATA ports are 0-{SataPortCount - 1}.");
        }

        if (_sataDisks[port] is not null)
        {
            throw new InvalidOperationException($"SATA port {port} already holds a disk.");
        }

        controller.AttachDisk(port, disk);
        _sataDisks[port] = disk;
    }

    private void DisposeSataDisks()
    {
        foreach (DiskImage? disk in _sataDisks)
        {
            disk?.Dispose();
        }
    }
}
