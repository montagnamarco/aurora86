using Aurora386.Core.Devices.Pci;
using Aurora386.Core.IO;

namespace Aurora386.Core.Devices.Network;

/// <summary>
/// Realtek's 1997 RTL8029AS PCI Ethernet card. Its DP8390 register engine is
/// the existing NE2000 core; this wrapper supplies the PCI BAR, INTA routing
/// and the small page-3 EEPROM/configuration register bank Linux probes.
/// </summary>
public sealed class Rtl8029 : IIoDevice
{
    public const int SnapshotVersion = 1;
    public const int SnapshotLength = Ne2000.SnapshotLength + 4;
    private const int WindowLength = 32;
    private const int EmbeddedCoreVersion = 1; // R829 version one embeds NE2K version one.
    private const byte EepromControlWritableMask = 0xCE;
    private const byte EepromEnableMask = 0xC0;
    private const byte EepromEnabled = 0xC0;
    private const byte EepromReload = 0x40;
    private const byte Config2WritableMask = 0xE0;
    private const byte Config3WritableMask = 0x46;
    private const byte Config3PowerDown = 0x02;
    private const int CoreDataPort = 0x10;
    private const int DataPortFirst = 0x10;
    private const int DataPortLast = 0x17;
    private const int ResetPortFirst = 0x18;
    private const int ResetPortLast = 0x1F;
    private const int CommandRegister = 0x00;
    private const int Page3EepromControl = 0x01;
    private const int Page3Config0 = 0x03;
    private const int Page3Config2 = 0x05;
    private const int Page3Config3 = 0x06;
    private const int Page3HaltClock = 0x09;
    private const int Page3Id0 = 0x0E;
    private const int Page3Id1 = 0x0F;
    private const byte CoreResetCommand = 0x21;
    private readonly PciIoBarWindow _window;
    private byte _eepromControl;
    private byte _config2;
    private byte _config3;
    private byte _haltClock = 0xFF;

    /// <summary>Attaches one PCI function and routes the unregistered core through INTA.</summary>
    public Rtl8029(
        PciBus pci,
        IoBus io,
        INetworkBackend backend,
        ushort functionAddress = 0x0028,
        ushort ioAddress = 0x0300)
    {
        PciInterruptRouter router = pci.InterruptRouter
            ?? throw new InvalidOperationException("RTL8029 requires PCI interrupt routing.");
        if (PciInterruptRouter.Link(functionAddress, 1) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(functionAddress));
        }

        Function = new PciFunction(functionAddress, 0x10EC, 0x8029, 0x020000, commandMask: 3);
        Function.Define(4, 1, 2, 3); // BIOS-assigned I/O decode; no bus mastering.
        Function.Define(6, 0x0200, 2); // Read-only medium DEVSEL status.
        Function.Define(0x0C, 0, 1); // Reserved cache-line size.
        Function.Define(0x0D, 0, 1); // No bus-master latency timer.
        // BAR0 retains every address bit; high addresses must not alias low ports.
        Function.Define(0x10, (uint)(ioAddress & 0xFFE0) | 1, 4, 0xFFFFFFE0);
        Function.Define(0x2C, 0x802910EC, 4); // Synthetic board subsystem identity.
        Function.Define(0x3C, 0, 1, 0x0F); // Interrupt Line bookkeeping only.
        Function.Define(0x3D, 1, 1); // INTA is wired to the motherboard router.
        pci.Add(Function);
        Core = new Ne2000(asserted => router.SetInterrupt(functionAddress, 1, asserted), backend);
        _window = new PciIoBarWindow(Function, 0, WindowLength, io, this);
        ResetNic();
    }

    public PciFunction Function { get; }
    public Ne2000 Core { get; }

    /// <summary>Restores the synthetic EEPROM defaults without resetting PCI configuration.</summary>
    public void Reset()
    {
        _eepromControl = 0;
        _config2 = 0;
        _config3 = 0;
        _haltClock = 0xFF;
        ResetNic();
    }

    /// <summary>Lets the attached network backend deliver frames independently of PCI I/O decode.</summary>
    public void Poll()
    {
        Core.Poll();
    }

    public byte ReadByte(ushort port)
    {
        int offset = Offset(port);
        return offset < 0 ? (byte)0xFF : ReadRegisterByte(offset);
    }

    public ushort ReadWord(ushort port)
    {
        int offset = Offset(port);
        if (offset is >= DataPortFirst and <= DataPortLast)
        {
            return Core.ReadWord(CoreDataPort);
        }

        return (ushort)(ReadByte(port) | (ReadByte((ushort)(port + 1)) << 8));
    }

    public void WriteByte(ushort port, byte value)
    {
        int offset = Offset(port);
        if (offset >= 0)
        {
            WriteRegisterByte(offset, value);
        }
    }

    public void WriteWord(ushort port, ushort value)
    {
        int offset = Offset(port);
        if (offset is >= DataPortFirst and <= DataPortLast)
        {
            Core.WriteWord(CoreDataPort, value);
            return;
        }

        WriteByte(port, (byte)value);
        WriteByte((ushort)(port + 1), (byte)(value >> 8));
    }

    /// <summary>Stores only local NIC state; the PCI bus owns its configuration snapshot.</summary>
    public byte[] Capture()
    {
        byte[] core = Core.Capture();
        return [.. core, _eepromControl, _config2, _config3, _haltClock];
    }

    /// <summary>Rejects malformed local state before the snapshot container changes machine RAM.</summary>
    public static void ValidateSnapshot(byte[] payload, int version)
    {
        if (version != SnapshotVersion || payload.Length != SnapshotLength)
        {
            throw new InvalidDataException("Invalid RTL8029 snapshot envelope.");
        }

        // The embedded core has no ISA port registration or PIC line.
        if (BitConverter.ToUInt16(payload, 0) != 0
            || BitConverter.ToInt32(payload, 2) != 0
            || payload[6] != 1)
        {
            throw new InvalidDataException("RTL8029 snapshot embeds an ISA-attached core.");
        }

        int page3 = Ne2000.SnapshotLength;
        if ((payload[page3] & ~EepromControlWritableMask) != 0 ||
            (payload[page3] & EepromEnableMask) == EepromReload ||
            (payload[page3 + 1] & ~Config2WritableMask) != 0 ||
            (payload[page3 + 2] & ~Config3WritableMask) != 0 ||
            payload[page3 + 3] is not (0xFF or 0x48 or 0x52))
        {
            throw new InvalidDataException("Invalid RTL8029 page-3 snapshot.");
        }
    }

    /// <summary>Restores local registers and DMA while preserving the target backend and PCI BAR.</summary>
    public void Restore(byte[] payload, int version)
    {
        ValidateSnapshot(payload, version);
        int page3 = Ne2000.SnapshotLength;
        _eepromControl = payload[page3];
        _config2 = payload[page3 + 1];
        _config3 = payload[page3 + 2];
        _haltClock = payload[page3 + 3];
        Core.Restore(payload[..page3], EmbeddedCoreVersion);
    }

    /// <summary>Returns the BAR-relative port, or -1 while PCI I/O decoding is closed.</summary>
    private int Offset(ushort port)
    {
        if (_window.MappedBase is not { } mapped)
        {
            return -1;
        }

        int offset = port - mapped;
        return offset is >= 0 and < WindowLength ? offset : -1;
    }
    private byte ReadRegisterByte(int offset)
    {
        if (offset is >= DataPortFirst and <= DataPortLast)
        {
            return Core.ReadByte(CoreDataPort);
        }
        if (offset is >= ResetPortFirst and <= ResetPortLast)
        {
            ResetNic();
            return 0;
        }
        if (offset == 0)
        {
            return Core.ReadByte(0);
        }
        int page = Core.ReadByte(0) >> 6;
        if (page == 3)
        {
            return ReadPage3(offset);
        }
        if (page == 0 && offset == 0x0A)
        {
            // RTL8029 identification bytes share the write-only RBCR ports.
            return 0x50;
        }
        if (page == 0 && offset == 0x0B)
        {
            return 0x43;
        }
        return Core.ReadByte((ushort)offset);
    }

    /// <summary>Keeps page-three writes out of the shared DP8390 register file.</summary>
    private void WriteRegisterByte(int offset, byte value)
    {
        if (offset is >= DataPortFirst and <= DataPortLast)
        {
            Core.WriteByte(CoreDataPort, value);
            return;
        }
        if (offset is >= ResetPortFirst and <= ResetPortLast)
        {
            ResetNic();
            return;
        }
        if (offset == 0)
        {
            Core.WriteByte(0, value);
            return;
        }
        if (Core.ReadByte(0) >> 6 == 3)
        {
            WritePage3(offset, value);
            return;
        }
        Core.WriteByte((ushort)offset, value);
    }

    /// <summary>Reads the RTL8029-specific page-3 configuration subset.</summary>
    private byte ReadPage3(int offset)
    {
        switch (offset)
        {
            case Page3EepromControl:
                return _eepromControl;
            case Page3Config0:
                // The synthetic board assumes TP link, except forced BNC selection.
                return (byte)((_config2 & 0xC0) == 0xC0 ? 0x04 : 0);
            case Page3Config2:
                return _config2;
            case Page3Config3:
                return _config3;
            case Page3Id0:
                return 0x29;
            case Page3Id1:
                return 0x80;
            default:
                return 0;
        }
    }

    /// <summary>Applies the EEPROM unlock protocol; power and clock bits remain stored policy only.</summary>
    private void WritePage3(int offset, byte value)
    {
        if (offset == Page3EepromControl)
        {
            _eepromControl = (byte)(value & EepromControlWritableMask);
            if ((_eepromControl & EepromEnableMask) == EepromReload)
            {
                ReloadConfiguration();
            }
            return;
        }
        if (offset == Page3Config2 && (_eepromControl & EepromEnableMask) == EepromEnabled)
        {
            _config2 = (byte)(value & Config2WritableMask);
        }
        if (offset == Page3Config3 && (_eepromControl & EepromEnableMask) == EepromEnabled)
        {
            _config3 = (byte)(value & Config3WritableMask);
        }
        if (offset == Page3HaltClock && (_config3 & Config3PowerDown) != 0 &&
            value is 0x48 or 0x52)
        {
            _haltClock = value;
        }
    }

    /// <summary>Performs the inherited reset-port operation, preserving Realtek configuration.</summary>
    private void ResetNic()
    {
        Core.Reset();
        Core.WriteByte(CommandRegister, CoreResetCommand);
    }

    /// <summary>Completes the synthetic EEPROM reload synchronously, without a serial EEPROM engine.</summary>
    private void ReloadConfiguration()
    {
        Reset();
    }
}
