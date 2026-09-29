using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Input;

namespace Aurora386.Core.Devices.Host;

/// <summary>
/// The absolute pointer channel, "vmmouse" in Linux's <c>drivers/input/mouse</c>
/// and the mouse VMware Tools installs in a guest whose driver understands it.
///
/// Unlike the PS/2 mouse this reports a screen position rather than a delta,
/// which is why guests prefer it once their tools are installed: the pointer
/// tracks the host's cursor exactly, with none of the acceleration curves
/// and edge clamping a relative mouse needs a driver to approximate. The
/// channel has no interrupt of its own -- see
/// <see cref="Input.Ps2Mouse.RequestWakeupPacket"/> for how the PS/2 mouse
/// signals that a packet is waiting here.
/// </summary>
public sealed partial class VmwareBackdoor
{
    // ABSPOINTER_COMMAND sub-commands (EBX in), docs/VMWARE-BACKDOOR.md section 4.
    private const uint PointerCommandEnable = 0x45414552;
    private const uint PointerCommandDisable = 0x000000F5;
    private const uint PointerCommandRequestAbsolute = 0x53424152;
    private const uint PointerCommandRequestRelative = 0x4C455252;

    /// <summary>
    /// The version ENABLE queues as the channel's first dword, which the
    /// Linux <c>vmmouse</c> driver reads back through ABSPOINTER_DATA to
    /// confirm the channel actually answered before it trusts anything else
    /// on it.
    /// </summary>
    private const uint PointerVersion = 0x3442554A;

    // Packet button bits (docs/VMWARE-BACKDOOR.md section 4), distinct from
    // Input.MouseButtons' own PS/2 bit order -- the two protocols were
    // designed a decade apart and never agreed on which bit means what.
    private const uint PacketButtonLeft = 0x20;
    private const uint PacketButtonRight = 0x10;
    private const uint PacketButtonMiddle = 0x08;

    /// <summary>
    /// The queue is flat dwords rather than whole packets because
    /// ABSPOINTER_DATA lets the guest ask for one dword at a time (used for
    /// the version probe) or four at once (a whole packet); a driver is free
    /// to drain it however it likes, so the device does not get to assume
    /// packets are read as a unit.
    /// </summary>
    private readonly Queue<uint> _pointerQueue = new();

    /// <summary>
    /// Bounds the queue to 64 packets' worth of dwords. Without a bound, a
    /// guest that stops polling -- a driver not yet loaded, or one that has
    /// crashed -- would grow the queue without limit while the host keeps
    /// moving the mouse.
    /// </summary>
    private const int MaximumQueuedDwords = 64 * 4;

    /// <summary>Whether the vmmouse channel is enabled, following ABSPOINTER_COMMAND.</summary>
    public bool PointerEnabled { get; private set; }

    /// <summary>
    /// Whether the channel currently reports absolute (screen) coordinates
    /// rather than relative deltas, following the last "request absolute" or
    /// "request relative" sub-command.
    /// </summary>
    public bool PointerAbsolute { get; private set; }

    /// <summary>
    /// Queues one absolute-pointer packet from the host's own mouse. Does
    /// nothing while the channel is disabled -- a guest that has not enabled
    /// vmmouse is not polling it, so there is nothing to wake up for.
    /// </summary>
    public void QueueAbsolutePointer(int x, int y, MouseButtons buttons, int wheel)
    {
        if (!PointerEnabled) return;

        uint status = 0;
        if ((buttons & MouseButtons.Left) != 0) status |= PacketButtonLeft;
        if ((buttons & MouseButtons.Right) != 0) status |= PacketButtonRight;
        if ((buttons & MouseButtons.Middle) != 0) status |= PacketButtonMiddle;

        EnqueuePacket(status, (uint)(ushort)x, (uint)(ushort)y, unchecked((uint)wheel));
        _pointerDataReady();
    }

    private void EnqueuePacket(uint statusAndButtons, uint x, uint y, uint wheel)
    {
        MakeRoomForDwords(4);
        _pointerQueue.Enqueue(statusAndButtons);
        _pointerQueue.Enqueue(x);
        _pointerQueue.Enqueue(y);
        _pointerQueue.Enqueue(wheel);
    }

    /// <summary>
    /// Drops whole packets from the front of the queue until there is room
    /// for <paramref name="incoming"/> more dwords, which is the "drop
    /// oldest" policy the plan calls for: a driver that has fallen behind
    /// cares about where the mouse is now, not where it was several packets
    /// ago.
    /// </summary>
    private void MakeRoomForDwords(int incoming)
    {
        while (_pointerQueue.Count + incoming > MaximumQueuedDwords && _pointerQueue.Count > 0)
        {
            _pointerQueue.Dequeue();
        }
    }

    private void HandlePointerCommand(CpuState state)
    {
        switch (state.Ebx)
        {
            case PointerCommandEnable:
                PointerEnabled = true;
                _pointerQueue.Clear();
                MakeRoomForDwords(1);
                _pointerQueue.Enqueue(PointerVersion);
                break;

            case PointerCommandDisable:
                PointerEnabled = false;
                _pointerQueue.Clear();
                break;

            case PointerCommandRequestAbsolute:
                PointerAbsolute = true;
                break;

            case PointerCommandRequestRelative:
                PointerAbsolute = false;
                break;
        }
    }

    private void HandlePointerStatus(CpuState state)
    {
        // FFFF0000h while disabled is exactly what open-vm-tools' vmmouse
        // probe treats as "no such device": the high word of a healthy
        // queue length can never look like that, since a queue holds at
        // most MaximumQueuedDwords entries.
        state.Eax = PointerEnabled ? (uint)_pointerQueue.Count : 0xFFFF0000;
    }

    private void HandlePointerData(CpuState state)
    {
        uint wanted = state.Ebx;

        if (wanted >= 1 && _pointerQueue.TryDequeue(out uint first)) state.Eax = first;
        if (wanted >= 2 && _pointerQueue.TryDequeue(out uint second)) state.Ebx = second;
        if (wanted >= 3 && _pointerQueue.TryDequeue(out uint third)) state.Ecx = third;
        if (wanted >= 4 && _pointerQueue.TryDequeue(out uint fourth)) state.Edx = fourth;
    }
}
