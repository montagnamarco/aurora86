namespace Aurora386.Core.Configuration;

/// <summary>
/// The primary display adapter installed in an Aurora386 virtual machine.
/// Values are stable so persistent configuration and snapshots can adopt
/// their numeric identity without renumbering existing machine models.
/// </summary>
public enum VmVideoModel
{
    S3Trio64VPlus = 0,
    S3Virge325 = 1,

    /// <summary>The Aurora Display Adapter, designed for Aurora386 (docs/AURORA-GPU.md).</summary>
    AuroraGpu = 2
}
