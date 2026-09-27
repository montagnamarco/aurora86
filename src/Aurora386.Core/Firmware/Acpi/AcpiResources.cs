namespace Aurora386.Core.Firmware.Acpi;

/// <summary>
/// Builds the raw resource-descriptor byte sequences the DSDT's <c>_CRS</c>
/// and <c>_PRS</c> buffers hold (ACPI §6.4, "Resource Data Types for ACPI"),
/// as named by coordinator decision C3 in plans/specs/acpi.md: the DWord
/// Address Space, Word Bus Number Range, IRQ and I/O Port descriptors, and
/// the End Tag every resource template closes with.
///
/// These are not AML terms -- they are literal bytes that sit inside an AML
/// Buffer's own ByteList (<see cref="AmlWriter.WriteBuffer"/> writes them
/// verbatim) -- which is why they live apart from <see cref="AmlWriter"/>
/// rather than as more of its methods.
/// </summary>
internal static class AcpiResources
{
    // ---- large resource item tags (bit 7 set; ACPI §6.4.3) ----

    /// <summary>DWord Address Space Descriptor, fixed 23-byte (0x17) body -- confirmed by plans/specs/acpi.md §3.</summary>
    private const byte DWordAddressSpaceTag = 0x87;
    private const byte DWordAddressSpaceLength = 0x17;

    /// <summary>Word Bus Number Range Descriptor, fixed 13-byte (0x0D) body -- confirmed by plans/specs/acpi.md §3.</summary>
    private const byte WordBusNumberTag = 0x88;
    private const byte WordBusNumberLength = 0x0D;

    // ---- small resource item tags (bit 7 clear; ACPI §6.4.2) ----

    /// <summary>IRQ Descriptor with its flags byte (level/polarity/sharing) -- confirmed by plans/specs/acpi.md §3.</summary>
    private const byte IrqWithFlagsTag = 0x23;

    /// <summary>I/O Port Descriptor, fixed 7-byte body -- confirmed by plans/specs/acpi.md §3.</summary>
    private const byte IoPortTag = 0x47;

    /// <summary>End Tag, one checksum byte that a written-once BIOS never bothers computing (§6.4.2.9: a zero checksum means "not computed", which every AML interpreter this DSDT targets tolerates).</summary>
    private const byte EndTagTag = 0x79;

    // ---- Address Space Descriptor "ResourceType" byte (both large descriptors above share it) ----

    private const byte ResourceTypeMemory = 0x00;
    private const byte ResourceTypeBusNumber = 0x02;

    /// <summary>
    /// The DWord Address Space Descriptor's own General Flags and Memory
    /// Type-Specific Flags bytes are not settled from a datasheet -- there
    /// is nothing PIIX4-specific about a PCI root bridge's own memory
    /// window, and the primary ACPI PDF would not fetch (plans/specs/acpi.md's
    /// own sourcing note) -- so both are left at the values a plain,
    /// non-negotiable, read-write, non-cacheable memory window needs at
    /// minimum: General Flags 0 (nothing here is bridged or translated),
    /// Type-Specific Flags bit 0 set (writable). Flagged per decision C6.
    /// </summary>
    private const byte MemoryTypeSpecificWritable = 0x01;

    /// <summary>Same note as above for the bus range: General/Type-Specific Flags 0, since bus 0 is fixed and not negotiated (decision C6).</summary>
    private const byte BusRangeFlagsUnsettled = 0x00;

    /// <summary>
    /// IRQ Descriptor flags byte (ACPI §6.4.2.5): bit 0 _HE (0 Level, 1
    /// Edge), bit 3 _LL (0 Active-High, 1 Active-Low), bit 4 _SHR (0
    /// Exclusive, 1 Shared). Recalled from memory, not re-confirmed against
    /// the PIIX4 datasheet or the primary ACPI PDF (decision C6): PIRQA-D
    /// and the SCI are level, active-low and shared on this board regardless
    /// (plans/specs/apic.md §2's pin table and this package's own runtime
    /// facts), so 0x18 is what every link and the SCI both need.
    /// </summary>
    private const byte LevelActiveLowShared = 0x18;

    /// <summary>Bit 0 of a small-item tag byte's own length field: I/O Port Descriptor's "Information" byte, bit 0 set for 16-bit decode (every port on this board is fully decoded, never the ISA 10-bit alias).</summary>
    private const byte IoDecode16 = 0x01;

    public static byte[] EndTag() => [EndTagTag | 1, 0x00];   // small-item length is 1 (the checksum byte); tag|length = 0x79.

    /// <summary>Concatenates several resource descriptors (and, at the end, an End Tag) into one Buffer's worth of bytes.</summary>
    public static byte[] Combine(params byte[][] parts)
    {
        int length = 0;
        foreach (byte[] part in parts) length += part.Length;

        byte[] result = new byte[length];
        int at = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }

        return result;
    }

    /// <summary>A fixed, non-negotiable I/O range: Min = Max = base, Length = the whole span, Alignment 1 (decode exactly at that base).</summary>
    public static byte[] FixedIoRange(ushort minimum, ushort maximum, ushort length)
    {
        return
        [
            IoPortTag,
            IoDecode16,
            (byte)minimum, (byte)(minimum >> 8),
            (byte)maximum, (byte)(maximum >> 8),
            0x01,                          // Base alignment: 1, since Min == Max fixes the base already.
            (byte)length,
        ];
    }

    /// <summary>Bus 0-0, the only bus this board's PCI0 root bridge ever describes (plans/specs/acpi.md §3).</summary>
    public static byte[] WordBusNumberRange(ushort minimum, ushort maximum)
    {
        ushort length = (ushort)(maximum - minimum + 1);
        return
        [
            WordBusNumberTag, WordBusNumberLength, 0x00,
            ResourceTypeBusNumber,
            BusRangeFlagsUnsettled,          // General Flags
            BusRangeFlagsUnsettled,          // Type-Specific Flags
            0x00, 0x00,                      // Granularity
            (byte)minimum, (byte)(minimum >> 8),
            (byte)maximum, (byte)(maximum >> 8),
            0x00, 0x00,                      // Address Translation Offset
            (byte)length, (byte)(length >> 8),
        ];
    }

    /// <summary>The PCI0 memory window: from the frame buffer base to the top of the MMIO space this board's own devices occupy, computed at BIOS build time (test case 17).</summary>
    public static byte[] DWordMemoryRange(uint minimum, uint length)
    {
        uint maximum = minimum + length - 1;
        return
        [
            DWordAddressSpaceTag, DWordAddressSpaceLength, 0x00,
            ResourceTypeMemory,
            BusRangeFlagsUnsettled,          // General Flags
            MemoryTypeSpecificWritable,      // Type-Specific Flags
            0x00, 0x00, 0x00, 0x00,          // Granularity
            (byte)minimum, (byte)(minimum >> 8), (byte)(minimum >> 16), (byte)(minimum >> 24),
            (byte)maximum, (byte)(maximum >> 8), (byte)(maximum >> 16), (byte)(maximum >> 24),
            0x00, 0x00, 0x00, 0x00,          // Address Translation Offset
            (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)(length >> 24),
        ];
    }

    /// <summary>A single IRQ, level/active-low/shared -- what every PCI interrupt line and the SCI need (decision C4, plans/specs/apic.md §2).</summary>
    public static byte[] LevelIrq(ushort irqMask)
    {
        return [IrqWithFlagsTag, (byte)irqMask, (byte)(irqMask >> 8), LevelActiveLowShared];
    }

    /// <summary>
    /// IRQ Descriptor with no flags byte (§6.4.2.1): the implicit default is
    /// Edge, Active-High, Exclusive, which is exactly how every plain ISA
    /// device line in this DSDT's namespace behaves -- none of them (1, 4,
    /// 6, 8, 12, 13) is ever marked level in the ELCR
    /// (<c>InterruptController.ResetTriggerModes</c> leaves every ISA line
    /// edge-triggered; only the SCI and the PCI-routed IRQs are ever set
    /// level, and neither is a plain ISA device here).
    /// </summary>
    public static byte[] EdgeIrq(ushort irqMask)
    {
        const byte IrqNoFlagsTag = 0x22;
        return [IrqNoFlagsTag, (byte)irqMask, (byte)(irqMask >> 8)];
    }
}
