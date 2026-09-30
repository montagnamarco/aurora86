using Aurora86.Core.Devices.Pci;
using CpuNs = Aurora86.Core.Cpu;

namespace Aurora86.Core.Firmware;

/// <summary>
/// The 1990s PCI BIOS 2.1 enumeration and configuration service, shared by INT 1Ah
/// callers in real mode and the BIOS32 protected-mode entry point.
/// </summary>
public sealed class PciBios(PciBus bus)
{
    // BIOS32's directory asks for "$PCI" in little-endian EAX.
    private const uint PciBiosServiceSignature = 0x49435024;
    private const uint PciBiosSignature = 0x20494350; // "PCI " in EDX.

    private const byte PciBiosService = 0xB1;
    private const ushort PciBiosVersion21 = 0x0210;

    private const byte InstallationCheckFunction = 0x01;
    private const byte FindDeviceFunction = 0x02;
    private const byte FindClassCodeFunction = 0x03;
    private const byte GetInterruptRoutingOptionsFunction = 0x0E;
    private const byte SetInterruptFunction = 0x0F;
    private const byte ReadConfigurationByteFunction = 0x08;
    private const byte WriteConfigurationByteFunction = 0x0B;
    private const byte WriteConfigurationDwordFunction = 0x0D;
    private const int ConfigurationSpaceLength = 256;
    private const int RoutingTableHeaderLength = 32;
    private const uint ClassCodeMask = 0x00FFFFFF;

    private const byte Successful = 0x00;
    private const byte FunctionNotSupported = 0x81;
    private const byte BadVendorId = 0x83;
    private const byte DeviceNotFound = 0x86;
    private const byte BadRegisterNumber = 0x87;
    private const byte SetFailed = 0x88;
    private const byte BufferTooSmall = 0x89;

    private const ushort PciInterruptLineRegister = 0x3C;
    private const ushort DefaultRouterDeviceId = 0x122E;
    private const byte FirstProgrammableInterrupt = 0x0A;
    private const byte LastProgrammableInterrupt = 0x0D;
    // CL links 0Ah..0Dh map to router pins 1..4, so the wire number is CL - 9.
    private const byte PirqLinkNumberOffset = 9;

    /// <summary>
    /// Answers the BIOS32 directory lookup for the PCI BIOS service.  BIOS32
    /// passes a zero service selector and the <c>$PCI</c> signature here.
    /// </summary>
    public void HandleDirectory(CpuNs.Cpu cpu)
    {
        var state = cpu.State;
        if (state.Ebx != 0)
        {
            state.SetReg8(0, FunctionNotSupported);
            return;
        }

        if (state.Eax != PciBiosServiceSignature)
        {
            state.SetReg8(0, 0x80); // BIOS32: service identifier not found.
            return;
        }

        state.SetReg8(0, Successful);
        state.Ebx = BiosRom.Base;
        state.Ecx = 0x10000; // The service occupies the 64 KiB system BIOS window.
        state.Edx = BiosRom.Entry.PciProtected;
    }

    /// <summary>
    /// Dispatches a PCI BIOS function and reports its status in AH.  The two
    /// entry mechanisms put the carry result in different places: the CPU
    /// flags for BIOS32 and the saved real-mode flags for INT 1Ah.
    /// </summary>
    public void Handle(CpuNs.Cpu cpu, bool fromEntryPoint)
    {
        var state = cpu.State;
        byte status = Successful;

        if (state.GetReg8(4) != PciBiosService)
        {
            status = FunctionNotSupported;
        }
        else
        {
            switch (state.GetReg8(0))
            {
                case InstallationCheckFunction:
                    HandleInstallationCheck(state);
                    break;

                case FindDeviceFunction:
                case FindClassCodeFunction:
                    status = FindDeviceOrClass(state);
                    break;

                case GetInterruptRoutingOptionsFunction:
                    status = GetInterruptRoutingOptions(cpu, fromEntryPoint);
                    break;

                case SetInterruptFunction:
                    status = SetInterrupt(state);
                    break;

                case >= ReadConfigurationByteFunction and <= WriteConfigurationDwordFunction:
                    status = AccessConfiguration(state);
                    break;

                default:
                    status = FunctionNotSupported; // No special cycles.
                    break;
            }
        }

        state.SetReg8(4, status);
        if (fromEntryPoint)
        {
            cpu.Flags.Carry = status != Successful;
        }
        else
        {
            cpu.SetCarryInStackedFlags(status != Successful);
        }
    }

    /// <summary>Returns the PCI BIOS version and configuration mechanism.</summary>
    private void HandleInstallationCheck(CpuNs.CpuState state)
    {
        state.SetReg8(0, 1); // Configuration mechanism 1, no special cycles.
        state.Bx = PciBiosVersion21;
        state.Edx = PciBiosSignature;
        IEnumerable<int> busNumbers = bus.Functions.Select(function => function.Address >> 8);
        int highestBus = busNumbers.DefaultIfEmpty(0).Max();
        state.SetReg8(1, (byte)highestBus);
    }

    /// <summary>Finds the requested device or class, starting at SI's zero-based occurrence.</summary>
    private byte FindDeviceOrClass(CpuNs.CpuState state)
    {
        bool findByDevice = state.GetReg8(0) == FindDeviceFunction;
        if (findByDevice && state.Dx == 0xFFFF)
        {
            return BadVendorId;
        }

        IEnumerable<PciFunction> matches = bus.Functions.Where(function => findByDevice
            ? function.VendorId == state.Dx && function.DeviceId == state.Cx
            : function.ClassCode == (state.Ecx & ClassCodeMask));
        PciFunction? found = matches.Skip(state.Si).FirstOrDefault();
        if (found is null)
        {
            return DeviceNotFound;
        }

        state.Bx = found.Address;
        return Successful;
    }

    /// <summary>Builds the interrupt-routing table in the caller's far buffer.</summary>
    private byte GetInterruptRoutingOptions(CpuNs.Cpu cpu, bool fromEntryPoint)
    {
        var state = cpu.State;
        bool wideParameters = fromEntryPoint && state.Cs.Big;
        uint parameters = cpu.FarPointerToLinear(state.Es.Selector, wideParameters ? state.Edi : state.Di);
        ushort capacity = cpu.ReadLinearWord(parameters);

        // Match the ROM table, including PIIX4's integrated USB entry.
        ushort routerDeviceId = bus.InterruptRouter?.CompatibleRouterDeviceId ?? DefaultRouterDeviceId;
        byte[] routingTable = PciInterruptRouter.BuildRoutingTable(routerDeviceId);
        byte[] entries = routingTable[RoutingTableHeaderLength..];
        cpu.WriteLinearWord(parameters, (ushort)entries.Length);
        if (capacity < entries.Length)
        {
            return BufferTooSmall;
        }

        uint offset = cpu.ReadLinearWord(parameters + 2);
        if (wideParameters)
        {
            offset |= (uint)cpu.ReadLinearWord(parameters + 4) << 16;
        }

        ushort segment = cpu.ReadLinearWord(parameters + (wideParameters ? 6u : 4u));
        uint buffer = cpu.FarPointerToLinear(segment, offset);
        for (uint i = 0; i < entries.Length; i++)
        {
            cpu.WriteLinearByte(buffer + i, entries[i]);
        }

        state.Bx = 0; // All supported IRQs can also serve ISA devices.
        return Successful;
    }

    /// <summary>Connects one PIRQ link to the IRQ in CH and updates PCI offset 3Ch.</summary>
    private byte SetInterrupt(CpuNs.CpuState state)
    {
        // CL is 0Ah..0Dh. Route programs ELCR; the caller owns PIC masks.
        byte pin = state.GetReg8(1);
        byte irq = state.GetReg8(5);
        bool routed = pin is >= FirstProgrammableInterrupt and <= LastProgrammableInterrupt
            && bus.InterruptRouter is not null
            && bus.InterruptRouter.Route(state.Bx, (byte)(pin - PirqLinkNumberOffset), irq);
        if (!routed)
        {
            return SetFailed;
        }

        // The Interrupt Line register is scratch for the OS, but a real BIOS
        // fills it with the route it just installed. Drivers commonly trust
        // offset 3Ch rather than repeat the PIRQ link calculation.
        bus.WriteConfiguration(state.Bx, PciInterruptLineRegister, 1, irq);
        return Successful;
    }

    /// <summary>Reads or writes an aligned byte, word, or dword configuration register.</summary>
    private byte AccessConfiguration(CpuNs.CpuState state)
    {
        int function = state.GetReg8(0);
        bool write = function >= WriteConfigurationByteFunction;
        int firstFunction = write ? WriteConfigurationByteFunction : ReadConfigurationByteFunction;
        int width = 1 << (function - firstFunction);
        int offset = state.Di;
        if (offset > ConfigurationSpaceLength - width || (offset & (width - 1)) != 0)
        {
            return BadRegisterNumber;
        }

        if (write)
        {
            bus.WriteConfiguration(state.Bx, offset, width, state.Ecx);
            return Successful;
        }

        uint value = bus.ReadConfiguration(state.Bx, offset, width);
        if (width == 1)
        {
            state.SetReg8(1, (byte)value);
        }
        else if (width == 2)
        {
            state.Cx = (ushort)value;
        }
        else
        {
            state.Ecx = value;
        }

        return Successful;
    }
}
