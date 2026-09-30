using Aurora86.Core;
using Aurora86.Core.Devices.Audio;
using Aurora86.Core.Devices.Cards;

namespace Aurora86.Tests;

/// <summary>
/// Snapshot fixes from the F1.4 (expansion-card registry) review that do not
/// belong to one single card: the motherboard's own 8237 DMA state (D4), the
/// OPL3's restore ordering and timer accumulators (D5), and the presence
/// check that refuses a mismatched snapshot before anything is restored
/// (fix 5). See <see cref="CardRegistryTests"/> and <see
/// cref="Ne2000CardTests"/> for the per-card fixes.
/// </summary>
public class CardSnapshotTests
{
    // ---- D4: the DMA controller's state travels with the snapshot, restored before the cards ----

    [Fact]
    public void AMidPlaybackSnapshotRestoresTheDmaControllerSoTheBlockIrqFiresAtTheSamePoint()
    {
        using var reference = new Machine(4 * 1024 * 1024);
        using var partial = new Machine(4 * 1024 * 1024);

        ProgramAutoInitTransfer(reference);
        ProgramAutoInitTransfer(partial);

        // The reference machine runs the whole span in one go: time
        // constant 0xA6 gives a rate of 1,000,000/(256-166) = 11111 Hz, so
        // 1450 microseconds is comfortably past the 16-transfer block
        // (16 * 90 us = 1440 us), and the auto-init block IRQ fires exactly
        // once.
        reference.SoundBlaster!.Advance(1450);
        Assert.Equal(1, reference.SoundBlaster.InterruptCount);

        // The other machine only gets partway there -- 800 us, short of the
        // block boundary -- before the snapshot is taken.
        partial.SoundBlaster!.Advance(800);
        Assert.Equal(0, partial.SoundBlaster.InterruptCount);

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(partial).Save(path);

            using var restored = new Machine(4 * 1024 * 1024);
            MachineSnapshot.Load(path).RestoreTo(restored);

            // The remaining 650 us -- 800 + 650 = 1450, the same total the
            // reference machine ran -- must land the DMA controller and the
            // DSP's own block counter at the same place: the channel's
            // count register reloaded by auto-init, and the block IRQ fired
            // exactly once, not zero and not twice.
            restored.SoundBlaster!.Advance(650);

            Assert.Equal(reference.SoundBlaster.InterruptCount, restored.SoundBlaster.InterruptCount);
            Assert.Equal(reference.Dma[1].CurrentCount, restored.Dma[1].CurrentCount);
            Assert.Equal(reference.Dma[1].CurrentAddress, restored.Dma[1].CurrentAddress);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Programs DMA channel 1 for a 16-transfer, 8-bit, auto-init block at
    /// physical address 0x10000, and starts the DSP playing it at time
    /// constant 0xA6 -- the same register sequence a driver streaming audio
    /// would issue. Mirrors <c>SoundBlasterTests.CompletingADmaBlockRaisesTheInterrupt</c>.
    /// </summary>
    private static void ProgramAutoInitTransfer(Machine machine)
    {
        machine.Io.WriteByte(0x0C, 0);          // clear the byte-pointer flip-flop
        machine.Io.WriteByte(0x02, 0x00);       // address low
        machine.Io.WriteByte(0x02, 0x00);       // address high
        machine.Io.WriteByte(0x83, 0x01);       // page: physical 0x10000
        machine.Io.WriteByte(0x03, 0x0F);       // count low (16 transfers)
        machine.Io.WriteByte(0x03, 0x00);       // count high
        machine.Io.WriteByte(0x0B, 0x59);       // mode: auto-init, read from memory, single, channel 1
        machine.Io.WriteByte(0x0A, 0x01);       // unmask channel 1

        machine.Io.WriteByte(0x22C, 0x40);      // DSP: set time constant
        machine.Io.WriteByte(0x22C, 0xA6);

        // Command 0x48 itself already stores its argument as "count minus
        // one" (like the DMA count register), and StartDma then adds
        // another one for the auto-init commands that read _blockLength
        // back rather than a fresh argument -- so 0x0E here, one less than
        // the DMA count register's own 0x0F below, is what yields the same
        // 16-transfer block both are counting down.
        machine.Io.WriteByte(0x22C, 0x48);      // DSP: set block size
        machine.Io.WriteByte(0x22C, 0x0E);
        machine.Io.WriteByte(0x22C, 0x00);
        machine.Io.WriteByte(0x22C, 0x1C);      // 8-bit auto-init DMA, start
    }

    // ---- D5: Opl3 restore must set OPL3 mode first, clear state, and carry the timer accumulators ----

    [Fact]
    public void Opl3ModeWaveformsAndPanningSurviveARestoreIntoAFreshMachine()
    {
        using var source = new Machine(4 * 1024 * 1024);

        // Register 0x105 bit 0: OPL3 mode. Waveform values 4-7 and the
        // stereo panning bits below only exist once this is set.
        source.Io.WriteByte(0x38A, 0x05);
        source.Io.WriteByte(0x38B, 0x01);

        // Operator 0's waveform (register 0xE0, bank 0): waveform 5, valid
        // only in OPL3 mode -- an OPL2 masks a write here down to 1 (5 & 3).
        source.Io.WriteByte(0x388, 0xE0);
        source.Io.WriteByte(0x389, 0x05);

        // Voice 0's panning (register 0xC0, bank 0): additive off, left
        // channel only -- the panning bits do not exist at all outside OPL3
        // mode, where a voice is always heard on both channels.
        source.Io.WriteByte(0x388, 0xC0);
        source.Io.WriteByte(0x389, 0x10);

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);

            using var target = new Machine(4 * 1024 * 1024);
            MachineSnapshot.Load(path).RestoreTo(target);

            Opl3 fm = target.SoundBlaster!.Fm;
            Assert.True(fm.Opl3ModeEnabled);
            Assert.Equal(5, fm.WaveformOf(0, 0));

            (bool left, bool right) = fm.PanningOf(0, 0);
            Assert.True(left);
            Assert.False(right);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ARunningOpl3TimerOverflowsAtTheSameCycleAfterARestoreIntoAFreshMachine()
    {
        using var reference = new Machine(4 * 1024 * 1024);
        using var partial = new Machine(4 * 1024 * 1024);

        // Timer 1 preload 0xFA (register 0x02): counts in 80 us steps, so it
        // overflows after (256-250) * 80 = 480 us. Register 0x04 bit 0
        // starts it; bit 6 clear leaves the overflow bit unmasked.
        StartTimer1(reference, preload: 0xFA);
        StartTimer1(partial, preload: 0xFA);

        reference.SoundBlaster!.Fm.AdvanceTimers(500);
        Assert.Equal(0xC0, reference.SoundBlaster.Fm.Status);   // bit 6 plus the summary bit

        partial.SoundBlaster!.Fm.AdvanceTimers(300);
        Assert.Equal(0x00, partial.SoundBlaster.Fm.Status);     // short of the 480 us threshold

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(partial).Save(path);

            using var restored = new Machine(4 * 1024 * 1024);
            MachineSnapshot.Load(path).RestoreTo(restored);

            // 300 + 200 = 500 us, the same total the reference machine ran:
            // the accumulator built up before the snapshot must carry
            // across the restore, or the timer overflows late (or not at
            // all within this span).
            restored.SoundBlaster!.Fm.AdvanceTimers(200);

            Assert.Equal(reference.SoundBlaster.Fm.Status, restored.SoundBlaster.Fm.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void StartTimer1(Machine machine, byte preload)
    {
        machine.Io.WriteByte(0x388, 0x02);
        machine.Io.WriteByte(0x389, preload);
        machine.Io.WriteByte(0x388, 0x04);
        machine.Io.WriteByte(0x389, 0x01);
    }

    // ---- fix 5: a mismatched snapshot is refused before anything is restored ----

    [Fact]
    public void RestoreToRefusesAUsbSectionOnAMachineWithoutUsbBeforeTouchingRam()
    {
        using var source = new Machine(4 * 1024 * 1024);
        source.EnableUsb();

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);

            // Today's default hardware -- sound and network fitted, no USB
            // -- so only the "UHCI" section is orphaned.
            using var target = new Machine(4 * 1024 * 1024);

            // A marker RAM would carry if RestoreTo ever reached it, so the
            // assertion below can tell "refused before restoring" from
            // "refused partway through".
            target.Memory.WriteByte(0, 0x42);

            MachineSnapshot snapshot = MachineSnapshot.Load(path);
            Assert.Throws<InvalidDataException>(() => snapshot.RestoreTo(target));

            Assert.Equal((byte)0x42, target.Memory.ReadByte(0));
            Assert.DoesNotContain(target.Cards, c => c.Kind == "usb");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
