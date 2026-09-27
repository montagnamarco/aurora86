namespace Aurora386.Core.Devices.Chipset;

/// <summary>
/// Selects the motherboard's Intel chipset generation. The 1995 i430FX/PIIX
/// configuration remains the default; the 1997 i430TX/PIIX4 configuration adds
/// bus-master IDE and integrates USB and the minimal power-management block.
/// Expansion cards are configured independently of this motherboard choice.
/// </summary>
public enum Chipset
{
    /// <summary>82437FX (8086:122D) and 82371FB PIIX (122E/1230), with PIO IDE.</summary>
    I430Fx,

    /// <summary>82439TX (8086:7100) and 82371AB PIIX4 (7110 through 7113).</summary>
    I430Tx,
}

/// <summary>Converts the motherboard selection to and from its persistent vm.json name.</summary>
public static class ChipsetNames
{
    public const string I430Fx = "i430fx";
    public const string I430Tx = "i430tx";

    /// <summary>Returns null for an unknown name so configuration callers can reject typos.</summary>
    public static Chipset? Parse(string name)
    {
        if (string.Equals(name, I430Fx, StringComparison.OrdinalIgnoreCase))
        {
            return Chipset.I430Fx;
        }

        if (string.Equals(name, I430Tx, StringComparison.OrdinalIgnoreCase))
        {
            return Chipset.I430Tx;
        }

        return null;
    }

    /// <summary>Returns the stable configuration name of a supported chipset.</summary>
    public static string ToName(Chipset chipset)
    {
        return chipset switch
        {
            Chipset.I430Fx => I430Fx,
            Chipset.I430Tx => I430Tx,
            _ => throw new ArgumentOutOfRangeException(nameof(chipset)),
        };
    }
}
