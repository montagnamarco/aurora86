using System.Text;
using Aurora386.Core.Cpu;

namespace Aurora386.Core.Devices.Host;

/// <summary>
/// The RPC channel MESSAGE (command 30) carries -- open-vm-tools' clipboard,
/// "tools.set.version" and every other exchange VMware Tools calls "RPCI",
/// plus the host-to-guest "TCLO" commands a guest polls for. Both directions
/// share the same wire protocol: open a channel, announce a length, push it
/// four bytes at a time, then pull the reply back the same way
/// (docs/VMWARE-BACKDOOR.md, section 5, citing open-vm-tools'
/// <c>lib/message/message.c</c> and <c>guest_msg_def.h</c>).
/// </summary>
public sealed partial class VmwareBackdoor
{
    // Sub-command, ECX's high word on the way in (guest_msg_def.h's enum).
    private const uint MessageOpen = 0;
    private const uint MessageSendSize = 1;
    private const uint MessageSendPayload = 2;
    private const uint MessageRecvSize = 3;
    private const uint MessageRecvPayload = 4;
    private const uint MessageRecvStatus = 5;
    private const uint MessageClose = 6;

    // Status bits, ECX's high word on the way out.
    private const uint StatusSuccess = 0x0001;
    private const uint StatusDoRecv = 0x0002;
    private const uint StatusClosed = 0x0004;
    private const uint StatusUnsent = 0x0008;

    // OPEN's protocol numbers (EBX in), and the bit above them that asks the
    // channel to hand back a cookie pair every later call must repeat.
    private const uint ProtocolRpci = 0x49435052;
    private const uint ProtocolTclo = 0x4F4C4354;
    private const uint CookieRequestBit = 0x80000000;

    /// <summary>
    /// VMware allows eight channels open at once; the guest picks none of
    /// them, OPEN hands back whichever id is free.
    /// </summary>
    private const int MaximumChannels = 8;

    /// <summary>
    /// A request longer than this makes SENDSIZE fail outright rather than
    /// accept a length the host is not going to hold in memory for a guest
    /// that may never finish sending it.
    /// </summary>
    private const int MaximumRequestBytes = 64 * 1024;

    /// <summary>Mirrors <see cref="MaximumRequestBytes"/> for whatever a host answers with.</summary>
    private const int MaximumReplyBytes = 64 * 1024;

    private readonly Dictionary<int, Channel> _channels = new();

    /// <summary>
    /// Cookie generation only has to avoid handing the same pair to two
    /// channels a guest might confuse with each other; it is not a security
    /// boundary, so a simple multiplicative hash of an incrementing counter
    /// is enough, and it keeps a snapshot-free run reproducible for tests.
    /// </summary>
    private uint _nextCookieSeed = 1;

    /// <summary>One open channel: which protocol, whether it demands cookies, and its half-finished transfer.</summary>
    private sealed class Channel
    {
        public uint Protocol;
        public bool CookiesEnabled;
        public uint CookieA;
        public uint CookieB;
        public uint RequestLength;

        /// <summary>True between a non-empty SENDSIZE and the payload dword that completes it.</summary>
        public bool AwaitingPayload;
        public readonly List<byte> RequestBuffer = new();
        public byte[]? ReplyBytes;
        public int ReplyOffset;
        public bool HasReply;
    }

    private enum ChannelLookup
    {
        Found,
        UnknownChannel,
        CookieMismatch,
    }

    private void HandleMessage(CpuState state)
    {
        uint subCommand = state.Ecx >> 16;
        switch (subCommand)
        {
            case MessageOpen: HandleMessageOpen(state); break;
            case MessageSendSize: HandleMessageSendSize(state); break;
            case MessageSendPayload: HandleMessageSendPayload(state); break;
            case MessageRecvSize: HandleMessageRecvSize(state); break;
            case MessageRecvPayload: HandleMessageRecvPayload(state); break;
            case MessageRecvStatus: HandleMessageRecvStatus(state); break;
            case MessageClose: HandleMessageClose(state); break;

            // A sub-command the real protocol never defines is answered with
            // no status bits at all, the same as any other call this device
            // cannot honour.
            default: SetMessageStatus(state, 0); break;
        }
    }

    private void HandleMessageOpen(CpuState state)
    {
        int channelId = FindFreeChannelId();
        if (channelId < 0)
        {
            SetMessageStatus(state, 0);   // all eight channels already open
            return;
        }

        bool wantsCookies = (state.Ebx & CookieRequestBit) != 0;
        var channel = new Channel
        {
            Protocol = state.Ebx & ~CookieRequestBit,
            CookiesEnabled = wantsCookies,
        };

        if (wantsCookies)
        {
            channel.CookieA = NextCookie();
            channel.CookieB = NextCookie();
            state.Esi = channel.CookieA;
            state.Edi = channel.CookieB;
        }

        _channels[channelId] = channel;
        state.Edx = (state.Edx & 0xFFFF) | ((uint)channelId << 16);
        SetMessageStatus(state, StatusSuccess);
    }

    private void HandleMessageSendSize(CpuState state)
    {
        ChannelLookup lookup = TryGetChannel(state, out int _, out Channel? channel);
        if (!ReportFailedLookup(state, lookup)) return;

        uint length = state.Ebx;
        if (length > MaximumRequestBytes)
        {
            // Too large to buffer; refuse before a single byte arrives
            // rather than accept a promise the host cannot keep.
            SetMessageStatus(state, 0);
            return;
        }

        channel!.RequestLength = length;
        channel.RequestBuffer.Clear();
        channel.HasReply = false;
        channel.ReplyBytes = null;
        channel.ReplyOffset = 0;
        channel.AwaitingPayload = length > 0;

        // An empty request -- exactly what a TCLO poll sends -- has nothing
        // more coming, so the reply is ready immediately.
        if (length == 0)
        {
            PrepareReply(channel);
        }

        SetMessageStatus(state, StatusSuccess);
    }

    private void HandleMessageSendPayload(CpuState state)
    {
        ChannelLookup lookup = TryGetChannel(state, out int _, out Channel? channel);
        if (!ReportFailedLookup(state, lookup)) return;

        // Payload with no request announced, or after it was complete: the
        // host has already answered, and answering again would repeat the
        // request's effects and lose a queued host command.
        if (!channel!.AwaitingPayload)
        {
            SetMessageStatus(state, 0);
            return;
        }

        int stillWanted = (int)channel.RequestLength - channel.RequestBuffer.Count;
        int bytesFromThisDword = Math.Clamp(stillWanted, 0, 4);
        uint dword = state.Ebx;
        for (int index = 0; index < bytesFromThisDword; index++)
        {
            channel.RequestBuffer.Add((byte)(dword >> (index * 8)));
        }

        if (channel.RequestBuffer.Count >= channel.RequestLength)
        {
            channel.AwaitingPayload = false;
            PrepareReply(channel);
        }

        SetMessageStatus(state, StatusSuccess);
    }

    private void HandleMessageRecvSize(CpuState state)
    {
        ChannelLookup lookup = TryGetChannel(state, out int _, out Channel? channel);
        if (!ReportFailedLookup(state, lookup)) return;

        state.Ebx = channel!.HasReply ? (uint)channel.ReplyBytes!.Length : 0;
        state.Edx = (state.Edx & 0xFFFF) | (MessageSendSize << 16);   // "1", the send-side sub-command this mirrors

        uint status = StatusSuccess | (channel.HasReply ? StatusDoRecv : StatusUnsent);
        SetMessageStatus(state, status);
    }

    private void HandleMessageRecvPayload(CpuState state)
    {
        ChannelLookup lookup = TryGetChannel(state, out int _, out Channel? channel);
        if (!ReportFailedLookup(state, lookup)) return;

        // The guest acknowledges the previous chunk by presenting SUCCESS
        // before asking for the next one; anything else aborts the transfer
        // rather than let it desynchronise silently.
        if (state.Ebx != StatusSuccess)
        {
            SetMessageStatus(state, 0);
            return;
        }

        byte[] replyBytes = channel!.ReplyBytes ?? Array.Empty<byte>();
        uint dword = 0;
        for (int index = 0; index < 4; index++)
        {
            if (channel.ReplyOffset >= replyBytes.Length) break;
            dword |= (uint)replyBytes[channel.ReplyOffset] << (index * 8);
            channel.ReplyOffset++;
        }

        state.Ebx = dword;
        state.Edx = (state.Edx & 0xFFFF) | (MessageSendPayload << 16);   // "2", the send-side sub-command this mirrors

        bool moreToCome = channel.ReplyOffset < replyBytes.Length;
        SetMessageStatus(state, StatusSuccess | (moreToCome ? StatusDoRecv : 0));
    }

    private void HandleMessageRecvStatus(CpuState state)
    {
        ChannelLookup lookup = TryGetChannel(state, out int _, out Channel? channel);
        if (!ReportFailedLookup(state, lookup)) return;

        // The guest's SUCCESS here just acknowledges it drained the reply;
        // clearing the channel's transfer state readies it for its next
        // request without forcing a CLOSE and a fresh OPEN.
        channel!.HasReply = false;
        channel.ReplyBytes = null;
        channel.ReplyOffset = 0;
        channel.RequestBuffer.Clear();
        channel.RequestLength = 0;
        channel.AwaitingPayload = false;

        SetMessageStatus(state, StatusSuccess);
    }

    private void HandleMessageClose(CpuState state)
    {
        ChannelLookup lookup = TryGetChannel(state, out int channelId, out Channel? _);
        if (!ReportFailedLookup(state, lookup)) return;

        _channels.Remove(channelId);
        SetMessageStatus(state, StatusSuccess);
    }

    /// <summary>
    /// Hands the assembled request to the far end and buffers whatever it
    /// answers with, so RECVSIZE and RECVPAYLOAD have something to drain.
    /// </summary>
    private void PrepareReply(Channel channel)
    {
        string reply;
        if (channel.Protocol == ProtocolTclo)
        {
            // A TCLO channel carries no request text of its own -- the guest
            // sends an empty message purely to poll -- so the "request" is
            // discarded and the reply is whatever the host has queued.
            reply = RpcHost.NextHostCommand() ?? string.Empty;
        }
        else
        {
            string request = Encoding.ASCII.GetString(channel.RequestBuffer.ToArray());
            reply = RpcHost.HandleRequest(request) ?? string.Empty;
        }

        if (reply.Length > MaximumReplyBytes) reply = reply[..MaximumReplyBytes];

        channel.ReplyBytes = Encoding.ASCII.GetBytes(reply);
        channel.ReplyOffset = 0;
        channel.HasReply = true;
    }

    private int FindFreeChannelId()
    {
        for (int candidate = 0; candidate < MaximumChannels; candidate++)
        {
            if (!_channels.ContainsKey(candidate)) return candidate;
        }

        return -1;
    }

    private uint NextCookie()
    {
        // A Knuth multiplicative hash of an incrementing seed: cheap, and
        // different enough between successive calls that a guest which
        // accidentally reuses a stale cookie will not match by chance.
        _nextCookieSeed = unchecked(_nextCookieSeed * 2654435761u + 1);
        return _nextCookieSeed;
    }

    private ChannelLookup TryGetChannel(CpuState state, out int channelId, out Channel? channel)
    {
        channelId = (int)(state.Edx >> 16);
        if (!_channels.TryGetValue(channelId, out Channel? found))
        {
            channel = null;
            return ChannelLookup.UnknownChannel;
        }

        if (found.CookiesEnabled && (found.CookieA != state.Esi || found.CookieB != state.Edi))
        {
            channel = null;
            return ChannelLookup.CookieMismatch;
        }

        channel = found;
        return ChannelLookup.Found;
    }

    /// <summary>
    /// Reports a failed channel lookup in the register a caller checks
    /// (ECX's high word) and returns false, so every MESSAGE handler can
    /// read its result and bail out in one line.
    /// </summary>
    private bool ReportFailedLookup(CpuState state, ChannelLookup lookup)
    {
        switch (lookup)
        {
            case ChannelLookup.UnknownChannel:
                SetMessageStatus(state, StatusClosed);
                return false;
            case ChannelLookup.CookieMismatch:
                // No bit names "wrong cookie" specifically; the guarantee
                // that matters is the one every caller relies on -- SUCCESS
                // stays clear -- so nothing else is asserted here.
                SetMessageStatus(state, 0);
                return false;
            default:
                return true;
        }
    }

    /// <summary>
    /// Writes the status bits into ECX's high word, leaving the low word --
    /// still 30, the MESSAGE command itself -- exactly as it arrived.
    /// </summary>
    private static void SetMessageStatus(CpuState state, uint statusBits)
    {
        state.Ecx = (state.Ecx & 0xFFFF) | (statusBits << 16);
    }

    /// <summary>
    /// The RPC host used until the owner wires in a real one: every RPCI
    /// request succeeds trivially and no TCLO command is ever queued, so
    /// that a guest probing the channel gets an answer that lets it move on
    /// rather than retrying forever.
    /// </summary>
    private sealed class DefaultGuestRpcHost : IGuestRpcHost
    {
        public string HandleRequest(string request) => "1 ";

        public string? NextHostCommand() => null;
    }
}
