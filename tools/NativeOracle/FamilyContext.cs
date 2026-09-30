using Aurora86.Core.Cpu;

namespace NativeOracle;

/// <summary>Small, repeated randomness a generator needs, kept out of every family so they read as the encoding they build, not the dice rolls behind it.</summary>
public static class RandomExtensions
{
    public static uint NextUInt32(this Random rng)
    {
        Span<byte> bytes = stackalloc byte[4];
        rng.NextBytes(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    public static byte[] NextBytes(this Random rng, int count)
    {
        var bytes = new byte[count];
        rng.NextBytes(bytes);
        return bytes;
    }
}

/// <summary>
/// What every family generator is handed: the random source for this case,
/// and the address the shared buffer lives at (the same address in the host
/// process and in the emulator's bus, see <see cref="HostRunner"/>).
/// </summary>
public sealed class FamilyContext(Random rng, uint bufferAddress)
{
    public Random Rng { get; } = rng;
    public uint BufferAddress { get; } = bufferAddress;

    /// <summary>Random values for the seven usable registers; Esp (index 4) is left zero and never read.</summary>
    public uint[] FreshGpr()
    {
        var gpr = new uint[8];
        foreach (Reg register in InstructionEncoding.AddressableRegisters)
        {
            gpr[(int)register] = Rng.NextUInt32();
        }
        return gpr;
    }

    public byte[] FreshBuffer() => Rng.NextBytes(HostRunner.BufferSize);
}
