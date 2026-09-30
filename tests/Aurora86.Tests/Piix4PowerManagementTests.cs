using System.Buffers.Binary;
using Aurora86.Core.Devices.Chipset;
using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.Pci;
using Aurora86.Core.IO;

namespace Aurora86.Tests;

/// <summary>Exercises the PIIX4 PM register contract as firmware and a timer reader would.</summary>
public class Piix4PowerManagementTests
{
    [Fact]
    public void PmbaUsesOffset40AndPmregmiscEnablesItIndependentlyOfPciCommand()
    {
        var function = CreateFunction();
        var io = new IoBus();
        var power = new Piix4PowerManagement(function, io, 0xB000, new InterruptController(), NoPowerOff, () => 0, () => 100_000_000);
        Assert.Equal(0xB001u, function.Read(0x40, 4));
        Assert.Equal(0u, function.Read(0x20, 4));
        function.Write(4, 2, 1);
        Assert.Equal(0xFF, io.ReadByte(0xB000));
        function.Write(4, 2, 0);
        function.Write(0x80, 1, 0xFF);
        Assert.Equal(1u, function.Read(0x80, 1));
        Assert.Equal(0, io.ReadByte(0xB000));
        function.Write(0x40, 4, 0xABCD_B07F);
        Assert.Equal(0xB041u, function.Read(0x40, 4));
        Assert.Equal(0xFF, io.ReadByte(0xB000));
        Assert.Equal(0, io.ReadByte(0xB040));
        function.Write(0x80, 1, 0);
        Assert.Equal(0xFF, io.ReadByte(0xB040));
    }

    [Fact]
    public void TimerCountsAt3579545HzWrapsAt24BitsAndIgnoresWrites()
    {
        long cycles = 0;
        var function = CreateFunction();
        var io = new IoBus();
        var power = new Piix4PowerManagement(function, io, 0xB000, new InterruptController(), NoPowerOff, () => cycles, () => 100_000_000);
        function.Write(0x80, 1, 1);
        cycles = 100_000_000;
        Assert.Equal(3_579_545u, io.ReadDword(0xB008));
        Assert.Equal(unchecked((ushort)3_579_545u), io.ReadWord(0xB008));
        Assert.Equal(0, io.ReadByte(0xB00B));
        io.WriteDword(0xB008, uint.MaxValue);
        Assert.Equal(3_579_545u, io.ReadDword(0xB008));
        cycles = 500_000_000;
        Assert.Equal(17_897_725u & 0xFFFFFF, io.ReadDword(0xB008));
        power.Reset();
        Assert.Equal(0u, io.ReadDword(0xB008));
    }

    [Fact]
    public void TimerStatusSetsOnEitherBit23TransitionAndOnlyWrittenOnesClearIt()
    {
        long cycles = 0;
        var function = CreateFunction();
        var io = new IoBus();
        var power = new Piix4PowerManagement(function, io, 0xB000, new InterruptController(), NoPowerOff, () => cycles, () => 3_579_545);
        function.Write(0x80, 1, 1);
        cycles = 1 << 23;
        Assert.Equal(1, io.ReadWord(0xB000));
        io.WriteByte(0xB001, 0xFF);
        Assert.Equal(1, io.ReadWord(0xB000));
        io.WriteByte(0xB000, 0);
        Assert.Equal(1, io.ReadWord(0xB000));
        io.WriteByte(0xB000, 1);
        Assert.Equal(0, io.ReadWord(0xB000));
        cycles = 1 << 24;
        Assert.Equal(1, io.ReadWord(0xB000));
    }

    [Fact]
    public void ReservedEnableAndControlBitsStayZeroAndSuspendEnableReadsZero()
    {
        var function = CreateFunction();
        var io = new IoBus();
        var power = new Piix4PowerManagement(function, io, 0xB000, new InterruptController(), NoPowerOff, () => 0, () => 100_000_000);
        function.Write(0x80, 1, 1);
        io.WriteWord(0xB002, 0xFFFF);
        io.WriteWord(0xB004, 0xFFFF);
        Assert.Equal(0x0521, io.ReadWord(0xB002));

        // SLP_EN (bit 13, 0x2000) still reads back 0: it is a write-only
        // strobe (plans/specs/acpi.md §4 and decision C6), acted on once and
        // never stored, exactly as it was already excluded from ControlMask
        // before this package -- only SLP_TYP = 0 (\_S5) now has an
        // observable effect (a power-off request), which this test does not
        // trigger since SLP_TYP here is all ones, not zero.
        Assert.Equal(0x1C07, io.ReadWord(0xB004));
        Assert.Equal(0, io.ReadWord(0xB006));
    }

    [Fact]
    public void RestorePreservesTimerPhaseRegistersAndRestoredPciDecode()
    {
        long cycles = 100;
        var function = CreateFunction();
        var io = new IoBus();
        var power = new Piix4PowerManagement(function, io, 0xB000, new InterruptController(), NoPowerOff, () => cycles, () => 3_579_545);
        function.Write(0x80, 1, 1);
        cycles = 500;
        io.WriteWord(0xB002, 0x521);
        io.WriteWord(0xB004, 0x1401);
        byte[] state = power.Capture();
        byte[] pciState = function.Capture();
        cycles = 900;
        power.Reset();
        function.Write(0x40, 4, 0xC001);
        function.Restore(pciState);
        power.Restore(state, Piix4PowerManagement.SnapshotVersion);
        Assert.Equal(400u, io.ReadDword(0xB008));
        Assert.Equal(0x521, io.ReadWord(0xB002));
        Assert.Equal(0x1401, io.ReadWord(0xB004));
        Assert.Equal(0xFF, io.ReadByte(0xC000));
        Assert.Throws<InvalidDataException>(() => power.Restore(new byte[1], Piix4PowerManagement.SnapshotVersion));
        byte[] invalid = (byte[])state.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(18), 0xFFFF);
        Assert.Throws<InvalidDataException>(() => power.Restore(invalid, Piix4PowerManagement.SnapshotVersion));
        Assert.Equal(state, power.Capture());
    }

    [Fact]
    public void FunctionResetClosesDecodeAndLongUptimeKeepsExactTimerValue()
    {
        long cycles = 0;
        var function = CreateFunction();
        var io = new IoBus();
        var power = new Piix4PowerManagement(function, io, 0xB000, new InterruptController(), NoPowerOff, () => cycles, () => 100_000_000);
        function.Write(0x80, 1, 1);
        cycles = 8_000_000_000_000_123;
        uint expected = (uint)(((UInt128)(ulong)cycles * 3_579_545 / 100_000_000) & 0xFFFFFF);
        Assert.Equal(expected, io.ReadDword(0xB008));
        function.Reset();
        Assert.Equal(0xFF, io.ReadByte(0xB000));
    }

    [Theory]
    [InlineData(5_000_000)]
    [InlineData(0)]
    public void RestoreRebasesLiveCpuCyclesAndPreservesFractionalTimerTick(long restoredCycles)
    {
        long cycles = 1000;
        var function = CreateFunction();
        var io = new IoBus();
        var power = new Piix4PowerManagement(function, io, 0xB000, new InterruptController(), NoPowerOff, () => cycles, () => 100_000_000);
        function.Write(0x80, 1, 1);
        cycles += 27;
        Assert.Equal(0u, power.TimerValue);
        byte[] snapshot = power.Capture();

        // Restoring can happen in a later-running machine or a fresh one.
        cycles = restoredCycles;
        var restoredFunction = CreateFunction();
        var restoredIo = new IoBus();
        var restored = new Piix4PowerManagement(
            restoredFunction, restoredIo, 0xB000, new InterruptController(), NoPowerOff, () => cycles, () => 100_000_000);
        restored.Restore(snapshot, Piix4PowerManagement.SnapshotVersion);
        Assert.Equal(snapshot, restored.Capture());
        Assert.Equal(0u, restored.TimerValue);
        cycles++;
        Assert.Equal(1u, restored.TimerValue);
        cycles += 99_999_972;
        Assert.Equal(3_579_545u, restored.TimerValue);
    }

    [Fact]
    public void SnapshotRejectsAnInvalidFractionBeforeChangingTimerState()
    {
        var function = CreateFunction();
        var power = new Piix4PowerManagement(function, new IoBus(), 0xB000, new InterruptController(), NoPowerOff, () => 0, () => 100_000_000);
        byte[] state = power.Capture();
        byte[] invalid = (byte[])state.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(8), 100_000_000);
        Assert.Throws<InvalidDataException>(() => power.Restore(invalid, Piix4PowerManagement.SnapshotVersion));
        Assert.Equal(state, power.Capture());
        BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(12), 0);
        Assert.Throws<InvalidDataException>(() => Piix4PowerManagement.ValidateSnapshot(invalid, Piix4PowerManagement.SnapshotVersion));
    }

    /// <summary>A version-1 (pre-GPE0) snapshot must still be readable, restoring GPE0 at its own reset value (work package P4.5).</summary>
    [Fact]
    public void VersionOneSnapshotWithoutGpe0StillRestoresAndReadsBackAsZero()
    {
        var function = CreateFunction();
        var io = new IoBus();
        var power = new Piix4PowerManagement(function, io, 0xB000, new InterruptController(), NoPowerOff, () => 0, () => 100_000_000);
        function.Write(0x80, 1, 1);

        // The pre-P4.5 22-byte layout: a real Capture() with the trailing
        // GPE0 STS/EN pair (added by this package) simply cut off, exactly
        // as an old file would have been written.
        byte[] legacyState = power.Capture()[..22];
        Piix4PowerManagement.ValidateSnapshot(legacyState, 1);
        power.Restore(legacyState, 1);
        Assert.Equal(0, io.ReadWord(0xB00C));   // GPE0_STS
        Assert.Equal(0, io.ReadWord(0xB00E));   // GPE0_EN
    }

    private static void NoPowerOff()
    {
    }

    private static PciFunction CreateFunction()
    {
        return new PciFunction(0x0B, 0x8086, 0x7113, 0x068000, commandMask: 1);
    }
}
