using Aurora86.Core.Devices.Usb;

namespace Aurora86.Tests;

public class UsbHidTests
{
    private static void Setup(UsbHidDevice d, byte type, byte request, ushort value = 0, ushort length = 0)
    {
        byte[] setup = [type,request,(byte)value,(byte)(value >> 8),0,0,(byte)length,(byte)(length >> 8)];
        Assert.Equal(UsbHandshake.Ack, d.Transfer(0x2D, 0, false, setup).Handshake);
    }
    private static void StatusIn(UsbHidDevice d) => Assert.Equal(UsbHandshake.Ack, d.Transfer(0x69, 0, true, []).Handshake);
    private static void Configure(UsbHidDevice d) { Setup(d, 0, 9, 1); StatusIn(d); }
    private bool _pollToggle;
    private byte[] Poll(UsbHidDevice d)
    {
        byte[] bytes = new byte[8]; var result = d.Transfer(0x69, 1, _pollToggle, bytes);
        _pollToggle = !_pollToggle;
        Assert.Equal(UsbHandshake.Ack, result.Handshake);
        return bytes[..result.Length];
    }

    [Fact]
    public void EnumerationSplitsDescriptorsAndDefersTheNewAddress()
    {
        var d = new UsbHidDevice(true);
        Setup(d, 0x80, 6, 0x100, 18);
        var data = new List<byte>(); bool toggle = true;
        for (int i = 0; i < 3; i++)
        {
            byte[] packet = new byte[8]; var result = d.Transfer(0x69, 0, toggle, packet);
            Assert.Equal(UsbHandshake.Ack, result.Handshake);
            data.AddRange(packet[..result.Length]); toggle = !toggle;
        }
        Assert.Equal(18, data.Count); Assert.Equal(8, data[7]);
        Assert.Equal(UsbHandshake.Ack, d.Transfer(0xE1, 0, true, []).Handshake);
        Setup(d, 0, 5, 7); Assert.Equal(0, d.Address);
        StatusIn(d); Assert.Equal(7, d.Address);
        Configure(d); Assert.Equal(1, d.Configuration);
    }

    [Fact]
    public void KeyboardReportsModifiersSixKeysAndRolloverThenReleases()
    {
        var d = new UsbHidDevice(true); Configure(d);
        d.SetKey(0xE1, true); d.SetKey(4, true);
        Assert.Equal(new byte[] {2,0,4,0,0,0,0,0}, Poll(d));
        Assert.Equal(UsbHandshake.Nak, d.Transfer(0x69, 1, _pollToggle, new byte[8]).Handshake);
        for (byte i = 5; i <= 10; i++) d.SetKey(i, true);
        Assert.Equal(new byte[] {2,0,1,1,1,1,1,1}, Poll(d));
        for (byte i = 4; i <= 10; i++) d.SetKey(i, false);
        d.SetKey(0xE1, false);
        Assert.Equal(new byte[8], Poll(d));
    }

    [Fact]
    public void KeyboardLedOutputCommitsAtStatusAndIdleResendsTheReport()
    {
        var d = new UsbHidDevice(true); Configure(d); Poll(d);
        Setup(d, 0x21, 9, 0x200, 1);
        Assert.Equal(UsbHandshake.Ack, d.Transfer(0xE1, 0, true, new byte[] { 3 }).Handshake);
        Assert.Equal(0, d.Leds); StatusIn(d); Assert.Equal(3, d.Leds);
        Setup(d, 0x21, 10, 0x0200); StatusIn(d);
        d.Advance(7999); Assert.Equal(UsbHandshake.Nak, d.Transfer(0x69, 1, _pollToggle, new byte[8]).Handshake);
        d.Advance(1); Assert.Equal(new byte[8], Poll(d));
    }

    [Fact]
    public void MouseClampsMotionAndBootProtocolOmitsTheWheel()
    {
        var d = new UsbHidDevice(false); Configure(d);
        d.Move(200, -10, 1); d.SetButtons(5);
        Assert.Equal(new byte[] {5,127,246,1}, Poll(d));
        Assert.Equal(new byte[] {5,73,0,0}, Poll(d));
        Setup(d, 0x21, 11); StatusIn(d);
        d.Move(1, 2, 3);
        Assert.Equal(new byte[] {5,1,2}, Poll(d));
    }

    [Fact]
    public void SnapshotRestoresAnInProgressControlTransferAndHeldKeys()
    {
        var d = new UsbHidDevice(true); Configure(d); d.SetKey(4, true);
        Setup(d, 0x80, 6, 0x200, 34);
        byte[] bytes = new byte[8]; d.Transfer(0x69, 0, true, bytes);
        byte[] snapshot = d.Capture();
        d.Transfer(0x69, 0, false, bytes); byte[] expected = bytes.ToArray();
        d.Reset(); d.Restore(snapshot);
        Assert.Equal(UsbHandshake.Ack, d.Transfer(0x69, 0, false, bytes).Handshake);
        Assert.Equal(expected, bytes);
        Assert.Equal(4, Poll(d)[2]);
    }
    [Fact]
    public void RetryingAnInterruptPacketDoesNotConsumeMouseMovementTwice()
    {
        var d = new UsbHidDevice(false); Configure(d); d.Move(200, 0);
        byte[] first = new byte[8], retry = new byte[8];
        Assert.Equal(UsbHandshake.Ack, d.Transfer(0x69, 1, false, first).Handshake);
        Assert.Equal(UsbHandshake.Ack, d.Transfer(0x69, 1, false, retry).Handshake);
        Assert.Equal(first, retry);
        Assert.Equal(UsbHandshake.Ack, d.Transfer(0x69, 1, true, retry).Handshake);
        Assert.Equal(73, retry[1]);
    }

    [Fact]
    public void FrontendInputUsesUsbAfterConfigurationWithoutAlsoFeedingLegacyPorts()
    {
        using var m = new Aurora86.Core.Machine(); m.EnableUsb(); m.RunInstructions(3000);
        Configure(m.UsbKeyboard!); Configure(m.UsbMouse!);
        m.Keyboard.PushScanCode(0x1E); // physical A
        Assert.Equal(4, Poll(m.UsbKeyboard!)[2]);
        Assert.Equal(0, m.Keyboard.ReadByte(0x64) & 1);
        m.Keyboard.PushScanCode(0xE0); m.Keyboard.PushScanCode(0x4D); // right arrow
        Assert.Equal(new byte[] {0,0,4,79,0,0,0,0}, Poll(m.UsbKeyboard!));
        m.Keyboard.PushScanCode(0x9E);
        Assert.Equal(79, Poll(m.UsbKeyboard!)[2]);
        m.Mouse.Move(5, -2); m.Mouse.SetButton(Aurora86.Core.Devices.Input.MouseButtons.Left, true);
        byte[] mouse = new byte[8];
        Assert.Equal(UsbHandshake.Ack, m.UsbMouse!.Transfer(0x69, 1, false, mouse).Handshake);
        Assert.Equal(new byte[] {1,5,254,0}, mouse[..4]);
        Assert.False(m.Mouse.HasSerialMotion); Assert.False(m.Mouse.HasPs2Motion);
    }

    [Fact]
    public void SnapshotKeepsAnExtendedScancodePrefix()
    {
        var d = new UsbHidDevice(true); Configure(d); d.PushScanCode(0xE0);
        byte[] saved = d.Capture(); d.Reset(); d.Restore(saved);
        d.PushScanCode(0x1D);
        Assert.Equal(0x10, Poll(d)[0]); // right control, not left
    }

}
