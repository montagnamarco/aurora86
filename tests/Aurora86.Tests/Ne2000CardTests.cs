using Aurora86.Core;
using Aurora86.Core.Devices.Cards;
using Aurora86.Core.Devices.Network;

namespace Aurora86.Tests;

/// <summary>
/// The NE2000 as a card in the expansion-card registry: <see
/// cref="Ne2000Card"/>, its place in <see cref="HardwareConfiguration"/>, and
/// its snapshot section. See <c>plans/specs/device-registry.md</c> and
/// <see cref="CardRegistryTests"/> for the same shape applied to the Sound
/// Blaster.
/// </summary>
public class Ne2000CardTests
{
    [Fact]
    public void ADefaultMachineHasTheNe2000AtTodaysAddress()
    {
        // new Machine() must build exactly today's hardware: this is what
        // keeps every existing test and bench unchanged by the card moving
        // into the registry.
        using var machine = new Machine();

        Assert.NotNull(machine.Network);
        Assert.Equal(0x300, machine.Network!.BasePort);
        Assert.Equal(3, machine.Network.IrqLine);

        IExpansionCard card = Assert.Single(machine.Cards, c => c.Kind == "network");
        Assert.Equal("ne2000-isa", card.Model);
        Assert.Equal("NE2K", card.SnapshotId);
    }

    [Fact]
    public void AConfigurationWithNoNetworkCardHasNoneAndTheOldPortsAreEmpty()
    {
        using var machine = new Machine(4 * 1024 * 1024, new HardwareConfiguration());

        Assert.Empty(machine.Cards);
        Assert.Null(machine.Network);
        Assert.DoesNotContain(machine.IsaPlugAndPlay.Cards,
            c => c.LogicalDevices.Any(device => device.Id == "RTL8019"));

        // 300h-31Fh answer as an empty bus: 0xFF, exactly like any other
        // unclaimed port -- not a special "no card" value.
        Assert.Equal(0xFF, machine.Io.ReadByte(0x300));
        Assert.Equal(0xFF, machine.Io.ReadByte(0x31F));

        // Nothing crashes advancing devices with no network card fitted.
        machine.RunInstructions(500);
    }

    [Fact]
    public void ANe2000AtANonDefaultAddressAnswersThereAndRaisesItsConfiguredIrq()
    {
        var hardware = new HardwareConfiguration
        {
            Network = { new CardSettings { Card = "ne2000-isa", Port = 0x340, Irq = 10 } },
        };
        using var machine = new Machine(4 * 1024 * 1024, hardware);

        Assert.Equal(0x340, machine.Network!.BasePort);
        Assert.Equal(10, machine.Network.IrqLine);

        // The reset port answers, and so does the command register once
        // reset -- the old, standard address is empty.
        machine.Io.WriteByte(0x340 + 0x1F, 0);
        Assert.NotEqual(0xFF, machine.Io.ReadByte(0x340));
        Assert.Equal(0xFF, machine.Io.ReadByte(0x300));

        // The station address PROM: a remote DMA read of the first 32 bytes,
        // exactly as every packet driver probes for the card at start-up.
        // Doubled MAC bytes, and 0x57 0x57 ("WW") at the end, mark it an
        // NE2000 rather than the older NE1000.
        machine.Io.WriteByte(0x340, 0x21);         // stop, page 0
        machine.Io.WriteByte(0x340 + 0x08, 0x00);  // RSAR low
        machine.Io.WriteByte(0x340 + 0x09, 0x00);  // RSAR high
        machine.Io.WriteByte(0x340 + 0x0A, 32);    // RBCR low
        machine.Io.WriteByte(0x340 + 0x0B, 0);     // RBCR high
        machine.Io.WriteByte(0x340, 0x0A);         // remote read, start
        byte[] prom = new byte[32];
        for (int i = 0; i < 32; i++) prom[i] = machine.Io.ReadByte(0x340 + 0x10);
        Assert.Equal(0x57, prom[28]);
        Assert.Equal(0x57, prom[29]);

        // Unmasking the remote-DMA-complete status, then issuing the
        // abort/complete DMA command (0x22), raises the interrupt on
        // whatever line the card was configured with.
        machine.Io.WriteByte(0x340, 0x02);         // page 0, start
        machine.Io.WriteByte(0x340 + 0x0F, 0x40);  // unmask remote DMA complete
        machine.Io.WriteByte(0x21, 0xFB);           // unmask the cascade line only
        machine.Io.WriteByte(0xA1, 0xFB);           // unmask IRQ10 (line 2 on the slave) only
        machine.Io.WriteByte(0x340, 0x22);         // abort/complete remote DMA
        Assert.True(machine.Interrupts.HasPendingInterrupt);
    }

    [Fact]
    public void AnUnknownCardNameIsRefusedRatherThanSilentlyOmitted()
    {
        var hardware = new HardwareConfiguration
        {
            Network = { new CardSettings { Card = "rtl-fake", Port = 0x300 } },
        };

        Assert.Throws<ArgumentException>(() => new Machine(4 * 1024 * 1024, hardware));
    }

    [Fact]
    public void TwoNetworkCardsAreRefusedBecauseTheirSnapshotSectionsWouldCollide()
    {
        var hardware = new HardwareConfiguration
        {
            Network =
            {
                new CardSettings { Card = "ne2000-isa", Port = 0x300, Irq = 3 },
                new CardSettings { Card = "ne2000-isa", Port = 0x320, Irq = 10 },
            },
        };

        Assert.Throws<ArgumentException>(() => new Machine(4 * 1024 * 1024, hardware));
    }

    [Fact]
    public void TheNe2000sRegistersAndBufferRamSurviveASnapshotFile()
    {
        using var machine = new Machine(4 * 1024 * 1024);
        Ne2000 card = machine.Network!;

        // Select page 1 and set a station address byte a driver would read
        // back, and drop a marker into the buffer RAM through remote DMA --
        // both are guest-visible through ordinary ports.
        machine.Io.WriteByte(0x300, 0x42);            // page 1, start
        machine.Io.WriteByte(0x300 + 0x01, 0xAB);      // physical address byte 0

        machine.Io.WriteByte(0x300, 0x21);            // stop, page 0
        machine.Io.WriteByte(0x300 + 0x08, 0x00);      // RSAR low = 0x4000 (page start)
        machine.Io.WriteByte(0x300 + 0x09, 0x40);
        machine.Io.WriteByte(0x300 + 0x0A, 1);         // RBCR = 1 byte
        machine.Io.WriteByte(0x300 + 0x0B, 0);
        machine.Io.WriteByte(0x300, 0x12);             // remote write, start
        machine.Io.WriteByte(0x300 + 0x10, 0x99);      // the marker byte

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);

            // Disturb both, so the restore -- not a coincidence -- is what
            // the assertions below are checking.
            machine.Io.WriteByte(0x300, 0x42);
            machine.Io.WriteByte(0x300 + 0x01, 0x00);
            Assert.Equal((byte)0x99, card.ReadCardMemory(0x4000));
            card.WriteCardMemory(0x4000, 0x00);

            MachineSnapshot.Load(path).RestoreTo(machine);

            machine.Io.WriteByte(0x300, 0x42);             // page 1, start
            Assert.Equal(0xAB, machine.Io.ReadByte(0x300 + 0x01));
            Assert.Equal((byte)0x99, card.ReadCardMemory(0x4000));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- F1.4 review: D2, restore must move the card through Relocate ----

    [Fact]
    public void ANe2000MovedByPlugAndPlaySurvivesARestoreIntoAFreshMachine()
    {
        using var source = new Machine(4 * 1024 * 1024);

        // Moved off the factory address the way Plug and Play configuration
        // does -- Relocate is exactly what a logical device's Configured
        // callback calls (see IsaPnpCards.ForNe2000) -- to 320h.
        source.Network!.Relocate(0x320, 10, enabled: true);

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);

            using var target = new Machine(4 * 1024 * 1024);
            MachineSnapshot.Load(path).RestoreTo(target);

            // The card answers at its new address: the reset port, then the
            // command register once reset.
            target.Io.WriteByte(0x320 + 0x1F, 0);
            Assert.NotEqual(0xFF, target.Io.ReadByte(0x320));

            // The old, factory address is empty.
            Assert.Equal(0xFF, target.Io.ReadByte(0x300));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASnapshotTakenWithNoNetworkCardRestoresAMachineWithNoneEither()
    {
        using var machine = new Machine(4 * 1024 * 1024, new HardwareConfiguration());

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            MachineSnapshot.Load(path).RestoreTo(machine);

            Assert.Empty(machine.Cards);
            Assert.Null(machine.Network);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidateRefusesANetworkCardSectionNewerThanThisBuildBeforeRamIsRestored()
    {
        using var machine = new Machine();

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            BumpSectionVersion(path, "NE2K", (ushort)(Ne2000.SnapshotVersion + 1));

            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Rewrites one section's version field in a saved snapshot file,
    /// leaving the magic word, every other section and every payload
    /// exactly as they were. Mirrors the layout <see cref="MachineSnapshot.Save"/>
    /// writes: the magic word, then for each section a four-letter name, a
    /// two-byte version, a four-byte length and the payload itself.
    /// </summary>
    private static void BumpSectionVersion(string path, string sectionName, ushort newVersion)
    {
        byte[] bytes = File.ReadAllBytes(path);
        const int magicLength = 8;
        int position = magicLength;

        while (position < bytes.Length)
        {
            string name = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
            int versionAt = position + 4;
            int lengthAt = versionAt + 2;
            int length = BitConverter.ToInt32(bytes, lengthAt);
            int payloadAt = lengthAt + 4;

            if (name == sectionName)
            {
                bytes[versionAt] = (byte)(newVersion & 0xFF);
                bytes[versionAt + 1] = (byte)(newVersion >> 8);
                File.WriteAllBytes(path, bytes);
                return;
            }

            position = payloadAt + length;
        }

        throw new InvalidOperationException($"Snapshot has no '{sectionName}' section.");
    }
}
