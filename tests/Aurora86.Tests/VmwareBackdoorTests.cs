using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Host;
using Aurora86.Core.Devices.Input;

namespace Aurora86.Tests;

/// <summary>
/// The VMware backdoor port, docs/VMWARE-BACKDOOR.md.
///
/// Every command here is a register layout a real driver reads: Linux's
/// <c>vmware.c</c> detection code, its <c>vmmouse.c</c> absolute pointer
/// driver, and open-vm-tools' RPC channel. The tests name the register the
/// way those drivers do, and the last two drive the actual CPU rather than
/// call the device directly, because the whole point of this port is that
/// it answers <c>IN EAX, DX</c> before the processor's own I/O permission
/// check -- a claim only the real decoder can verify.
/// </summary>
public class VmwareBackdoorTests
{
    private static VmwareBackdoor NewDevice(int processorMegahertz = 133,
                                            (int Width, int Height)? screenSize = null,
                                            DateTimeOffset? clock = null)
    {
        (int Width, int Height) size = screenSize ?? (640, 480);
        DateTimeOffset time = clock ?? DateTimeOffset.UtcNow;
        return new VmwareBackdoor(() => processorMegahertz, () => size, () => time, () => { });
    }

    private static CpuState NewCallWithMagic(ushort command)
    {
        var state = new CpuState();
        state.Eax = VmwareBackdoor.Magic;
        state.Ecx = command;
        return state;
    }

    // --- GETMHZ, GETVERSION, GETSCREENSIZE, GETTIME, GETTIMEFULL -------------

    [Fact]
    public void GetVersionReportsTheHypervisorVersionAndTheMagicInEbx()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        CpuState state = NewCallWithMagic(10);   // GETVERSION

        bool handled = device.TryHandleInput(state, VmwareBackdoor.Port);

        Assert.True(handled);
        Assert.Equal(6u, state.Eax);
        Assert.Equal(VmwareBackdoor.Magic, state.Ebx);
        Assert.Equal(2u, state.Ecx);
    }

    [Fact]
    public void GetMhzReportsTheConfiguredProcessorSpeed()
    {
        VmwareBackdoor device = NewDevice(processorMegahertz: 233);
        device.Enabled = true;
        CpuState state = NewCallWithMagic(1);   // GETMHZ

        Assert.True(device.TryHandleInput(state, VmwareBackdoor.Port));
        Assert.Equal(233u, state.Eax);
        Assert.Equal(VmwareBackdoor.Magic, state.Ebx);
    }

    [Fact]
    public void GetScreenSizePacksWidthAndHeightIntoEax()
    {
        VmwareBackdoor device = NewDevice(screenSize: (800, 600));
        device.Enabled = true;
        CpuState state = NewCallWithMagic(15);   // GETSCREENSIZE

        Assert.True(device.TryHandleInput(state, VmwareBackdoor.Port));
        Assert.Equal((800u << 16) | 600u, state.Eax);
    }

    [Fact]
    public void GetTimeReportsSecondsAndMicrosecondsFromTheInjectedClock()
    {
        var fixedTime = new DateTimeOffset(2024, 3, 1, 10, 20, 30, 250, TimeSpan.Zero);
        VmwareBackdoor device = NewDevice(clock: fixedTime);
        device.Enabled = true;
        CpuState state = NewCallWithMagic(23);   // GETTIME

        Assert.True(device.TryHandleInput(state, VmwareBackdoor.Port));
        Assert.Equal((uint)fixedTime.ToUnixTimeSeconds(), state.Eax);
        Assert.Equal(250_000u, state.Ebx);   // 250 ms into the second, in microseconds
        Assert.True(state.Ecx > 0, "GETTIME must report a positive maximum lag");
        Assert.Equal(0u, state.Edx);
    }

    [Fact]
    public void GetTimeFullSplitsSecondsAcrossEsiAndEdxAndCarriesTheMagicInEax()
    {
        var fixedTime = new DateTimeOffset(2024, 3, 1, 10, 20, 30, 750, TimeSpan.Zero);
        VmwareBackdoor device = NewDevice(clock: fixedTime);
        device.Enabled = true;
        CpuState state = NewCallWithMagic(46);   // GETTIMEFULL

        Assert.True(device.TryHandleInput(state, VmwareBackdoor.Port));
        Assert.Equal(VmwareBackdoor.Magic, state.Eax);
        Assert.Equal((uint)fixedTime.ToUnixTimeSeconds(), state.Edx);
        Assert.Equal(0u, state.Esi);   // no guest of this era needs the high 32 bits
        Assert.Equal(750_000u, state.Ebx);
        Assert.True(state.Ecx > 0);
    }

    [Fact]
    public void AnUnknownCommandReportsFfffffffAndLeavesEveryOtherRegister()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        CpuState state = NewCallWithMagic(9999);
        state.Ebx = 0x11111111;
        state.Edx = 0x22222222;

        Assert.True(device.TryHandleInput(state, VmwareBackdoor.Port));
        Assert.Equal(0xFFFFFFFFu, state.Eax);
        Assert.Equal(0x11111111u, state.Ebx);
        Assert.Equal(0x22222222u, state.Edx);
    }

    [Fact]
    public void ADisabledDeviceDeclinesAndTouchesNoRegister()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = false;
        CpuState state = NewCallWithMagic(10);
        state.Ebx = 0x33333333;

        bool handled = device.TryHandleInput(state, VmwareBackdoor.Port);

        Assert.False(handled);
        Assert.Equal(VmwareBackdoor.Magic, state.Eax);
        Assert.Equal(10u, state.Ecx);
        Assert.Equal(0x33333333u, state.Ebx);
    }

    [Fact]
    public void AccessWithoutTheMagicInEaxIsNotABackdoorCall()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        var state = new CpuState();
        state.Eax = 0x12345678;
        state.Ecx = 10;

        bool handled = device.TryHandleInput(state, VmwareBackdoor.Port);

        Assert.False(handled);
        Assert.Equal(0x12345678u, state.Eax);
    }

    // --- the absolute pointer (vmmouse) ---------------------------------------

    [Fact]
    public void VmmouseEnablesReadsItsVersionThenDrainsAQueuedPacket()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;

        // ABSPOINTER_COMMAND: enable. The channel queues its version as one
        // dword so the Linux vmmouse driver can confirm it is really there.
        CpuState enable = NewCallWithMagic(41);
        enable.Ebx = 0x45414552;
        Assert.True(device.TryHandleInput(enable, VmwareBackdoor.Port));
        Assert.True(device.PointerEnabled);

        // ABSPOINTER_STATUS: one dword waiting.
        CpuState status = NewCallWithMagic(40);
        Assert.True(device.TryHandleInput(status, VmwareBackdoor.Port));
        Assert.Equal(1u, status.Eax);

        // ABSPOINTER_DATA(1): that one dword is the version.
        CpuState data = NewCallWithMagic(39);
        data.Ebx = 1;
        Assert.True(device.TryHandleInput(data, VmwareBackdoor.Port));
        Assert.Equal(0x3442554Au, data.Eax);

        // ABSPOINTER_COMMAND: request absolute.
        CpuState requestAbsolute = NewCallWithMagic(41);
        requestAbsolute.Ebx = 0x53424152;
        Assert.True(device.TryHandleInput(requestAbsolute, VmwareBackdoor.Port));
        Assert.True(device.PointerAbsolute);

        // The host reports a click of the left and right buttons at (100, 200)
        // with the wheel turned back three notches.
        device.QueueAbsolutePointer(100, 200, MouseButtons.Left | MouseButtons.Right, wheel: -3);

        CpuState packet = NewCallWithMagic(39);
        packet.Ebx = 4;
        Assert.True(device.TryHandleInput(packet, VmwareBackdoor.Port));
        Assert.Equal(0x30u, packet.Eax);       // left (20h) and right (10h)
        Assert.Equal(100u, packet.Ebx);
        Assert.Equal(200u, packet.Ecx);
        Assert.Equal(unchecked((uint)-3), packet.Edx);

        // ABSPOINTER_COMMAND: disable.
        CpuState disable = NewCallWithMagic(41);
        disable.Ebx = 0x000000F5;
        Assert.True(device.TryHandleInput(disable, VmwareBackdoor.Port));

        CpuState statusAfterDisable = NewCallWithMagic(40);
        Assert.True(device.TryHandleInput(statusAfterDisable, VmwareBackdoor.Port));
        Assert.Equal(0xFFFF0000u, statusAfterDisable.Eax);
    }

    [Fact]
    public void MiddleButtonReportsBit08hInThePacketStatus()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        device.TryHandleInput(EnableAbsolutePointer(), VmwareBackdoor.Port);

        // Drain the version dword ENABLE queues, exactly as the driver does,
        // so the packet below lands at the front of the queue.
        CpuState drainVersion = NewCallWithMagic(39);
        drainVersion.Ebx = 1;
        device.TryHandleInput(drainVersion, VmwareBackdoor.Port);

        device.QueueAbsolutePointer(1, 2, MouseButtons.Middle, wheel: 0);

        CpuState packet = NewCallWithMagic(39);
        packet.Ebx = 4;
        device.TryHandleInput(packet, VmwareBackdoor.Port);

        Assert.Equal(0x08u, packet.Eax);
    }

    private static CpuState EnableAbsolutePointer()
    {
        CpuState state = NewCallWithMagic(41);
        state.Ebx = 0x45414552;
        return state;
    }

    // --- the RPC channel (MESSAGE, command 30) --------------------------------

    private const uint MessageCommand = 30;
    private const uint MessageOpen = 0;
    private const uint MessageSendSize = 1;
    private const uint MessageSendPayload = 2;
    private const uint MessageRecvSize = 3;
    private const uint MessageRecvPayload = 4;
    private const uint MessageRecvStatus = 5;
    private const uint MessageClose = 6;

    private const uint StatusSuccess = 0x0001;
    private const uint StatusDoRecv = 0x0002;
    private const uint StatusClosed = 0x0004;

    private const uint ProtocolRpci = 0x49435052;
    private const uint ProtocolTclo = 0x4F4C4354;
    private const uint CookieRequestBit = 0x80000000;

    private static CpuState NewMessageCall(uint subCommand)
    {
        var state = new CpuState();
        state.Eax = VmwareBackdoor.Magic;
        state.Ecx = (subCommand << 16) | MessageCommand;
        return state;
    }

    [Fact]
    public void AnRpciRoundTripCarriesToolsSetVersionAndReceivesTheDefaultReply()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;

        // OPEN, asking for cookies.
        CpuState open = NewMessageCall(MessageOpen);
        open.Ebx = ProtocolRpci | CookieRequestBit;
        Assert.True(device.TryHandleInput(open, VmwareBackdoor.Port));
        Assert.Equal(StatusSuccess, open.Ecx >> 16);

        uint channelId = open.Edx >> 16;
        uint cookieA = open.Esi;
        uint cookieB = open.Edi;
        Assert.NotEqual(0u, cookieA);
        Assert.NotEqual(0u, cookieB);

        const string request = "tools.set.version 123";
        byte[] requestBytes = System.Text.Encoding.ASCII.GetBytes(request);

        // SENDSIZE: announce the length before a single byte travels.
        CpuState sendSize = NewMessageCall(MessageSendSize);
        sendSize.Ebx = (uint)requestBytes.Length;
        sendSize.Edx = channelId << 16;
        sendSize.Esi = cookieA;
        sendSize.Edi = cookieB;
        Assert.True(device.TryHandleInput(sendSize, VmwareBackdoor.Port));
        Assert.Equal(StatusSuccess, sendSize.Ecx >> 16);

        // SENDPAYLOAD, four bytes at a time, exactly as a guest's message.c would.
        for (int offset = 0; offset < requestBytes.Length; offset += 4)
        {
            uint dword = 0;
            for (int index = 0; index < 4 && offset + index < requestBytes.Length; index++)
            {
                dword |= (uint)requestBytes[offset + index] << (index * 8);
            }

            CpuState sendPayload = NewMessageCall(MessageSendPayload);
            sendPayload.Ebx = dword;
            sendPayload.Edx = channelId << 16;
            sendPayload.Esi = cookieA;
            sendPayload.Edi = cookieB;
            Assert.True(device.TryHandleInput(sendPayload, VmwareBackdoor.Port));
            Assert.Equal(StatusSuccess, sendPayload.Ecx >> 16);
        }

        // RECVSIZE: the default host answers "1 ", two bytes, with more to receive.
        CpuState recvSize = NewMessageCall(MessageRecvSize);
        recvSize.Edx = channelId << 16;
        recvSize.Esi = cookieA;
        recvSize.Edi = cookieB;
        Assert.True(device.TryHandleInput(recvSize, VmwareBackdoor.Port));
        Assert.Equal(2u, recvSize.Ebx);
        Assert.Equal(1u, recvSize.Edx >> 16);
        Assert.Equal(StatusSuccess | StatusDoRecv, recvSize.Ecx >> 16);

        // RECVPAYLOAD: "1 " packed little-endian into the low two bytes.
        CpuState recvPayload = NewMessageCall(MessageRecvPayload);
        recvPayload.Ebx = StatusSuccess;
        recvPayload.Edx = channelId << 16;
        recvPayload.Esi = cookieA;
        recvPayload.Edi = cookieB;
        Assert.True(device.TryHandleInput(recvPayload, VmwareBackdoor.Port));
        Assert.Equal(0x00002031u, recvPayload.Ebx);
        Assert.Equal(2u, recvPayload.Edx >> 16);
        Assert.Equal(StatusSuccess, recvPayload.Ecx >> 16);

        // RECVSTATUS acknowledges the reply.
        CpuState recvStatus = NewMessageCall(MessageRecvStatus);
        recvStatus.Ebx = StatusSuccess;
        recvStatus.Edx = channelId << 16;
        recvStatus.Esi = cookieA;
        recvStatus.Edi = cookieB;
        Assert.True(device.TryHandleInput(recvStatus, VmwareBackdoor.Port));
        Assert.Equal(StatusSuccess, recvStatus.Ecx >> 16);

        // CLOSE ends the channel.
        CpuState close = NewMessageCall(MessageClose);
        close.Edx = channelId << 16;
        close.Esi = cookieA;
        close.Edi = cookieB;
        Assert.True(device.TryHandleInput(close, VmwareBackdoor.Port));
        Assert.Equal(StatusSuccess, close.Ecx >> 16);
    }

    [Fact]
    public void AWrongCookieIsRefusedWithoutTheSuccessBit()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;

        CpuState open = NewMessageCall(MessageOpen);
        open.Ebx = ProtocolRpci | CookieRequestBit;
        device.TryHandleInput(open, VmwareBackdoor.Port);
        uint channelId = open.Edx >> 16;
        uint cookieA = open.Esi;

        CpuState sendSize = NewMessageCall(MessageSendSize);
        sendSize.Ebx = 4;
        sendSize.Edx = channelId << 16;
        sendSize.Esi = cookieA + 1;   // wrong
        sendSize.Edi = open.Edi;

        Assert.True(device.TryHandleInput(sendSize, VmwareBackdoor.Port));
        Assert.Equal(0u, (sendSize.Ecx >> 16) & StatusSuccess);
    }

    [Fact]
    public void AClosedChannelIsRefusedAsClosedRatherThanAsSuccess()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;

        CpuState open = NewMessageCall(MessageOpen);
        open.Ebx = ProtocolRpci;
        device.TryHandleInput(open, VmwareBackdoor.Port);
        uint channelId = open.Edx >> 16;

        CpuState close = NewMessageCall(MessageClose);
        close.Edx = channelId << 16;
        device.TryHandleInput(close, VmwareBackdoor.Port);

        CpuState sendSizeAfterClose = NewMessageCall(MessageSendSize);
        sendSizeAfterClose.Ebx = 4;
        sendSizeAfterClose.Edx = channelId << 16;

        Assert.True(device.TryHandleInput(sendSizeAfterClose, VmwareBackdoor.Port));
        Assert.Equal(StatusClosed, sendSizeAfterClose.Ecx >> 16);
    }

    [Fact]
    public void ATcloPollWithNothingQueuedReportsSizeZero()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;

        CpuState open = NewMessageCall(MessageOpen);
        open.Ebx = ProtocolTclo;
        Assert.True(device.TryHandleInput(open, VmwareBackdoor.Port));
        uint channelId = open.Edx >> 16;

        // An empty message is the poll itself; there is nothing to send.
        CpuState sendSize = NewMessageCall(MessageSendSize);
        sendSize.Ebx = 0;
        sendSize.Edx = channelId << 16;
        Assert.True(device.TryHandleInput(sendSize, VmwareBackdoor.Port));

        CpuState recvSize = NewMessageCall(MessageRecvSize);
        recvSize.Edx = channelId << 16;
        Assert.True(device.TryHandleInput(recvSize, VmwareBackdoor.Port));
        Assert.Equal(0u, recvSize.Ebx);
    }

    // --- the clipboard (GETSELLENGTH/GETNEXTPIECE/SETSELLENGTH/SETNEXTPIECE) ---

    private const ushort CommandGetSelLength = 6;
    private const ushort CommandGetNextPiece = 7;
    private const ushort CommandSetSelLength = 8;
    private const ushort CommandSetNextPiece = 9;

    /// <summary>
    /// A host clipboard double that hands back a fixed string and records
    /// what the guest last set, so tests can assert on both directions
    /// without a real toolkit clipboard.
    /// </summary>
    private sealed class FakeHostClipboard : IHostClipboard
    {
        public string? Text { get; set; }
        public string? LastSetText { get; private set; }

        public string? GetText() => Text;

        public void SetText(string text)
        {
            LastSetText = text;
        }
    }

    [Fact]
    public void GetSelLengthAndGetNextPieceReadTheHostSelectionUtf8LittleEndian()
    {
        // "héllo" is 6 bytes in UTF-8 (h, C3, A9, l, l, o): the accented
        // letter takes two bytes, so this also proves the packing is byte-
        // exact rather than one dword per character.
        var clipboard = new FakeHostClipboard { Text = "héllo" };
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        device.Clipboard = clipboard;

        CpuState length = NewCallWithMagic(CommandGetSelLength);
        Assert.True(device.TryHandleInput(length, VmwareBackdoor.Port));
        Assert.Equal(6u, length.Eax);

        CpuState firstPiece = NewCallWithMagic(CommandGetNextPiece);
        Assert.True(device.TryHandleInput(firstPiece, VmwareBackdoor.Port));
        Assert.Equal(0x6CA9C368u, firstPiece.Eax);   // 'h', C3, A9, 'l' packed little-endian

        CpuState secondPiece = NewCallWithMagic(CommandGetNextPiece);
        Assert.True(device.TryHandleInput(secondPiece, VmwareBackdoor.Port));
        Assert.Equal(0x00006F6Cu, secondPiece.Eax);   // 'l', 'o', then nothing left

        CpuState pastTheEnd = NewCallWithMagic(CommandGetNextPiece);
        Assert.True(device.TryHandleInput(pastTheEnd, VmwareBackdoor.Port));
        Assert.Equal(0u, pastTheEnd.Eax);
    }

    [Fact]
    public void AMissingHostClipboardReportsASelectionLengthOfZero()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        device.Clipboard = null;

        CpuState length = NewCallWithMagic(CommandGetSelLength);
        Assert.True(device.TryHandleInput(length, VmwareBackdoor.Port));
        Assert.Equal(0u, length.Eax);

        CpuState piece = NewCallWithMagic(CommandGetNextPiece);
        Assert.True(device.TryHandleInput(piece, VmwareBackdoor.Port));
        Assert.Equal(0u, piece.Eax);
    }

    [Fact]
    public void SetSelLengthAndSetNextPieceHandleTheGuestSelectionToTheHostClipboard()
    {
        // Five ASCII bytes, "ABCDE": a length that is not a multiple of four,
        // so the second SETNEXTPIECE dword only contributes its low byte.
        var clipboard = new FakeHostClipboard();
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        device.Clipboard = clipboard;

        CpuState declare = NewCallWithMagic(CommandSetSelLength);
        declare.Ebx = 5;
        Assert.True(device.TryHandleInput(declare, VmwareBackdoor.Port));
        Assert.Null(clipboard.LastSetText);   // nothing handed over until the transfer completes

        CpuState firstPiece = NewCallWithMagic(CommandSetNextPiece);
        firstPiece.Ebx = 0x44434241;   // 'A', 'B', 'C', 'D' packed little-endian
        Assert.True(device.TryHandleInput(firstPiece, VmwareBackdoor.Port));
        Assert.Null(clipboard.LastSetText);   // one byte still to come

        CpuState secondPiece = NewCallWithMagic(CommandSetNextPiece);
        secondPiece.Ebx = 0x00000045;   // 'E' in the low byte; the rest is padding past the declared length
        Assert.True(device.TryHandleInput(secondPiece, VmwareBackdoor.Port));
        Assert.Equal("ABCDE", clipboard.LastSetText);
    }

    [Fact]
    public void AZeroLengthGuestSelectionSetsNothingOnTheHostClipboard()
    {
        var clipboard = new FakeHostClipboard { Text = "unchanged" };
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        device.Clipboard = clipboard;

        CpuState declare = NewCallWithMagic(CommandSetSelLength);
        declare.Ebx = 0;
        Assert.True(device.TryHandleInput(declare, VmwareBackdoor.Port));

        Assert.Null(clipboard.LastSetText);
    }

    [Fact]
    public void AnOversizedSetSelLengthIsIgnoredAndItsPiecesAreDiscarded()
    {
        var clipboard = new FakeHostClipboard();
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        device.Clipboard = clipboard;

        CpuState declare = NewCallWithMagic(CommandSetSelLength);
        declare.Ebx = 64 * 1024 + 1;   // one byte past Aurora86's cap
        Assert.True(device.TryHandleInput(declare, VmwareBackdoor.Port));

        CpuState piece = NewCallWithMagic(CommandSetNextPiece);
        piece.Ebx = 0x44434241;
        Assert.True(device.TryHandleInput(piece, VmwareBackdoor.Port));

        Assert.Null(clipboard.LastSetText);
    }

    [Fact]
    public void AHostSelectionOverTheCapIsTruncatedWithoutSplittingACharacter()
    {
        // 65435 ASCII bytes followed by "é" (two UTF-8 bytes) is 65437 bytes
        // total, one past the 65436-byte cap; cutting at the cap would land
        // inside "é" (its second, continuation byte), so the whole character
        // must be dropped rather than have only its first byte kept.
        string oversized = new string('a', 65435) + "é";
        var clipboard = new FakeHostClipboard { Text = oversized };
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        device.Clipboard = clipboard;

        CpuState length = NewCallWithMagic(CommandGetSelLength);
        Assert.True(device.TryHandleInput(length, VmwareBackdoor.Port));
        Assert.Equal(65435u, length.Eax);
    }

    [Fact]
    public void GetSelLengthSnapshotsTheHostTextSoALaterHostChangeCannotTearTheRead()
    {
        var clipboard = new FakeHostClipboard { Text = "first" };
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        device.Clipboard = clipboard;

        CpuState length = NewCallWithMagic(CommandGetSelLength);
        Assert.True(device.TryHandleInput(length, VmwareBackdoor.Port));
        Assert.Equal(5u, length.Eax);

        // The host clipboard changes after GETSELLENGTH but before every
        // piece has been read.
        clipboard.Text = "a completely different and much longer replacement";

        CpuState firstPiece = NewCallWithMagic(CommandGetNextPiece);
        Assert.True(device.TryHandleInput(firstPiece, VmwareBackdoor.Port));
        Assert.Equal(0x73726966u, firstPiece.Eax);   // 'f', 'i', 'r', 's' packed little-endian

        CpuState secondPiece = NewCallWithMagic(CommandGetNextPiece);
        Assert.True(device.TryHandleInput(secondPiece, VmwareBackdoor.Port));
        Assert.Equal(0x00000074u, secondPiece.Eax);   // 't', the fifth and last byte of "first"
    }

    // --- snapshots -------------------------------------------------------------

    [Fact]
    public void CaptureAndRestoreRoundTripTheEnabledAndPointerFlags()
    {
        VmwareBackdoor device = NewDevice();
        device.Enabled = true;
        device.TryHandleInput(EnableAbsolutePointer(), VmwareBackdoor.Port);
        CpuState requestAbsolute = NewCallWithMagic(41);
        requestAbsolute.Ebx = 0x53424152;
        device.TryHandleInput(requestAbsolute, VmwareBackdoor.Port);

        byte[] snapshot = device.Capture();
        Assert.Equal(VmwareBackdoor.SnapshotLength, snapshot.Length);
        VmwareBackdoor.ValidateSnapshot(snapshot);

        var restored = NewDevice();
        restored.Restore(snapshot);

        Assert.True(restored.Enabled);
        Assert.True(restored.PointerEnabled);
        Assert.True(restored.PointerAbsolute);
    }

    [Fact]
    public void ValidateSnapshotRejectsTheWrongLength()
    {
        Assert.Throws<System.IO.InvalidDataException>(() => VmwareBackdoor.ValidateSnapshot(new byte[1]));
    }

    // --- through the real CPU ---------------------------------------------------

    /// <summary>
    /// The backdoor's whole reason to exist is answering from a ring the I/O
    /// permission check would otherwise refuse. A protected-mode program at
    /// CPL 3, IOPL 0, with a task state segment too short to carry an I/O
    /// bitmap, executes <c>IN EAX, DX</c> on port 5658h with the magic in EAX
    /// and must not fault -- and must get the magic back in EBX -- while the
    /// same program with the device disabled must take a #GP, because then
    /// there is nothing to claim the access before the permission check runs.
    /// </summary>
    private const uint GdtBase = 0x00010000;
    private const uint IdtBase = 0x00011000;
    private const uint TssBase = 0x00012000;
    private const uint CodeBase = 0x00005000;
    private const uint KernelStackTop = 0x00009000;
    private const uint GpHandlerAddress = 0x00006000;

    private static void WriteDescriptor(Machine machine, int index, uint baseAddress, uint limit, byte access, byte granularity)
    {
        uint address = GdtBase + (uint)(index * 8);
        machine.Memory.WriteWord(address, (ushort)(limit & 0xFFFF));
        machine.Memory.WriteWord(address + 2, (ushort)(baseAddress & 0xFFFF));
        machine.Memory.WriteByte(address + 4, (byte)((baseAddress >> 16) & 0xFF));
        machine.Memory.WriteByte(address + 5, access);
        machine.Memory.WriteByte(address + 6, (byte)((granularity & 0xF0) | ((limit >> 16) & 0x0F)));
        machine.Memory.WriteByte(address + 7, (byte)((baseAddress >> 24) & 0xFF));
    }

    private static void WriteGate(Machine machine, int vector, ushort selector, uint offset, byte dpl)
    {
        uint address = IdtBase + (uint)(vector * 8);
        machine.Memory.WriteWord(address, (ushort)(offset & 0xFFFF));
        machine.Memory.WriteWord(address + 2, selector);
        machine.Memory.WriteByte(address + 4, 0);
        machine.Memory.WriteByte(address + 5, (byte)(0x80 | ((dpl & 3) << 5) | 0x0E));   // 32-bit interrupt gate
        machine.Memory.WriteWord(address + 6, (ushort)(offset >> 16));
    }

    /// <summary>
    /// Builds a ring 3 program at CPL 3 with IOPL 0 and a task state segment
    /// eleven bytes long -- enough for the ring 0 stack pair the CPU needs to
    /// deliver a fault, too short to reach the I/O permission bitmap offset
    /// at 0x66. That is "no TSS bitmap" in the words the CPU actually checks
    /// (Cpu.Ops.cs' IoPermitted): every ordinary port faults from this ring.
    /// </summary>
    private static Machine BuildRingThreeMachineWithNoIoBitmap()
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        WriteDescriptor(machine, 1, 0, 0xFFFFF, 0x9A, 0xC0);   // ring 0 code
        WriteDescriptor(machine, 2, 0, 0xFFFFF, 0x92, 0xC0);   // ring 0 data
        WriteDescriptor(machine, 3, TssBase, 0x0B, 0x89, 0x00);   // TSS, too short for a bitmap
        WriteDescriptor(machine, 4, 0, 0xFFFFF, 0xFA, 0xC0);   // ring 3 code
        WriteDescriptor(machine, 5, 0, 0xFFFFF, 0xF2, 0xC0);   // ring 3 data

        machine.Memory.WriteDword(TssBase + 4, KernelStackTop);   // ESP0
        machine.Memory.WriteDword(TssBase + 8, 0x10);             // SS0

        WriteGate(machine, 13, 0x08, GpHandlerAddress, dpl: 0);   // #GP

        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x2F;
        machine.Cpu.State.IdtBase = IdtBase;
        machine.Cpu.State.IdtLimit = 0x7FF;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.State.TaskRegister = 0x18;

        machine.Cpu.SetSegment(SegReg.Cs, 0x23);
        machine.Cpu.SetSegment(SegReg.Ss, 0x2B);
        machine.Cpu.State.Cpl = 3;
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.Flags.SetValue(0x0002);   // IOPL 0, IF clear

        // MOV EAX, 564D5868h ; MOV ECX, 10 ; MOV EDX, 5658h ; IN EAX, DX
        machine.Memory.WriteByte(CodeBase, 0xB8);
        machine.Memory.WriteDword(CodeBase + 1, VmwareBackdoor.Magic);
        machine.Memory.WriteByte(CodeBase + 5, 0xB9);
        machine.Memory.WriteDword(CodeBase + 6, 10);
        machine.Memory.WriteByte(CodeBase + 10, 0xBA);
        machine.Memory.WriteDword(CodeBase + 11, VmwareBackdoor.Port);
        machine.Memory.WriteByte(CodeBase + 15, 0xED);

        machine.Cpu.State.Eip = CodeBase;
        machine.Cpu.State.Halted = false;

        return machine;
    }

    [Fact]
    public void AnEnabledBackdoorAnswersInEaxDxFromRingThreeWithoutFaulting()
    {
        using Machine machine = BuildRingThreeMachineWithNoIoBitmap();
        var device = NewDevice();
        device.Enabled = true;
        machine.Cpu.Backdoor = device;

        machine.Cpu.Step();   // MOV EAX, magic
        machine.Cpu.Step();   // MOV ECX, 10 (GETVERSION)
        machine.Cpu.Step();   // MOV EDX, 5658h
        machine.Cpu.Step();   // IN EAX, DX

        Assert.Equal(3, (int)machine.Cpu.State.Cpl);
        Assert.Equal(VmwareBackdoor.Magic, machine.Cpu.State.Ebx);
        Assert.Equal(CodeBase + 16, machine.Cpu.State.Eip);
    }

    [Fact]
    public void ADisabledBackdoorLeavesTheOrdinaryPermissionCheckToFault()
    {
        using Machine machine = BuildRingThreeMachineWithNoIoBitmap();
        var device = NewDevice();
        device.Enabled = false;
        machine.Cpu.Backdoor = device;

        machine.Cpu.Step();   // MOV EAX, magic
        machine.Cpu.Step();   // MOV ECX, 10
        machine.Cpu.Step();   // MOV EDX, 5658h
        machine.Cpu.Step();   // IN EAX, DX -- no TSS bitmap, IOPL 0, ring 3: must fault

        Assert.Equal(GpHandlerAddress, machine.Cpu.State.Eip);
        Assert.Equal(0, (int)machine.Cpu.State.Cpl);
        Assert.Equal((ushort)0x10, machine.Cpu.State.Ss.Selector);
    }

    /// <summary>
    /// Only the 32-bit IN EAX,DX is a backdoor call. The same access narrowed
    /// to IN AX,DX by an operand-size prefix is an ordinary port read, so
    /// from ring 3 with no I/O bitmap it faults even with the magic in EAX
    /// and the port enabled.
    /// </summary>
    [Fact]
    public void ASixteenBitInWithTheMagicStillFaultsFromRingThree()
    {
        using Machine machine = BuildRingThreeMachineWithNoIoBitmap();
        var device = NewDevice();
        device.Enabled = true;
        machine.Cpu.Backdoor = device;
        machine.Memory.WriteByte(CodeBase + 15, 0x66);   // operand-size prefix
        machine.Memory.WriteByte(CodeBase + 16, 0xED);   // IN AX, DX

        machine.Cpu.Step();
        machine.Cpu.Step();
        machine.Cpu.Step();
        machine.Cpu.Step();

        Assert.Equal(GpHandlerAddress, machine.Cpu.State.Eip);
    }

    /// <summary>
    /// The high-bandwidth port 5659h is not implemented, so it keeps the
    /// architectural check: the magic does not open it from ring 3.
    /// </summary>
    [Fact]
    public void TheUnimplementedHighBandwidthPortStillFaultsFromRingThree()
    {
        const ushort HighBandwidthPort = 0x5659;
        using Machine machine = BuildRingThreeMachineWithNoIoBitmap();
        var device = NewDevice();
        device.Enabled = true;
        machine.Cpu.Backdoor = device;
        machine.Memory.WriteDword(CodeBase + 11, HighBandwidthPort);

        machine.Cpu.Step();
        machine.Cpu.Step();
        machine.Cpu.Step();
        machine.Cpu.Step();

        Assert.Equal(GpHandlerAddress, machine.Cpu.State.Eip);
    }
}
