using System.Buffers.Binary;
using Aurora386.Core.Devices.Audio;
using Aurora386.Core.Devices.IsaPnp;
using Aurora386.Core.Devices.System;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Devices.Usb;
using Aurora386.Core.Devices.Video;
using Aurora386.Core.IO;

namespace Aurora386.Core;

/// <summary>
/// Continues the AT DMA and 1990s ISA audio/PnP services that DOS and Windows
/// drivers program through ports, without replaying their configuration writes.
/// </summary>
public sealed partial class MachineSnapshot
{
    private const ushort DmaRecordKind = 2;
    private const ushort SoundBlasterRecordKind = 3;
    private const ushort IsaPnpRecordKind = 4;
    private const int DmaPayloadLength = 106;
    private const int MaximumDspPayloadLength = 64 * 1024 * 1024;
    private const int MaximumPnpPayloadLength = 4 * 1024 * 1024;
    private readonly byte[] _dma = [];
    private readonly byte[] _soundBlaster = [];
    private readonly byte[] _isaPnp = [];

    /// <summary>Preserves historical absence, empty trailers and Trio-only records.</summary>
    private void WriteDeviceStateTrailer(BinaryWriter file)
    {
        if (!_hasDeviceStateTrailer)
        {
            return;
        }

        byte[][] payloads = [_trioAccelerator, _dma, _soundBlaster, _isaPnp];
        int length = DeviceStateHeaderLength;
        ushort count = 0;
        foreach (byte[] payload in payloads)
        {
            if (payload.Length != 0)
            {
                length = checked(length + DeviceRecordHeaderLength + payload.Length);
                count++;
            }
        }

        file.Write(length);
        file.Write(DeviceStateMagic);
        file.Write(DeviceStateVersion);
        file.Write(count);
        for (int index = 0; index < payloads.Length; index++)
        {
            byte[] payload = payloads[index];
            if (payload.Length == 0)
            {
                continue;
            }
            file.Write((ushort)(index + 1));
            file.Write((ushort)0); // Current device model.
            file.Write((ushort)1); // Record schema.
            file.Write((ushort)0); // Reserved flags.
            file.Write(payload.Length);
            file.Write(payload);
        }
    }

    /// <summary>Bounds every record before allocation and rejects partial audio sets.</summary>
    private static (bool Present, byte[] Trio, byte[] Dma, byte[] Dsp, byte[] Pnp)
        ReadDeviceStateTrailer(BinaryReader file)
    {
        long remaining = file.BaseStream.Length - file.BaseStream.Position;
        if (remaining == 0)
        {
            return (false, [], [], [], []);
        }
        if (remaining < sizeof(int))
        {
            throw new InvalidDataException("The device state snapshot length is truncated.");
        }
        int length = file.ReadInt32();
        if (length < DeviceStateHeaderLength || length != remaining - sizeof(int))
        {
            throw new InvalidDataException("The device state snapshot length is invalid.");
        }
        uint magic = file.ReadUInt32();
        ushort version = file.ReadUInt16();
        ushort count = file.ReadUInt16();
        if (magic != DeviceStateMagic || version != DeviceStateVersion || count > 4)
        {
            throw new InvalidDataException("The device state snapshot header is invalid.");
        }

        byte[][] payloads = [[], [], [], []];
        for (int index = 0; index < count; index++)
        {
            remaining = file.BaseStream.Length - file.BaseStream.Position;
            if (remaining < DeviceRecordHeaderLength)
            {
                throw new InvalidDataException("The device record header is truncated.");
            }
            ushort kind = file.ReadUInt16();
            ushort model = file.ReadUInt16();
            ushort recordVersion = file.ReadUInt16();
            ushort flags = file.ReadUInt16();
            int payloadLength = file.ReadInt32();
            if (kind < TrioAcceleratorRecordKind || kind > IsaPnpRecordKind
                || model != 0 || recordVersion != 1 || flags != 0
                || payloadLength <= 0 || payloadLength > remaining - DeviceRecordHeaderLength
                || payloads[kind - 1].Length != 0)
            {
                throw new InvalidDataException("Invalid or unsupported device snapshot record.");
            }
            if ((kind == TrioAcceleratorRecordKind && payloadLength > S3Accelerator.MaximumSnapshotLength)
                || (kind == DmaRecordKind && payloadLength != DmaPayloadLength)
                || (kind == SoundBlasterRecordKind && payloadLength > MaximumDspPayloadLength)
                || (kind == IsaPnpRecordKind && payloadLength > MaximumPnpPayloadLength))
            {
                throw new InvalidDataException("The device snapshot payload exceeds its schema limit.");
            }
            byte[] payload = file.ReadBytes(payloadLength);
            switch (kind)
            {
                case TrioAcceleratorRecordKind:
                    S3Accelerator.ValidateSnapshot(payload);
                    break;
                case DmaRecordKind:
                    Dma8237.ValidateSnapshot(payload);
                    break;
                case SoundBlasterRecordKind:
                    SoundBlaster16.ValidateSnapshot(payload);
                    break;
                case IsaPnpRecordKind:
                    IsaPnpBus.ValidateSnapshot(payload);
                    break;
            }
            payloads[kind - 1] = payload;
        }
        if (file.BaseStream.Position != file.BaseStream.Length)
        {
            throw new InvalidDataException("Trailing device snapshot data.");
        }
        return (true, payloads[0], payloads[1], payloads[2], payloads[3]);
    }

    private void ValidateDeviceRecordSet()
    {
        if (_soundBlaster.Length > MaximumDspPayloadLength || _isaPnp.Length > MaximumPnpPayloadLength)
        {
            throw new InvalidDataException("The device snapshot payload exceeds its schema limit.");
        }
        bool anyAudio = _dma.Length != 0 || _soundBlaster.Length != 0 || _isaPnp.Length != 0;
        if (anyAudio && (_dma.Length == 0 || _soundBlaster.Length == 0 || _isaPnp.Length == 0
            || (_videoModel == Configuration.VmVideoModel.S3Trio64VPlus && _trioAccelerator.Length == 0)))
        {
            throw new InvalidDataException("The snapshot device record set is incomplete.");
        }
    }

    /// <summary>Builds legacy defaults without changing the target's live devices.</summary>
    private (byte[] Dsp, byte[] Pnp) EffectiveDevicePayloads(Machine machine)
    {
        if (_soundBlaster.Length != 0)
        {
            return (_soundBlaster, _isaPnp);
        }
        var defaults = new SoundBlaster16(outputSampleRate: machine.SoundBlaster.SampleRate);
        return (defaults.Capture(), machine.IsaPlugAndPlay.CaptureResetSnapshot());
    }

    /// <summary>Checks all resource owners and proposed decode before any machine mutation.</summary>
    private void PreflightDevicesFor(Machine machine, bool capturing = false)
    {
        ValidateDeviceRecordSet();
        if ((machine.SoundBlaster.Enabled && machine.SoundBlaster.BasePort > 0xFFF0)
            || (machine.Network.Enabled && machine.Network.BasePort > 0xFFE0))
        {
            throw new InvalidDataException("The target has an out-of-range active ISA decode.");
        }
        if (_dma.Length != 0)
        {
            Dma8237.ValidateSnapshot(_dma);
        }
        (byte[] dsp, byte[] pnp) = EffectiveDevicePayloads(machine);
        machine.SoundBlaster.ValidateSnapshotForRestore(dsp);
        machine.IsaPlugAndPlay.ValidateSnapshotForRestore(pnp);
        IReadOnlyList<IsaPnpBus.LogicalDeviceSnapshot> devices = IsaPnpBus.GetSnapshotLogicalDevices(pnp);
        if (devices.Count != 2 || devices[0].Id != "CTL0031" || devices[1].Id != "RTL8019")
        {
            throw new InvalidDataException("Unsupported machine ISA PnP topology.");
        }
        foreach (IsaPnpBus.LogicalDeviceSnapshot device in devices)
        {
            if (device.Registers[0x30] != device.AppliedRegisters[0x30]
                || (device.Active && !device.Registers.AsSpan(0x30).SequenceEqual(device.AppliedRegisters.AsSpan(0x30))))
            {
                throw new InvalidDataException("Active ISA PnP registers differ from applied configuration.");
            }
        }
        IsaPnpBus.LogicalDeviceSnapshot audio = devices[0];
        IsaPnpBus.LogicalDeviceSnapshot network = devices[1];
        // Only declared ISA resource choices are supported at machine boundaries.
        // Disabled pending registers may be incomplete; inspect the applied image.
        if (audio.IoBase(0) < 0x220 || audio.IoBase(0) > 0x280 || (audio.IoBase(0) & 0x1F) != 0
            || audio.IoBase(1) != 0x388 || audio.Irq(0) is not (2 or 5 or 7 or 9 or 10)
            || audio.Dma(0) is not (0 or 1 or 3 or 4) || audio.Dma(1) is not (4 or 5 or 6 or 7)
            || network.IoBase(0) < 0x240 || network.IoBase(0) > 0x360 || (network.IoBase(0) & 0x1F) != 0
            || network.Irq(0) is not (3 or 4 or 5 or 9 or 10 or 11 or 12 or 15))
        {
            throw new InvalidDataException("Unsupported applied ISA PnP resources.");
        }
        SoundBlaster16.ValidateMachineSnapshotResources(dsp, audio.IoBase(0), audio.Active,
            audio.Irq(0), audio.Dma(0), audio.Dma(1));
        if (capturing && (machine.Network.BasePort != network.IoBase(0)
            || machine.Network.IrqLine != network.Irq(0) || machine.Network.Enabled != network.Active))
        {
            throw new InvalidDataException("Network resource owner differs from applied ISA PnP state.");
        }

        PreflightDeviceMappings(machine, pnp, audio, network, capturing);
    }

    /// <summary>Checks the complete restored PCI/ISA decode against fixed target owners.</summary>
    private void PreflightDeviceMappings(Machine machine, byte[] pnp,
        IsaPnpBus.LogicalDeviceSnapshot audio, IsaPnpBus.LogicalDeviceSnapshot network,
        bool capturing)
    {
        PciBus.SnapshotState pci = machine.Pci.ValidateSnapshot(_pci);
        var proposed = new Dictionary<ushort, IIoDevice>();
        void CheckRange(ushort start, int count, IIoDevice owner)
        {
            for (int offset = 0; offset < count; offset++)
            {
                ushort port = checked((ushort)(start + offset));
                if (!proposed.TryAdd(port, owner))
                {
                    throw new InvalidDataException("Restored ISA devices overlap in I/O space.");
                }
                IIoDevice? current = machine.Io.RegisteredDevice(port);
                if (capturing && !ReferenceEquals(current, owner))
                {
                    throw new InvalidDataException("Source device decode differs from its saved resource owner.");
                }
                bool movable = ReferenceEquals(current, owner)
                    || ReferenceEquals(current, machine.Usb)
                    || ReferenceEquals(current, machine.UsbStorageController)
                    || ReferenceEquals(current, machine.SoundBlaster)
                    || ReferenceEquals(current, machine.Network)
                    || (ReferenceEquals(current, machine.IsaPlugAndPlay)
                        && port == machine.IsaPlugAndPlay.ReadDataPort
                        && port != IsaPnpBus.AddressPort && port != IsaPnpBus.WriteDataPort);
                if (current is not null && !movable)
                {
                    throw new InvalidDataException("Restored ISA decode conflicts with a fixed device port.");
                }
            }
        }
        // PCI restore also rebuilds UHCI decode. Its saved BARs participate in
        // this same overlap check even when a dirty target currently maps elsewhere.
        foreach (UhciController? controller in new[] { machine.Usb, machine.UsbStorageController })
        {
            if (controller is null)
            {
                continue;
            }
            ReadOnlySpan<byte> function = pci.FunctionState(controller.Function.Address);
            uint basePort = controller.Function.Address == 0x10 ? 0xC100u : 0xC120u;
            bool enabled = true;
            if (!pci.IsLegacyReset)
            {
                enabled = (BinaryPrimitives.ReadUInt16LittleEndian(function[4..]) & 1) != 0;
                basePort = BinaryPrimitives.ReadUInt32LittleEndian(function[0x20..]) & 0xFFFFFFE0;
            }
            if (enabled && basePort <= 0xFFE0)
            {
                CheckRange((ushort)basePort, 32, controller);
            }
        }
        CheckRange(IsaPnpBus.AddressPort, 1, machine.IsaPlugAndPlay);
        CheckRange(IsaPnpBus.WriteDataPort, 1, machine.IsaPlugAndPlay);
        CheckRange(0x388, 4, machine.SoundBlaster.Fm);
        CheckRange(IsaPnpBus.GetSnapshotReadDataPort(pnp), 1, machine.IsaPlugAndPlay);
        if (audio.Active)
        {
            CheckRange(audio.IoBase(0), 16, machine.SoundBlaster);
        }
        if (network.Active)
        {
            CheckRange(network.IoBase(0), 32, machine.Network);
        }
    }

    /// <summary>Rebuilds only decode; PIC state is restored by the enclosing method afterwards.</summary>
    private void RestoreDevicesTo(Machine machine)
    {
        (byte[] dsp, byte[] pnp) = EffectiveDevicePayloads(machine);
        IsaPnpBus.LogicalDeviceSnapshot network = IsaPnpBus.GetSnapshotLogicalDevices(pnp)[1];
        // Detach all old ranges first, so a swap cannot erase another restored owner.
        if (machine.SoundBlaster.Enabled)
        {
            machine.Io.Unregister(machine.SoundBlaster.BasePort, 16, machine.SoundBlaster);
        }
        if (machine.Network.Enabled)
        {
            machine.Io.Unregister(machine.Network.BasePort, 32, machine.Network);
        }
        machine.IsaPlugAndPlay.DetachSnapshotPortMapping();
        if (_dma.Length != 0)
        {
            machine.Dma.Restore(_dma);
        }
        else
        {
            machine.Dma.ResetSnapshotState();
        }
        machine.SoundBlaster.Restore(dsp);
        machine.IsaPlugAndPlay.Restore(pnp);
        machine.IsaPlugAndPlay.Register(machine.Io);
        machine.SoundBlaster.Register(machine.Io);
        machine.Network.Relocate(network.IoBase(0), network.Irq(0), network.Active);
    }
}
