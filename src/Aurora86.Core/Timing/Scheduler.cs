// A cycle-accurate event queue, sitting alongside the batch device advance
// in Machine.Run.cs rather than replacing it. The batch advance still moves
// the PIT, the sound card and the rest of today's devices in one proportional
// step per instruction chunk; this is for the devices that have not been
// migrated to it yet and need something to fire on an exact cycle instead --
// bus-master DMA completion, a NIC's transmit-done interrupt, an LAPIC timer,
// an HPET/ACPI PM timer. None of those exist yet, so nothing schedules
// anything here today; this is the plumbing for when they do.

namespace Aurora86.Core.Timing;

/// <summary>
/// A handle to one scheduled event: its name (for diagnostics), the cycle it
/// is due at, and enough identity that cancelling a stale handle -- one whose
/// event already fired, or that the caller kept around after cancelling it
/// once -- is a no-op rather than a mistake that reaches into someone else's
/// event.
///
/// Two handles are never equal to each other even if they share a name and a
/// due cycle: identity is the object reference, exactly like <see
/// cref="System.Threading.Timer"/>'s handle or a real interrupt source's
/// private token.
/// </summary>
public sealed class ScheduledEvent
{
    internal ScheduledEvent(long sequence, long dueCycle, string name, Action<long> callback)
    {
        Sequence = sequence;
        DueCycle = dueCycle;
        Name = name;
        Callback = callback;
    }

    /// <summary>Assigned in the order <see cref="Scheduler.Schedule"/> was called, breaking ties between equal due cycles.</summary>
    internal long Sequence { get; }

    /// <summary>The cycle this event is due at.</summary>
    public long DueCycle { get; internal set; }

    /// <summary>The name it was scheduled under, for diagnostics only.</summary>
    public string Name { get; }

    internal Action<long> Callback { get; }

    /// <summary>
    /// True from the moment this handle is created until it either fires or
    /// is cancelled. Once false it stays false: a fired or cancelled event is
    /// never reused, and a fresh <see cref="Scheduler.Schedule"/> call always
    /// returns a new handle.
    /// </summary>
    internal bool Active = true;
}

/// <summary>
/// An event queue measured in CPU cycles -- the same count as <see
/// cref="Cpu.Cpu.Cycles"/>, one cycle per instruction today, from which the
/// TSC is derived. The scheduler does not own the clock: every method takes
/// <c>now</c> from the caller, which is <c>Machine</c> passing
/// <c>Cpu.Cycles</c>.
///
/// <para>
/// Ordering is by due cycle, then by the order <see cref="Schedule"/> was
/// called (first in, first fired among ties) -- deterministic regardless of
/// how many events share a due cycle. A callback is free to schedule or
/// cancel events of its own, including ones due at or before the <c>now</c>
/// passed to the current <see cref="RunDue"/> call: those are appended to the
/// queue with a higher sequence number than anything already pending, so they
/// fire later in the very same <see cref="RunDue"/> call -- after everything
/// that was already due -- rather than being skipped to the next call or,
/// worse, invoked recursively out of the callback that scheduled them.
/// </para>
///
/// <para>
/// Cancellation is lazy: <see cref="Cancel"/> only clears the handle's
/// internal flag. A cancelled entry left at the front of the underlying
/// queue is discarded the next time anything asks what is due, which keeps
/// <see cref="RunDue"/> and <see cref="NextDueCycle"/> O(1) when nothing
/// is due and there is nothing stale to clear -- the common case, since it
/// runs after every instruction batch whether or not a device is using the
/// scheduler yet.
/// </para>
///
/// <para>
/// <b>Reset.</b> <c>Machine.RunInstructions</c> calls <see cref="Clear"/>
/// when the guest resets the CPU through the keyboard controller or port
/// 92h, on the same branch that calls <c>Cpu.Reset()</c>. Every handle held
/// by anyone at that point becomes permanently inactive -- <see
/// cref="IsPending"/> reports false on it from then on -- because
/// <c>Cpu.Cycles</c> itself goes back to zero on reset, and a due cycle
/// measured against the cycle count before reset means nothing afterwards.
/// No device migrates to this scheduler yet, so today this only ever
/// discards an empty queue; the first device that does schedule its own
/// events must re-arm them from its own <c>Reset()</c>, exactly as it
/// already re-arms whatever state it keeps today.
/// </para>
///
/// <para>
/// <b>Snapshots.</b> This package adds no snapshot section: no device uses
/// the scheduler yet, so there is nothing pending to lose. The first device
/// that migrates to it is responsible for two things: re-arming its own
/// events from <c>Restore</c> (the same handles cannot survive a save/load
/// round trip, since they are plain object references) rather than
/// expecting the scheduler to remember them, and, if by then some event
/// genuinely needs to survive a snapshot mid-flight, adding a versioned
/// section for it -- there isn't one here.
/// </para>
/// </summary>
public sealed class Scheduler
{
    private readonly PriorityQueue<ScheduledEvent, (long DueCycle, long Sequence)> _queue = new();
    private long _nextSequence;

    /// <summary>
    /// Schedules <paramref name="callback"/> to run once <c>now</c> reaches
    /// <paramref name="dueCycle"/> or later. The callback receives the cycle
    /// the event actually fires at (its due cycle, not whatever <c>now</c>
    /// happened to be when <see cref="RunDue"/> noticed it).
    /// </summary>
    public ScheduledEvent Schedule(long dueCycle, string name, Action<long> callback)
    {
        var ev = new ScheduledEvent(_nextSequence++, dueCycle, name, callback);
        _queue.Enqueue(ev, (ev.DueCycle, ev.Sequence));
        return ev;
    }

    /// <summary>Convenience for scheduling relative to a known current cycle.</summary>
    public ScheduledEvent ScheduleAfter(long now, long delayCycles, string name, Action<long> callback)
        => Schedule(now + delayCycles, name, callback);

    /// <summary>
    /// Cancels a pending event. A no-op if the handle already fired, was
    /// already cancelled, or belongs to an event the scheduler has since
    /// forgotten (<see cref="Clear"/>, a machine reset) -- there is never a
    /// wrong handle to cancel, only a handle that no longer does anything.
    /// </summary>
    public void Cancel(ScheduledEvent e) => e.Active = false;

    /// <summary>Whether this handle's event is still going to fire.</summary>
    public bool IsPending(ScheduledEvent e) => e.Active;

    /// <summary>
    /// The due cycle of the earliest event still pending, or null when there
    /// is none. Discards any cancelled entries left at the front of the
    /// queue first, so a cancelled event never keeps this non-null forever.
    /// </summary>
    public long? NextDueCycle
    {
        get
        {
            DiscardCancelledAtFront();
            return _queue.TryPeek(out var ev, out _) ? ev.DueCycle : null;
        }
    }

    /// <summary>
    /// Fires every event due at or before <paramref name="now"/>, in order:
    /// by due cycle, then by scheduling order, including events a callback
    /// schedules or re-schedules for a cycle at or before <c>now</c> while
    /// this call is still running. Returns how many callbacks ran.
    /// </summary>
    public int RunDue(long now)
    {
        int fired = 0;
        while (true)
        {
            DiscardCancelledAtFront();
            if (!_queue.TryPeek(out var ev, out _) || ev.DueCycle > now) break;

            _queue.Dequeue();
            ev.Active = false;
            fired++;
            ev.Callback(ev.DueCycle);
        }
        return fired;
    }

    /// <summary>
    /// Drops every pending event. Every handle held by a caller becomes
    /// permanently inactive; see the reset note on the class summary for why
    /// <c>Machine</c> calls this on a CPU reset.
    /// </summary>
    public void Clear()
    {
        while (_queue.TryDequeue(out var ev, out _))
            ev.Active = false;
    }

    private void DiscardCancelledAtFront()
    {
        while (_queue.TryPeek(out var ev, out _) && !ev.Active)
            _queue.Dequeue();
    }
}
