using System.IO;
using Aurora386.Core.Cpu;

namespace Aurora386.Core.Devices.Host;

/// <summary>
/// VMware's backdoor I/O port, offered since the late 1990s as a channel
/// from a guest to the virtualisation layer beneath it (docs/VMWARE-BACKDOOR.md,
/// which cites open-vm-tools, the Linux kernel's <c>vmmouse.c</c> and QEMU's
/// <c>vmport.c</c> for every field this class fills in).
///
/// A guest that wants to talk to the backdoor loads <c>EAX</c> with the
/// magic <c>"VMXh"</c>, <c>ECX</c> with a command, and executes
/// <c>IN EAX, DX</c> with <c>DX = 5658h</c>. That single instruction reads
/// and writes six general registers, which is why every command below is
/// described as "in" and "out" register assignments rather than a return
/// value: the processor calls <see cref="TryHandleInput"/> from
/// <see cref="IBackdoorPort.TryHandleInput"/>, itself invoked from opcode
/// <c>0xED</c> before the ordinary I/O permission check, because VMware
/// answers this call from any ring -- VMware Tools' Windows installer probes
/// the port from ring 3 and refuses to install if it faults.
///
/// The Linux kernel's detection code (<c>arch/x86/kernel/cpu/vmware.c</c>)
/// and the <c>vmmouse</c> driver both drive this class directly; open-vm-tools
/// drives the RPC channel in <see cref="HandleMessage"/>.
/// </summary>
public sealed partial class VmwareBackdoor : IBackdoorPort
{
    /// <summary>The low-bandwidth backdoor port. Any other port is not this device's.</summary>
    public const ushort Port = 0x5658;

    /// <summary>"VMXh", the magic EAX must carry for an access to be a backdoor call at all.</summary>
    public const uint Magic = 0x564D5868;

    // Command numbers (ECX's low word, docs/VMWARE-BACKDOOR.md section 3).
    // The clipboard quartet (6-9) is declared in VmwareBackdoor.Clipboard.cs,
    // next to the handlers that answer them.
    private const ushort CommandGetMhz = 1;
    private const ushort CommandGetVersion = 10;
    private const ushort CommandGetScreenSize = 15;
    private const ushort CommandGetTime = 23;
    private const ushort CommandMessage = 30;
    private const ushort CommandAbsPointerData = 39;
    private const ushort CommandAbsPointerStatus = 40;
    private const ushort CommandAbsPointerCommand = 41;
    private const ushort CommandGetTimeFull = 46;

    /// <summary>An unknown command's answer: EAX = FFFFFFFFh, every other register untouched.</summary>
    private const uint UnknownCommandResult = 0xFFFFFFFF;

    // GETVERSION answers with the same numbers QEMU's vmport.c defaults to:
    // hypervisor version 6, VMX type 2 ("scalable server"). Nothing in the
    // guests this device targets branches on either field; they exist only
    // so that a version probe gets a plausible answer instead of a fault.
    private const uint HypervisorVersion = 6;
    private const uint VmxTypeScalableServer = 2;

    // GETTIME's "maximum lag" is how far the guest's clock may drift before
    // a time-sync tool resynchronises it. Aurora386 has no drift to report --
    // the guest clock is the host clock, read fresh on every call -- so this
    // is a fixed, generous bound rather than a measured one.
    private const uint MaxTimeLagMicroseconds = 1_000_000;

    // A tick is 100 nanoseconds, so ten of them make one microsecond.
    private const long TicksPerMicrosecond = 10;

    private readonly Func<int> _processorMegahertz;
    private readonly Func<(int Width, int Height)> _screenSize;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action _pointerDataReady;

    /// <summary>
    /// Whether the port answers at all. Off by default and until the machine's
    /// configuration turns it on: with it off, a privileged <c>IN</c> from
    /// 5658h reads FFFFFFFFh and EBX never echoes the magic, which is exactly
    /// what a real PC does and what detection code tests for.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The host clipboard the legacy selection commands read and write
    /// (docs/VMWARE-BACKDOOR.md section 8), or null when the front end
    /// offers none: the guest then sees an empty host selection and its own
    /// is discarded.
    /// </summary>
    public IHostClipboard? Clipboard { get; set; }

    /// <summary>
    /// The far end of the RPC channel (section 5). Defaults to
    /// <see cref="DefaultGuestRpcHost"/>, which answers every RPCI request
    /// with success and every TCLO poll with nothing queued, so that guests
    /// probing the channel do not retry forever waiting for a real service.
    /// </summary>
    public IGuestRpcHost RpcHost { get; set; }

    /// <param name="processorMegahertz">
    /// Answers GETMHZ. A function rather than a fixed number because the
    /// configured processor model can change after this device is built.
    /// </param>
    /// <param name="screenSize">Answers GETSCREENSIZE with the displayed picture's width and height.</param>
    /// <param name="clock">Answers GETTIME and GETTIMEFULL. Injected so tests can fix the time.</param>
    /// <param name="pointerDataReady">
    /// Called whenever <see cref="QueueAbsolutePointer"/> queues a fresh
    /// packet, so the owner can wake the PS/2 mouse's empty-packet signal
    /// (see docs/VMWARE-BACKDOOR.md section 4 and
    /// <see cref="Input.Ps2Mouse.RequestWakeupPacket"/>).
    /// </param>
    public VmwareBackdoor(Func<int> processorMegahertz, Func<(int Width, int Height)> screenSize,
                          Func<DateTimeOffset> clock, Action pointerDataReady)
    {
        _processorMegahertz = processorMegahertz;
        _screenSize = screenSize;
        _clock = clock;
        _pointerDataReady = pointerDataReady;
        RpcHost = new DefaultGuestRpcHost();
    }

    /// <summary>
    /// Handles a 32-bit <c>IN EAX, DX</c>. Declines -- returns false without
    /// touching any register -- unless the device is enabled, the port is
    /// 5658h and EAX already carries the magic; that is exactly the gate
    /// docs/VMWARE-BACKDOOR.md section 1 describes, and matching it precisely
    /// matters because everything that is not a backdoor call must fall
    /// through to the ordinary port and its architectural checks.
    /// </summary>
    public bool TryHandleInput(CpuState state, ushort port)
    {
        if (!Enabled) return false;
        if (port != Port) return false;
        if (state.Eax != Magic) return false;

        ushort command = (ushort)(state.Ecx & 0xFFFF);
        switch (command)
        {
            case CommandGetSelLength:
                HandleGetSelLength(state);
                break;

            case CommandGetNextPiece:
                HandleGetNextPiece(state);
                break;

            case CommandSetSelLength:
                HandleSetSelLength(state);
                break;

            case CommandSetNextPiece:
                HandleSetNextPiece(state);
                break;

            case CommandGetMhz:
                state.Eax = (uint)_processorMegahertz();
                state.Ebx = Magic;
                break;

            case CommandGetVersion:
                state.Eax = HypervisorVersion;
                state.Ebx = Magic;
                state.Ecx = VmxTypeScalableServer;
                break;

            case CommandGetScreenSize:
            {
                (int width, int height) = _screenSize();
                state.Eax = ((uint)width << 16) | (uint)(ushort)height;
                break;
            }

            case CommandGetTime:
                HandleGetTime(state);
                break;

            case CommandGetTimeFull:
                HandleGetTimeFull(state);
                break;

            case CommandMessage:
                HandleMessage(state);
                break;

            case CommandAbsPointerData:
                HandlePointerData(state);
                break;

            case CommandAbsPointerStatus:
                HandlePointerStatus(state);
                break;

            case CommandAbsPointerCommand:
                HandlePointerCommand(state);
                break;

            default:
                // open-vm-tools' time-sync plugin relies on exactly this to
                // probe for commands an older backdoor does not implement,
                // so the other registers must survive untouched.
                state.Eax = UnknownCommandResult;
                break;
        }

        return true;
    }

    private void HandleGetTime(CpuState state)
    {
        DateTimeOffset now = _clock();
        long seconds = now.ToUnixTimeSeconds();
        long microsecondsIntoSecond = now.Ticks % TimeSpan.TicksPerSecond / TicksPerMicrosecond;

        state.Eax = (uint)seconds;
        state.Ebx = (uint)microsecondsIntoSecond;
        state.Ecx = MaxTimeLagMicroseconds;
        state.Edx = 0;
    }

    private void HandleGetTimeFull(CpuState state)
    {
        DateTimeOffset now = _clock();
        long seconds = now.ToUnixTimeSeconds();
        long microsecondsIntoSecond = now.Ticks % TimeSpan.TicksPerSecond / TicksPerMicrosecond;

        // The full form carries a 64-bit second count, split high:low across
        // ESI and EDX, for guests still running after the year 2038.
        state.Eax = Magic;
        state.Edx = unchecked((uint)seconds);
        state.Esi = unchecked((uint)((ulong)seconds >> 32));
        state.Ebx = (uint)microsecondsIntoSecond;
        state.Ecx = MaxTimeLagMicroseconds;
    }

    /// <summary>
    /// Restores power-on state: the port stops answering ABSPOINTER_DATA
    /// with anything queued and every open RPC channel disappears, exactly
    /// as section 6 says a guest must expect after a VMware checkpoint.
    /// Any clipboard transfer in progress (section 8) is dropped the same
    /// way, since it is neither published state a guest can query nor part
    /// of the snapshot format.
    /// </summary>
    public void Reset()
    {
        PointerEnabled = false;
        PointerAbsolute = false;
        _pointerQueue.Clear();
        _channels.Clear();
        _nextCookieSeed = 1;
        ResetClipboardTransfers();
    }

    // --- snapshot ------------------------------------------------------------
    //
    // Section 6 keeps this deliberately narrow: only whether the port is
    // enabled and the absolute pointer's mode survive a snapshot. Queued
    // pointer packets and open RPC channels do not, because a guest already
    // has to cope with losing both across a real VMware checkpoint -- that is
    // what MESSAGE_STATUS_CPT exists to tell it -- so a snapshot restore can
    // rely on the same recovery path rather than reproduce transient state.

    /// <summary>The snapshot format's version, bumped if a future field is added.</summary>
    public const int SnapshotVersion = 1;

    /// <summary>The fixed length of every capture this version produces.</summary>
    public const int SnapshotLength = 4;

    private const int SnapshotOffsetVersion = 0;
    private const int SnapshotOffsetEnabled = 1;
    private const int SnapshotOffsetPointerEnabled = 2;
    private const int SnapshotOffsetPointerAbsolute = 3;

    /// <summary>Captures whether the port is enabled and the pointer's mode (section 6).</summary>
    public byte[] Capture()
    {
        var payload = new byte[SnapshotLength];
        payload[SnapshotOffsetVersion] = SnapshotVersion;
        payload[SnapshotOffsetEnabled] = BoolToByte(Enabled);
        payload[SnapshotOffsetPointerEnabled] = BoolToByte(PointerEnabled);
        payload[SnapshotOffsetPointerAbsolute] = BoolToByte(PointerAbsolute);
        return payload;
    }

    /// <summary>Checks the format without touching a device, for a preflight before restore.</summary>
    public static void ValidateSnapshot(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != SnapshotLength)
        {
            throw new InvalidDataException(
                $"VMware backdoor snapshot must be {SnapshotLength} bytes, was {payload.Length}.");
        }

        if (payload[SnapshotOffsetVersion] != SnapshotVersion)
        {
            throw new InvalidDataException(
                $"VMware backdoor snapshot version {payload[SnapshotOffsetVersion]} is not supported.");
        }

        ValidateBoolean(payload[SnapshotOffsetEnabled], "Enabled");
        ValidateBoolean(payload[SnapshotOffsetPointerEnabled], "PointerEnabled");
        ValidateBoolean(payload[SnapshotOffsetPointerAbsolute], "PointerAbsolute");
    }

    /// <summary>
    /// Installs a validated capture. Queued packets, open channels and any
    /// clipboard transfer in progress are not part of the format, so
    /// restoring always leaves them as <see cref="Reset"/> would: empty and
    /// closed.
    /// </summary>
    public void Restore(ReadOnlySpan<byte> payload)
    {
        ValidateSnapshot(payload);

        Enabled = payload[SnapshotOffsetEnabled] != 0;
        PointerEnabled = payload[SnapshotOffsetPointerEnabled] != 0;
        PointerAbsolute = payload[SnapshotOffsetPointerAbsolute] != 0;
        _pointerQueue.Clear();
        _channels.Clear();
        _nextCookieSeed = 1;
        ResetClipboardTransfers();
    }

    private static byte BoolToByte(bool value) => value ? (byte)1 : (byte)0;

    private static void ValidateBoolean(byte value, string fieldName)
    {
        if (value > 1)
        {
            throw new InvalidDataException($"VMware backdoor snapshot field '{fieldName}' is not a boolean.");
        }
    }
}
