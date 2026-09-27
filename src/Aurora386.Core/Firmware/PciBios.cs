using Aurora386.Core.Devices.Pci;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Firmware;

/// <summary>PCI BIOS 2.1 enumeration/configuration, shared by INT 1Ah and BIOS32.</summary>
public sealed class PciBios(PciBus bus)
{
    public void HandleDirectory(CpuNs.Cpu cpu)
    {
        var state = cpu.State;
        if (state.Ebx != 0) { state.SetReg8(0, 0x81); return; }
        if (state.Eax != 0x49435024) { state.SetReg8(0, 0x80); return; } // $PCI
        state.SetReg8(0, 0);
        state.Ebx = BiosRom.Base;
        state.Ecx = 0x10000;
        state.Edx = BiosRom.Entry.PciProtected;
    }

    public void Handle(CpuNs.Cpu cpu, bool fromEntryPoint)
    {
        var state = cpu.State;
        byte status = 0;
        if (state.GetReg8(4) != 0xB1) status = 0x81;
        else switch (state.GetReg8(0))
        {
            case 0x01:
                state.SetReg8(0, 1); // configuration mechanism 1, no special cycles
                state.Bx = 0x0210;
                state.Edx = 0x20494350; // PCI followed by a space
                state.SetReg8(1, (byte)bus.Functions.Select(f => f.Address >> 8).DefaultIfEmpty(0).Max());
                break;
            case 0x02:
            case 0x03:
            {
                bool byDevice = state.GetReg8(0) == 2;
                if (byDevice && state.Dx == 0xFFFF) { status = 0x83; break; }
                var matches = bus.Functions.Where(f => byDevice
                    ? f.VendorId == state.Dx && f.DeviceId == state.Cx
                    : f.ClassCode == (state.Ecx & 0xFFFFFF));
                var found = matches.Skip(state.Si).FirstOrDefault();
                if (found is null) status = 0x86;
                else state.Bx = found.Address;
                break;
            }
            case 0x0E:
            {
                bool wide = fromEntryPoint && state.Cs.Big;
                uint parameters = cpu.FarPointerToLinear(state.Es.Selector, wide ? state.Edi : state.Di);
                ushort capacity = cpu.ReadLinearWord(parameters);
                // Match the ROM table, including PIIX4's integrated USB entry.
                ushort routerDeviceId = bus.InterruptRouter?.CompatibleRouterDeviceId ?? 0x122E;
                byte[] entries = PciInterruptRouter.BuildRoutingTable(routerDeviceId)[32..];
                cpu.WriteLinearWord(parameters, (ushort)entries.Length);
                if (capacity < entries.Length) { status = 0x89; break; }
                uint offset = cpu.ReadLinearWord(parameters + 2);
                if (wide) offset |= (uint)cpu.ReadLinearWord(parameters + 4) << 16;
                ushort segment = cpu.ReadLinearWord(parameters + (wide ? 6u : 4u));
                uint buffer = cpu.FarPointerToLinear(segment, offset);
                for (uint i = 0; i < entries.Length; i++) cpu.WriteLinearByte(buffer + i, entries[i]);
                state.Bx = 0; // all supported IRQs can also serve ISA devices
                break;
            }
            case 0x0F:
            {
                // CL is 0Ah..0Dh. Route programs ELCR; the caller owns PIC masks.
                byte pin = state.GetReg8(1);
                byte irq = state.GetReg8(5);
                bool routed = pin is >= 0x0A and <= 0x0D && bus.InterruptRouter is not null
                    && bus.InterruptRouter.Route(state.Bx, (byte)(pin - 9), irq);
                status = routed ? (byte)0 : (byte)0x88;
                // The Interrupt Line register is scratch for the OS, but a
                // real BIOS still fills it in with what it just routed: many
                // drivers trust 3Ch rather than repeating the PIRQ link math.
                if (routed) bus.WriteConfiguration(state.Bx, 0x3C, 1, irq);
                break;
            }
            case >= 0x08 and <= 0x0D:
            {
                int function = state.GetReg8(0);
                bool write = function >= 0x0B;
                int width = 1 << ((function - (write ? 0x0B : 0x08)));
                int offset = state.Di;
                if (offset > 256 - width || (offset & (width - 1)) != 0)
                { status = 0x87; break; }
                if (write) bus.WriteConfiguration(state.Bx, offset, width, state.Ecx);
                else
                {
                    uint value = bus.ReadConfiguration(state.Bx, offset, width);
                    if (width == 1) state.SetReg8(1, (byte)value);
                    else if (width == 2) state.Cx = (ushort)value;
                    else state.Ecx = value;
                }
                break;
            }
            default:
                status = 0x81; // no special cycles
                break;
        }
        state.SetReg8(4, status);
        if (fromEntryPoint) cpu.Flags.Carry = status != 0;
        else cpu.SetCarryInStackedFlags(status != 0);
    }
}
