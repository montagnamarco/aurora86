using Aurora386.Core;
using Aurora386.Core.Devices.IsaPnp;
using Aurora386.Core.IO;

namespace Aurora386.Tests;

/// <summary>
/// Tests for the ISA Plug and Play bus, played as the configuration
/// software plays it: key, isolation, select numbers, resource data,
/// configuration registers -- through the three ports and nothing else.
/// </summary>
public class IsaPnpTests
{
    private const ushort Address = IsaPnpBus.AddressPort;
    private const ushort WriteData = IsaPnpBus.WriteDataPort;
    private const ushort ReadData = IsaPnpBus.DefaultReadDataPort;

    private static readonly byte[] Key =
    [
        0x6A, 0xB5, 0xDA, 0xED, 0xF6, 0xFB, 0x7D, 0xBE, 0xDF, 0x6F, 0x37, 0x1B, 0x0D, 0x86, 0xC3, 0x61,
        0xB0, 0x58, 0x2C, 0x16, 0x8B, 0x45, 0xA2, 0xD1, 0xE8, 0x74, 0x3A, 0x9D, 0xCE, 0xE7, 0x73, 0x39,
    ];

    // --- the software side ------------------------------------------------------------

    private static void SendKey(IoBus io)
    {
        io.WriteByte(Address, 0);
        io.WriteByte(Address, 0);
        foreach (byte b in Key) io.WriteByte(Address, b);
    }

    private static void Write(IoBus io, byte register, byte value)
    {
        io.WriteByte(Address, register);
        io.WriteByte(WriteData, value);
    }

    private static byte Read(IoBus io, byte register, ushort readPort = ReadData)
    {
        io.WriteByte(Address, register);
        return io.ReadByte(readPort);
    }

    /// <summary>Runs one round of isolation: the identifier of the card left standing, or null when nobody answered.</summary>
    private static byte[]? Isolate(IoBus io)
    {
        Write(io, 0x00, ReadData >> 2);
        Write(io, 0x03, 0);                       // Wake[0]
        io.WriteByte(Address, 0x01);
        byte[] id = new byte[9];
        bool anything = false;
        for (int bit = 0; bit < 72; bit++)
        {
            byte first = io.ReadByte(ReadData);
            byte second = io.ReadByte(ReadData);
            if (first == 0x55 && second == 0xAA)
            {
                id[bit >> 3] |= (byte)(1 << (bit & 7));
                anything = true;
            }
        }
        return anything ? id : null;
    }

    private static (IoBus Io, IsaPnpBus Bus, IsaPnpCard A, IsaPnpCard B, List<(ushort Base, int Irq, bool Active)> Log) TwoCards()
    {
        var log = new List<(ushort, int, bool)>();
        var deviceA = new IsaPnpLogicalDevice("TSTA001", "PNPB003", r => { r.Io(0x220, 0x280, 0x20, 16); r.Irq(5, 7); });
        deviceA.SetDefaults(true, [0x220], [5], [1]);
        deviceA.Configured = d => log.Add((d.IoBase(0), d.Irq(0), d.Active));
        var a = new IsaPnpCard("TST0001", 0x11111111, "Card A", [deviceA]);

        var deviceB = new IsaPnpLogicalDevice("TSTB001", null, r => { r.Io(0x300, 0x360, 0x20, 32); r.Irq(3, 4); });
        deviceB.SetDefaults(true, [0x300], [3], []);
        var b = new IsaPnpCard("TST0002", 0x22222222, "Card B", [deviceB]);

        var io = new IoBus();
        var bus = new IsaPnpBus();
        bus.Add(a);
        bus.Add(b);
        bus.Register(io);
        return (io, bus, a, b, log);
    }

    // --- identifier --------------------------------------------------------------------

    [Fact]
    public void TheSerialIdentifierCarriesTheVendorIdTheSerialNumberAndTheChecksum()
    {
        var card = new IsaPnpCard("CTL0044", 0x12345678, "x", []);
        Assert.Equal(new byte[] { 0x0E, 0x8C, 0x00, 0x44 }, card.SerialIdentifier[..4]);
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, card.SerialIdentifier[4..8]);

        // The checksum recomputed the way the specification's C sample does.
        byte checksum = 0x6A;
        for (int i = 0; i < 8; i++)
            for (int bit = 0; bit < 8; bit++)
            {
                int b = (card.SerialIdentifier[i] >> bit) & 1;
                checksum = (byte)(((((checksum ^ (checksum >> 1)) & 1) ^ b) << 7) | (checksum >> 1));
            }
        Assert.Equal(checksum, card.SerialIdentifier[8]);
    }

    // --- key and states ----------------------------------------------------------------------

    [Fact]
    public void CardsIgnoreEverythingUntilTheKeyIsSent()
    {
        var (io, bus, a, b, _) = TwoCards();
        Write(io, 0x03, 0);
        Assert.Equal(0xFF, Read(io, 0x05));
        Assert.Equal(IsaPnpCard.CardState.WaitForKey, a.State);

        SendKey(io);
        Assert.Equal(IsaPnpCard.CardState.Sleep, a.State);
        Assert.Equal(IsaPnpCard.CardState.Sleep, b.State);
    }

    [Fact]
    public void AWrongByteInTheKeyStartsItOver()
    {
        var (io, _, a, _, _) = TwoCards();
        io.WriteByte(Address, 0);
        io.WriteByte(Address, 0);
        for (int i = 0; i < 10; i++) io.WriteByte(Address, Key[i]);
        io.WriteByte(Address, 0x12);
        for (int i = 10; i < 32; i++) io.WriteByte(Address, Key[i]);
        Assert.Equal(IsaPnpCard.CardState.WaitForKey, a.State);

        SendKey(io);
        Assert.Equal(IsaPnpCard.CardState.Sleep, a.State);
    }

    // --- isolation ---------------------------------------------------------------------------

    [Fact]
    public void IsolationFindsEachCardOnceAndThenNobody()
    {
        var (io, bus, a, b, _) = TwoCards();
        SendKey(io);

        byte[]? first = Isolate(io);
        Assert.NotNull(first);
        Write(io, 0x06, 1);
        byte[]? second = Isolate(io);
        Assert.NotNull(second);
        Write(io, 0x06, 2);
        Assert.Null(Isolate(io));

        var found = new HashSet<string> { Convert.ToHexString(first), Convert.ToHexString(second) };
        Assert.Contains(Convert.ToHexString(a.SerialIdentifier), found);
        Assert.Contains(Convert.ToHexString(b.SerialIdentifier), found);
        Assert.Equal(2, bus.AssignedCount);
        Assert.NotEqual(a.Csn, b.Csn);
    }

    [Fact]
    public void TheCardWithAOneWhereTheOtherHasAZeroWinsTheFirstRound()
    {
        var (io, _, a, b, _) = TwoCards();
        SendKey(io);
        byte[] first = Isolate(io)!;

        // Find the first bit where the two differ; the card with the 1 there
        // is the one that must have stayed on the bus.
        IsaPnpCard expected = a;
        for (int bit = 0; bit < 72; bit++)
        {
            int ba = (a.SerialIdentifier[bit >> 3] >> (bit & 7)) & 1;
            int bb = (b.SerialIdentifier[bit >> 3] >> (bit & 7)) & 1;
            if (ba != bb) { expected = ba == 1 ? a : b; break; }
        }
        Assert.Equal(expected.SerialIdentifier, first);
    }

    // --- resource data -----------------------------------------------------------------------

    [Fact]
    public void ResourceDataBeginsWithTheIdentifierAndEndsWithACheckedEndTag()
    {
        var (io, _, a, b, _) = TwoCards();
        SendKey(io);
        Isolate(io); Write(io, 0x06, 1);
        Isolate(io); Write(io, 0x06, 2);

        Write(io, 0x03, 1);                       // Wake[1]
        IsaPnpCard card = a.Csn == 1 ? a : b;
        Assert.Equal(1, Read(io, 0x05) & 1);      // ready

        byte[] header = new byte[9];
        for (int i = 0; i < 9; i++) header[i] = Read(io, 0x04);
        Assert.Equal(card.SerialIdentifier, header);

        var data = new List<byte>();
        while (true)
        {
            byte tag = Read(io, 0x04);
            data.Add(tag);
            int length;
            if ((tag & 0x80) != 0)
            {
                byte lo = Read(io, 0x04), hi = Read(io, 0x04);
                data.Add(lo); data.Add(hi);
                length = lo | (hi << 8);
            }
            else length = tag & 7;
            for (int i = 0; i < length; i++) data.Add(Read(io, 0x04));
            if ((tag & 0x78) == 0x78) break;      // end tag
        }
        Assert.Equal(card.ResourceData, data.ToArray());
        Assert.Equal(0, data.Sum(x => x) & 0xFF);
        Assert.Equal(0x0A, data[0]);              // version first
        Assert.Contains((byte)0x15, data);        // a logical device
        Assert.Equal(0xFF, Read(io, 0x04));       // and nothing after the end
    }

    // --- configuration -------------------------------------------------------------------------

    [Fact]
    public void ActivatingADeviceWithNewRegistersMovesTheChip()
    {
        var (io, _, a, b, log) = TwoCards();
        SendKey(io);
        Isolate(io); Write(io, 0x06, 1);
        Isolate(io); Write(io, 0x06, 2);

        Write(io, 0x03, a.Csn);
        Write(io, 0x07, 0);
        Write(io, 0x30, 0);                       // off while being moved
        Assert.Equal((0x220, 5, false), log[^1]);

        Write(io, 0x60, 0x02); Write(io, 0x61, 0x40);
        Write(io, 0x70, 7);
        Assert.Equal((0x220, 5, false), log[^1]); // nothing moves while it is off
        Write(io, 0x30, 1);
        Assert.Equal((0x240, 7, true), log[^1]);

        Assert.Equal(0x02, Read(io, 0x60));
        Assert.Equal(0x40, Read(io, 0x61));
        Assert.Equal(7, Read(io, 0x70));
        Assert.Equal(a.Csn, Read(io, 0x06));
    }

    [Fact]
    public void ResettingRestoresThePowerUpConfiguration()
    {
        var (io, _, a, _, log) = TwoCards();
        SendKey(io);
        Isolate(io); Write(io, 0x06, 1);
        Isolate(io); Write(io, 0x06, 2);
        Write(io, 0x03, a.Csn);
        Write(io, 0x60, 0x02); Write(io, 0x61, 0x40);
        Write(io, 0x30, 1);
        Assert.Equal((0x240, 5, true), log[^1]);

        Write(io, 0x02, 0x01);                    // reset all
        Assert.Equal((0x220, 5, true), log[^1]);
        Assert.Equal(IsaPnpCard.CardState.WaitForKey, a.State);
        Assert.Equal(1, a.Csn);                   // a reset keeps the number

        SendKey(io);
        Write(io, 0x02, 0x04);                    // reset select numbers
        Assert.Equal(0, a.Csn);
    }

    [Fact]
    public void TheReadDataPortCanBeMoved()
    {
        var (io, bus, _, _, _) = TwoCards();
        SendKey(io);
        Write(io, 0x00, 0x20B >> 2);
        Assert.Equal(0x20B, bus.ReadDataPort);
        Write(io, 0x03, 0);
        io.WriteByte(Address, 0x01);
        Assert.Equal(0xFF, io.ReadByte(ReadData));   // nobody there any more
        Assert.Contains(io.ReadByte(0x20B), new byte[] { 0x55, 0xFF });
    }

    // --- the real machine ---------------------------------------------------------------------------

    private static IsaPnpCard FindAndNumber(Machine machine, string vendor)
    {
        SendKey(machine.Io);
        byte csn = 1;
        while (Isolate(machine.Io) is not null) Write(machine.Io, 0x06, csn++);
        return machine.IsaPlugAndPlay.Cards.Single(c =>
            c.SerialIdentifier[..4].SequenceEqual(IsaPnpCard.CompressedId(vendor)));
    }

    [Fact]
    public void TheSoundBlasterMovesToTheAddressItIsGiven()
    {
        using var machine = new Machine();
        machine.RunInstructions(100);
        IsaPnpCard sb = FindAndNumber(machine, "CTL0044");
        Assert.Equal(2, machine.IsaPlugAndPlay.AssignedCount);

        Write(machine.Io, 0x03, sb.Csn);
        Write(machine.Io, 0x07, 0);
        Write(machine.Io, 0x60, 0x02); Write(machine.Io, 0x61, 0x40);
        Write(machine.Io, 0x70, 7);
        Write(machine.Io, 0x30, 1);

        Assert.Equal(0x240, machine.SoundBlaster.BasePort);
        Assert.Equal(7, machine.SoundBlaster.Irq);
        Assert.Equal(7, machine.SoundBlaster.Mixer.Irq);

        // The DSP answers a reset at the new address and is gone from the old.
        machine.Io.WriteByte(0x246, 1);
        machine.Io.WriteByte(0x246, 0);
        Assert.Equal(0xAA, machine.Io.ReadByte(0x24A));
        machine.Io.WriteByte(0x226, 1);
        machine.Io.WriteByte(0x226, 0);
        Assert.Equal(0xFF, machine.Io.ReadByte(0x22A));
    }

    [Fact]
    public void TheNetworkCardMovesToTheAddressItIsGiven()
    {
        using var machine = new Machine();
        machine.RunInstructions(100);
        IsaPnpCard ne = FindAndNumber(machine, "RTL8019");

        Write(machine.Io, 0x03, ne.Csn);
        Write(machine.Io, 0x07, 0);
        Write(machine.Io, 0x60, 0x03); Write(machine.Io, 0x61, 0x20);
        Write(machine.Io, 0x70, 10);
        Write(machine.Io, 0x30, 1);

        Assert.Equal(0x320, machine.Network.BasePort);
        Assert.Equal(10, machine.Network.IrqLine);
        Assert.NotEqual(0xFF, machine.Io.ReadByte(0x320));    // the command register answers
        Assert.Equal(0xFF, machine.Io.ReadByte(0x300));
    }

    [Fact]
    public void ThePnpBiosReportsHowManyCardsWereNumbered()
    {
        using var machine = new Machine();
        machine.RunInstructions(100);
        FindAndNumber(machine, "CTL0044");
        Assert.Equal(2, machine.IsaPlugAndPlay.AssignedCount);
    }
}
