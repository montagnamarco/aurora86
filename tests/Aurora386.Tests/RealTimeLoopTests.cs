using Aurora386.Core;
using Aurora386.Core.Firmware;
using Aurora386.Core.Firmware.Fpu;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Diagnostics;
namespace Aurora386.Tests;

/// <summary>
/// The real-time execution loop, the one the frontend drives once per frame.
/// These tests check that time inside the emulated machine passes at the right
/// rate and that the renderer does not do work it does not have to.
/// </summary>
public class RealTimeLoopTests
{
    private static byte[] IdleBootSector()
    {
        // A sector that enables interrupts and waits, so the machine stays
        // alive and interruptible while the timer runs.
        var sector = new byte[512];
        new byte[] { 0xFB, 0xF4, 0xEB, 0xFD }.CopyTo(sector, 0);   // STI; HLT; JMP -3
        sector[510] = 0x55; sector[511] = 0xAA;
        return sector;
    }

    [Fact]
    public void TimerTicksAtThePcFrequency()
    {
        using var machine = new Machine();
        DiskImage disk = DiskImage.CreateInMemory(0x00, 80, 2, 18);
        disk.WriteSectors(0, 0, 1, 1, IdleBootSector());
        machine.AttachDisk(disk);

        machine.RunInstructions(2000);      // boot
        uint before = machine.Bda.TimerTicks;

        // Two simulated seconds, one frame at a time, exactly as the frontend
        // drives it.
        for (int i = 0; i < 120; i++)
            machine.RunFor(TimeSpan.FromSeconds(1.0 / 60.0));

        uint ticks = machine.Bda.TimerTicks - before;

        // At 18.2 Hz, two seconds is about 36 ticks. The tolerance absorbs
        // the rounding between the CPU's declared frequency and the timer's.
        Assert.InRange(ticks, 30u, 42u);
    }

    [Fact]
    public void RendererDoesNotRedrawWithoutChanges()
    {
        // Redrawing 80x25 cells every frame when nothing has changed is
        // almost entirely wasted work, and dirty row tracking exists precisely
        // to avoid it.
        using var machine = new Machine();
        machine.RunInstructions(500);

        machine.Video.Render(force: true);
        long baseline = machine.Video.FramesRendered;

        for (int i = 0; i < 60; i++)
        {
            machine.RunFor(TimeSpan.FromSeconds(1.0 / 60.0));
            machine.Video.Render();
        }

        // With no writes to video memory the frame count must not move.
        Assert.Equal(baseline, machine.Video.FramesRendered);
    }

    [Fact]
    public void RendererRedrawsWhenTheScreenChanges()
    {
        using var machine = new Machine();
        machine.RunInstructions(500);
        machine.Video.Render(force: true);
        long baseline = machine.Video.FramesRendered;

        machine.VideoServices.TeletypeOutput((byte)'X', 0);

        Assert.True(machine.Video.Render());
        Assert.Equal(baseline + 1, machine.Video.FramesRendered);
    }

    [Fact]
    public void FramebufferHasTheTextModeSize()
    {
        using var machine = new Machine();
        machine.RunInstructions(500);
        machine.Video.Render(force: true);

        // 720 across: nine pixels per text cell, not eight.
        Assert.Equal(720, machine.Video.Width);
        Assert.Equal(400, machine.Video.Height);
        Assert.Equal(720 * 400, machine.Video.Framebuffer.Length);
    }

    [Fact]
    public void ExecutionAdvancesByAConsistentInstructionCount()
    {
        using var machine = new Machine();
        DiskImage disk = DiskImage.CreateInMemory(0x00, 80, 2, 18);

        // A loop that never stops, so there is always something to count.
        var sector = new byte[512];
        new byte[] { 0x90, 0xEB, 0xFD }.CopyTo(sector, 0);   // NOP; JMP -3
        sector[510] = 0x55; sector[511] = 0xAA;
        disk.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(disk);

        machine.RunInstructions(2000);
        long before = machine.Cpu.Cycles;

        machine.RunFor(TimeSpan.FromSeconds(0.1));

        long executed = machine.Cpu.Cycles - before;
        long expected = (long)(machine.CpuFrequencyHz * 0.1);

        // The count must match the declared frequency, within the batch size
        // the execution loop works in.
        Assert.InRange(executed, expected - 5000, expected + 5000);
    }

    [Fact]
    public void CursorBlinksWithoutTouchingVideoMemory()
    {
        using var machine = new Machine();
        machine.RunInstructions(500);
        machine.Video.Render(force: true);

        // Blink phase is renderer state: toggling it must redraw the cursor's
        // row without any write to video memory.
        byte cellBefore = machine.VideoMemory[
            Aurora386.Core.Devices.Video.VgaMemory.TextWindowOffset];

        machine.Video.CursorBlinkOn = false;
        machine.Video.Render(force: true);
        machine.Video.CursorBlinkOn = true;
        machine.Video.Render(force: true);

        Assert.Equal(cellBefore, machine.VideoMemory[
            Aurora386.Core.Devices.Video.VgaMemory.TextWindowOffset]);
    }
}
