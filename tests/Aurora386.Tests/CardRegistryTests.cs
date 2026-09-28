using Aurora386.Core;
using Aurora386.Core.Devices.Audio;
using Aurora386.Core.Devices.Cards;

namespace Aurora386.Tests;

/// <summary>
/// The expansion-card registry: <see cref="CardCatalog"/>, <see
/// cref="HardwareConfiguration"/>, and <see cref="Machine.Cards"/> built from
/// them -- with the Sound Blaster 16 as the first card migrated to it. See
/// <c>plans/specs/device-registry.md</c>.
/// </summary>
public class CardRegistryTests
{
    [Fact]
    public void ADefaultMachineHasOneSoundBlasterCardAtTodaysAddress()
    {
        // new Machine() must build exactly today's hardware: this is what
        // keeps every existing test and bench unchanged by the registry's
        // arrival.
        using var machine = new Machine();

        // The default machine also has one NE2000 card (see
        // Ne2000CardTests) since F1.4b; this asserts on the sound card only.
        IExpansionCard card = Assert.Single(machine.Cards, c => c.Kind == "sound");
        Assert.Equal("sound", card.Kind);
        Assert.Equal("sb16", card.Model);
        Assert.Equal("SB16", card.SnapshotId);

        Assert.NotNull(machine.SoundBlaster);
        Assert.Equal(0x220, machine.SoundBlaster!.BasePort);
        Assert.Equal(5, machine.SoundBlaster.Irq);
        Assert.Equal(1, machine.SoundBlaster.Dma8);
        Assert.Equal(5, machine.SoundBlaster.Dma16);
    }

    [Fact]
    public void AConfigurationWithNoSoundCardHasNoCardAndTheOldPortsAreEmpty()
    {
        using var machine = new Machine(4 * 1024 * 1024, new HardwareConfiguration());

        Assert.Empty(machine.Cards);
        Assert.Null(machine.SoundBlaster);

        // 220h-22Fh answer as an empty bus: 0xFF, exactly like any other
        // unclaimed port -- not a special "no card" value.
        Assert.Equal(0xFF, machine.Io.ReadByte(0x220));
        Assert.Equal(0xFF, machine.Io.ReadByte(0x22A));

        // Nothing crashes advancing devices with no sound card fitted.
        machine.RunInstructions(500);
    }

    [Fact]
    public void ASoundBlasterAtANonDefaultAddressAnswersThereAndRaisesItsConfiguredIrq()
    {
        var hardware = new HardwareConfiguration
        {
            Sound = { new CardSettings { Card = "sb16", Port = 0x240, Irq = 7, Dma = 1, HighDma = 5 } },
        };
        using var machine = new Machine(4 * 1024 * 1024, hardware);

        Assert.Equal(0x240, machine.SoundBlaster!.BasePort);
        Assert.Equal(7, machine.SoundBlaster.Irq);
        Assert.Equal(7, machine.SoundBlaster.Mixer.Irq);

        // The reset handshake answers at the new address...
        machine.Io.WriteByte(0x246, 1);
        machine.Io.WriteByte(0x246, 0);
        Assert.Equal(0xAA, machine.Io.ReadByte(0x24A));

        // ...and the old, standard address is empty.
        Assert.Equal(0xFF, machine.Io.ReadByte(0x220));

        // Command 0xF2 forces an 8-bit IRQ -- exactly what a driver uses to
        // check that the IRQ it was told about is really the one wired up.
        machine.Io.WriteByte(0x21, 0x7F);    // unmask IRQ 7 only
        machine.Io.WriteByte(0x24C, 0xF2);
        Assert.True(machine.Interrupts.HasPendingInterrupt);
    }

    [Fact]
    public void AnUnknownCardNameIsRefusedRatherThanSilentlyOmitted()
    {
        var hardware = new HardwareConfiguration
        {
            Sound = { new CardSettings { Card = "sb99", Port = 0x220 } },
        };

        Assert.Throws<ArgumentException>(() => new Machine(4 * 1024 * 1024, hardware));
    }

    [Fact]
    public void TheSoundBlasterCardsMixerAndDspStateSurviveASnapshotFile()
    {
        // Enough state that a restored guest carries on playing: a mixer
        // register and the DSP's time constant, both plainly guest-visible
        // through the same ports a driver reads them back from.
        using var machine = new Machine(4 * 1024 * 1024);
        SoundBlaster16 sb = machine.SoundBlaster!;

        machine.Io.WriteByte(0x224, 0x30);   // mixer index: master volume, left
        machine.Io.WriteByte(0x225, 0xB8);
        machine.Io.WriteByte(0x22C, 0x40);   // DSP: set time constant
        machine.Io.WriteByte(0x22C, 0xA6);

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);

            // Disturb both values so the restore, not a coincidence, is what
            // the assertions below are checking.
            machine.Io.WriteByte(0x224, 0x30);
            machine.Io.WriteByte(0x225, 0x00);
            machine.Io.WriteByte(0x22C, 0x40);
            machine.Io.WriteByte(0x22C, 0x00);

            MachineSnapshot.Load(path).RestoreTo(machine);

            Assert.Equal(0xB8, sb.Mixer.GetRegister(0x30));
            Assert.Equal(0xA6, sb.TimeConstant);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASnapshotTakenWithNoSoundCardRestoresAMachineWithNoneEither()
    {
        using var machine = new Machine(4 * 1024 * 1024, new HardwareConfiguration());

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            MachineSnapshot.Load(path).RestoreTo(machine);

            Assert.Empty(machine.Cards);
            Assert.Null(machine.SoundBlaster);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- F1.4 review: D2, restore must move the card through Relocate ----

    [Fact]
    public void ASoundBlasterRelocatedByPlugAndPlaySurvivesARestoreIntoAFreshMachine()
    {
        using var source = new Machine(4 * 1024 * 1024);

        // Moved off the factory address the way Plug and Play configuration
        // does: the audio logical device's I/O base (registers 60h/61h) and
        // IRQ (70h) are rewritten and applied, which calls Relocate through
        // the device's Configured callback (see IsaPnpCards.ForSoundBlaster16)
        // -- to 240h, IRQ 7, the same DMA channels. Going through the
        // registers matters: a snapshot whose card and Plug and Play
        // registers disagree is refused, since no guest can produce one.
        const int IoBaseHigh = 0x60;
        const int IoBaseLow = 0x61;
        const int InterruptLevel = 0x70;
        Aurora386.Core.Devices.IsaPnp.IsaPnpLogicalDevice audio = source.IsaPlugAndPlay.Cards
            .SelectMany(card => card.LogicalDevices)
            .Single(device => device.Id == "CTL0031");
        audio.Registers[IoBaseHigh] = 0x02;
        audio.Registers[IoBaseLow] = 0x40;
        audio.Registers[InterruptLevel] = 7;
        audio.NotifyConfigured();

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(source).Save(path);

            using var target = new Machine(4 * 1024 * 1024);
            MachineSnapshot.Load(path).RestoreTo(target);

            // The DSP answers at its new address...
            target.Io.WriteByte(0x246, 1);
            target.Io.WriteByte(0x246, 0);
            Assert.Equal(0xAA, target.Io.ReadByte(0x24A));

            // ...the old, factory address is empty...
            Assert.Equal(0xFF, target.Io.ReadByte(0x220));

            // ...and the mixer reports the new IRQ through register 0x80,
            // exactly what a driver reads back to learn what it was set to.
            target.Io.WriteByte(0x244, 0x80);
            Assert.Equal(0x04, target.Io.ReadByte(0x245));    // bit 2: IRQ 7
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- F1.4 review: D7, Reset must put every DSP field back to power-on ----

    [Fact]
    public void ResetPutsTheSampleRateTestRegisterAndLastOutputByteBackToPowerOn()
    {
        using var machine = new Machine(4 * 1024 * 1024);
        SoundBlaster16 sb = machine.SoundBlaster!;

        // Set the time constant (command 0x40) to something other than the
        // 22050 Hz power-on rate.
        machine.Io.WriteByte(0x22C, 0x40);
        machine.Io.WriteByte(0x22C, 0xA6);
        Assert.NotEqual(22050, sb.PlaybackRate);

        // Write the test register (command 0xE4) to a non-zero value.
        machine.Io.WriteByte(0x22C, 0xE4);
        machine.Io.WriteByte(0x22C, 0x77);

        // Push something other than 0xFF into the read FIFO: the DSP
        // version response (command 0xE1) pushes two bytes, so after both
        // are drained the last one pushed, 5, is what an empty read answers
        // with until the card is reset.
        machine.Io.WriteByte(0x22C, 0xE1);
        machine.Io.ReadByte(0x22A);
        machine.Io.ReadByte(0x22A);

        // Reset the card, exactly as unplugging and replugging it would.
        sb.Reset();

        Assert.Equal(22050, sb.PlaybackRate);

        // Checked before anything else pushes a new byte: the read port
        // answers 0xFF with the FIFO Reset just emptied -- the power-on
        // value, not the 5 pushed by command 0xE1 before the reset.
        Assert.Equal(0xFF, machine.Io.ReadByte(0x22A));

        // Command 0xE8 reads the test register back through the FIFO.
        machine.Io.WriteByte(0x22C, 0xE8);
        Assert.Equal(0x00, machine.Io.ReadByte(0x22A));
    }
}
