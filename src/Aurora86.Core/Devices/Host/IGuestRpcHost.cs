namespace Aurora86.Core.Devices.Host;

/// <summary>
/// The far end of the VMware RPC channel (docs/VMWARE-BACKDOOR.md, section 5):
/// whatever answers "RPCI" requests the guest sends and whatever queues
/// "TCLO" commands for the guest to poll for. open-vm-tools and VMware Tools
/// use this channel for the clipboard, for "tools.set.version" and similar
/// housekeeping, and for the host-driven side of the protocol that lets the
/// host push a command into the guest rather than wait to be asked.
///
/// <see cref="VmwareBackdoor"/> owns the wire protocol -- channels, cookies,
/// the four-byte-at-a-time transfer -- and hands the assembled request text
/// to this interface once it is complete. Aurora86 supplies a default
/// implementation that answers every RPCI request with "1 " (see
/// <see cref="VmwareBackdoor.DefaultGuestRpcHost"/>) so that tools do not
/// retry, and a real one is wired in once a specific service -- the
/// clipboard, first -- needs to act on what arrives.
/// </summary>
public interface IGuestRpcHost
{
    /// <summary>
    /// Answers a guest-to-host ("RPCI") request with the reply text the
    /// backdoor hands back through RECVSIZE/RECVPAYLOAD. A reply beginning
    /// with "1 " is success and "0 " is failure, by the convention every
    /// RPCI caller relies on (docs/VMWARE-BACKDOOR.md, section 5).
    /// </summary>
    string HandleRequest(string request);

    /// <summary>
    /// Returns the next host-to-guest ("TCLO") command waiting for the
    /// guest, or null when there is none. The guest discovers this by
    /// polling: opening a TCLO channel and sending an empty request, whose
    /// reply is the queued command or an empty string.
    /// </summary>
    string? NextHostCommand();
}
