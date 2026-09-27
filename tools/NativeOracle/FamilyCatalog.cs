using NativeOracle.Families;

namespace NativeOracle;

/// <summary>
/// Every instruction family this build of the oracle knows, in the order the
/// report lists them. A later work package (MMX, SSE) adds its own
/// `Families/*.cs` file and one line here; nothing else in the tool changes.
/// </summary>
public static class FamilyCatalog
{
    public static IReadOnlyList<Family> All { get; } =
    [
        .. AluFamilies.All(),
        .. UnaryFamilies.All(),
        .. MultiplyDivideFamilies.All(),
        .. ShiftFamilies.All(),
        .. BitFamilies.All(),
        .. ExchangeFamilies.All(),
        .. MiscFamilies.All(),
        .. CmovFamilies.All(),
        .. MmxFamilies.All(),
        .. SseFamilies.All(),
        .. SseMmxRegisterFamilies.All(),
        .. Sse2Families.All(),
    ];
}
