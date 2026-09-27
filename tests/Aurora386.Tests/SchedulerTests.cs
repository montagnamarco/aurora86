using Aurora386.Core;
using Aurora386.Core.Devices.Storage;
using Aurora386.Core.Timing;

namespace Aurora386.Tests;

/// <summary>
/// Work package F1.3: a cycle-accurate event queue (<see cref="Scheduler"/>)
/// alongside the existing batch device advance. No device is migrated to it
/// in this package -- the PIT and the sound card still move proportionally
/// in <c>Machine.AdvanceDevices</c> -- so these tests exercise the queue on
/// its own and, separately, its wiring into <c>Machine.RunInstructions</c>.
/// </summary>
public class SchedulerTests
{
    // ---- pure scheduler: ordering, cancellation, rescheduling -------------

    [Fact]
    public void EventsFireInDueCycleOrder()
    {
        var scheduler = new Scheduler();
        var order = new List<string>();
        scheduler.Schedule(30, "c", _ => order.Add("c"));
        scheduler.Schedule(10, "a", _ => order.Add("a"));
        scheduler.Schedule(20, "b", _ => order.Add("b"));

        int fired = scheduler.RunDue(100);

        Assert.Equal(3, fired);
        Assert.Equal(new[] { "a", "b", "c" }, order);
    }

    [Fact]
    public void EventsDueOnTheSameCycleFireInScheduleOrder()
    {
        var scheduler = new Scheduler();
        var order = new List<string>();
        scheduler.Schedule(10, "first", _ => order.Add("first"));
        scheduler.Schedule(10, "second", _ => order.Add("second"));
        scheduler.Schedule(10, "third", _ => order.Add("third"));

        scheduler.RunDue(10);

        Assert.Equal(new[] { "first", "second", "third" }, order);
    }

    [Fact]
    public void CancelledEventDoesNotFire()
    {
        var scheduler = new Scheduler();
        bool fired = false;
        var handle = scheduler.Schedule(10, "x", _ => fired = true);

        scheduler.Cancel(handle);
        int count = scheduler.RunDue(100);

        Assert.Equal(0, count);
        Assert.False(fired);
        Assert.False(scheduler.IsPending(handle));
    }

    [Fact]
    public void CancellingAnAlreadyFiredEventIsANoOp()
    {
        var scheduler = new Scheduler();
        var handle = scheduler.Schedule(5, "x", _ => { });

        scheduler.RunDue(5);
        Assert.False(scheduler.IsPending(handle));

        // Must not throw, and must not affect anything else pending.
        scheduler.Cancel(handle);
        var other = scheduler.Schedule(6, "y", _ => { });
        Assert.False(scheduler.IsPending(handle));
        Assert.True(scheduler.IsPending(other));
    }

    [Fact]
    public void CallbackCanRescheduleWithinTheSameRunDueCallAndForLater()
    {
        var scheduler = new Scheduler();
        var fired = new List<long>();

        scheduler.Schedule(10, "a", cycle =>
        {
            fired.Add(cycle);
            // Due at or before "now" (10): must still fire in this same
            // RunDue call, after "a" (higher sequence number, same due
            // cycle), never recursively out of this callback.
            scheduler.Schedule(10, "b-same-call", c => fired.Add(c));
            // Due after "now": must wait for a later RunDue call.
            scheduler.Schedule(50, "b-later-call", c => fired.Add(c));
        });

        int firstRun = scheduler.RunDue(10);
        Assert.Equal(2, firstRun);
        Assert.Equal(new long[] { 10, 10 }, fired);

        int secondRun = scheduler.RunDue(50);
        Assert.Equal(1, secondRun);
        Assert.Equal(new long[] { 10, 10, 50 }, fired);
    }

    [Fact]
    public void EventScheduledInThePastFiresOnTheNextRunDue()
    {
        var scheduler = new Scheduler();
        bool fired = false;
        scheduler.Schedule(5, "x", _ => fired = true);

        Assert.Equal(0, scheduler.RunDue(3));   // not due yet
        Assert.False(fired);

        Assert.Equal(1, scheduler.RunDue(10));  // now in the past relative to "now"
        Assert.True(fired);
    }

    [Fact]
    public void NextDueCycleReflectsCancellations()
    {
        var scheduler = new Scheduler();
        var a = scheduler.Schedule(10, "a", _ => { });
        var b = scheduler.Schedule(20, "b", _ => { });

        Assert.Equal(10, scheduler.NextDueCycle);

        scheduler.Cancel(a);
        Assert.Equal(20, scheduler.NextDueCycle);

        scheduler.Cancel(b);
        Assert.Null(scheduler.NextDueCycle);
    }

    [Fact]
    public void RunDueWithNothingDueFiresNothing()
    {
        var scheduler = new Scheduler();
        scheduler.Schedule(1000, "later", _ => { });

        Assert.Equal(0, scheduler.RunDue(0));
        Assert.Equal(1000, scheduler.NextDueCycle);
    }

    [Fact]
    public void ManyEventsInRandomOrderFireSorted()
    {
        var scheduler = new Scheduler();
        var random = new Random(12345);
        var fired = new List<long>();

        for (int i = 0; i < 10_000; i++)
        {
            long due = random.Next(0, 1_000_000);
            scheduler.Schedule(due, "e", c => fired.Add(c));
        }

        int count = scheduler.RunDue(long.MaxValue);

        Assert.Equal(10_000, count);
        Assert.Equal(10_000, fired.Count);
        for (int i = 1; i < fired.Count; i++)
            Assert.True(fired[i] >= fired[i - 1], $"out of order at {i}: {fired[i - 1]} then {fired[i]}");
    }

    [Fact]
    public void ClearMakesEveryPendingHandleInactive()
    {
        var scheduler = new Scheduler();
        var a = scheduler.Schedule(10, "a", _ => { });
        var b = scheduler.Schedule(20, "b", _ => { });

        scheduler.Clear();

        Assert.False(scheduler.IsPending(a));
        Assert.False(scheduler.IsPending(b));
        Assert.Null(scheduler.NextDueCycle);
        Assert.Equal(0, scheduler.RunDue(1000));
    }

    // ---- Machine integration -------------------------------------------

    private static byte[] BuildBootSector(params byte[] code)
    {
        var sector = new byte[512];
        code.CopyTo(sector, 0);
        sector[510] = 0x55;
        sector[511] = 0xAA;
        return sector;
    }

    private static DiskImage CreateBootableFloppy(byte[] bootSector)
    {
        DiskImage image = DiskImage.CreateInMemory(0x00, 80, 2, 18);
        image.WriteSectors(0, 0, 1, 1, bootSector);
        return image;
    }

    [Fact]
    public void EventFiresAtExactCpuCycleWhileTheCpuIsExecuting()
    {
        // An endless one-instruction loop: JMP $. The CPU never halts, so
        // every instruction the guest "runs" is really the same jump,
        // executed once per cycle -- which is exactly what makes the due
        // cycle predictable from outside.
        byte[] boot = BuildBootSector(0xEB, 0xFE);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(2000);   // past POST, settled into the loop

        long now = machine.Cpu.Cycles;
        long due = now + 777;
        long? seenArgument = null;
        long? seenCyclesInsideCallback = null;
        machine.Scheduler.Schedule(due, "test-event", cycle =>
        {
            seenArgument = cycle;
            seenCyclesInsideCallback = machine.Cpu.Cycles;
        });

        machine.RunInstructions(2000);   // comfortably past due

        Assert.Equal(due, seenArgument);
        Assert.Equal(due, seenCyclesInsideCallback);
    }

    [Fact]
    public void EventFiresWhileTheCpuIsHaltedWithInterruptsOffAndTheCpuStaysHalted()
    {
        // CLI; HLT: halted, and with IF clear nothing -- not even the timer
        // IRQ that is certainly pending by now -- may wake it back up. Only
        // the scheduler moves time forward from here.
        byte[] boot = BuildBootSector(0xFA, 0xF4);

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(2000);   // past POST and into CLI; HLT

        Assert.True(machine.Cpu.State.Halted);

        long now = machine.Cpu.Cycles;
        long due = now + 500;
        long? seenArgument = null;
        machine.Scheduler.Schedule(due, "wake-timer", cycle => seenArgument = cycle);

        machine.RunInstructions(2000);

        Assert.Equal(due, seenArgument);
        Assert.True(machine.Cpu.State.Halted);
    }

    [Fact]
    public void EventScheduledFromACallbackFiresAtItsOwnDueCycle()
    {
        byte[] boot = BuildBootSector(0xEB, 0xFE);   // JMP $

        using var machine = new Machine();
        machine.AttachDisk(CreateBootableFloppy(boot));
        machine.RunInstructions(2000);

        long now = machine.Cpu.Cycles;
        long firstDue = now + 300;
        long? secondArgument = null;

        machine.Scheduler.Schedule(firstDue, "first", cycle =>
        {
            machine.Scheduler.ScheduleAfter(cycle, 1000, "second", c => secondArgument = c);
        });

        machine.RunInstructions(3000);   // past firstDue + 1000

        Assert.Equal(firstDue + 1000, secondArgument);
    }

    [Fact]
    public void RunInstructionsWithNoEventsRunsExactlyTheRequestedInstructionCount()
    {
        using var machine = new Machine();
        long before = machine.Cpu.Cycles;

        machine.RunInstructions(5000);

        Assert.Equal(before + 5000, machine.Cpu.Cycles);
    }
}
