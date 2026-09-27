using Aurora386.Core.Cpu;
using Aurora386.Core.Cpu.Fpu;
using Aurora386.Core.Cpu.Sse;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// SSE3 operands at a page boundary on a protected-mode 386-style page
/// table. A real #PF handler receives control so fault assertions inspect
/// preserved registers rather than a machine reset after a triple fault.
/// </summary>
public class Sse3FaultTests
{
    /// <summary>Maps the low eight pages except 4000h, with an IDT #PF gate.</summary>
    private static Cpu Prepare(PhysicalMemoryBus bus, string instruction)
    {
        var cpu = new Cpu(bus);
        cpu.State.SelectModel(CpuModel.Core2);
        bus.WriteDword(0x9000, 0xA003);
        for (uint page = 0; page < 8; page++)
        {
            if (page != 4)
            {
                bus.WriteDword(0xA000 + page * 4, page * 0x1000 | 3);
            }
        }

        // Flat ring-zero code and data descriptors, both 32-bit.
        bus.WriteDword(0x5008, 0x0000FFFF);
        bus.WriteDword(0x500C, 0x00CF9A00);
        bus.WriteDword(0x5010, 0x0000FFFF);
        bus.WriteDword(0x5014, 0x00CF9200);
        cpu.State.GdtBase = 0x5000;
        cpu.State.GdtLimit = 0x17;
        cpu.State.IdtBase = 0x6000;
        cpu.State.IdtLimit = 0x7FF;
        uint pageFaultGate = 0x6000 + 14 * 8;
        bus.WriteDword(pageFaultGate, 0x00087000);
        bus.WriteDword(pageFaultGate + 4, 0x00008E00);
        cpu.State.Cr3 = 0x9000;
        cpu.State.Cr0 = CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging
            | CpuState.Cr0ExtensionType | CpuState.Cr0NumericError;
        cpu.SetSegment(SegReg.Cs, 8);
        cpu.SetSegment(SegReg.Ds, 16);
        cpu.SetSegment(SegReg.Ss, 16);
        cpu.State.Eip = 0x1000;
        cpu.State.Esp = 0x8000;
        byte[] bytes = Convert.FromHexString(instruction);
        bytes.CopyTo(bus.GetRamSpan(0x1000, bytes.Length));
        return cpu;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FisttpChecksBothDestinationPagesBeforeAnyStoreOrNewStatus(bool unmaskedInvalid)
    {
        using var bus = new PhysicalMemoryBus();
        Cpu cpu = Prepare(bus, "DD0DFC3F0000"); // FISTTP qword [3FFC].
        var registers = new Raw80[8];
        registers[0] = unmaskedInvalid
            ? new Raw80(0x8000000000000000, 0x7FFF)
            : new Raw80(0xC000000000000000, 0x3FFF);
        ushort control = unmaskedInvalid ? (ushort)0x037E : (ushort)0x037F;
        cpu.Fpu.RestoreState(control, 0x0200, 0xFFFC, registers); // C1 initially set.
        bus.WriteDword(0x3FFC, 0xDEADBEEF);
        ushort tags = cpu.Fpu.TagWord;

        cpu.Step();

        Assert.Equal(0x7000u, cpu.State.Eip);
        Assert.Equal(0x4000u, cpu.State.Cr2);
        Assert.Equal(2u, bus.ReadDword(cpu.State.Esp)); // Supervisor write, page not present.
        Assert.Equal(0x1000u, bus.ReadDword(cpu.State.Esp + 4));
        Assert.Equal(0xDEADBEEFu, bus.ReadDword(0x3FFC));
        Assert.Equal(0x0200, cpu.Fpu.StatusWord);
        Assert.Equal(control, cpu.Fpu.ControlWord);
        Assert.Equal(tags, cpu.Fpu.TagWord);
        Assert.Equal(registers[0].Significand, cpu.Fpu.GetRaw(0).Significand);
    }

    [Fact]
    public void LddquDoesNotCommitTheFirstHalfBeforeTheSecondPageFaults()
    {
        using var bus = new PhysicalMemoryBus();
        Cpu cpu = Prepare(bus, "F20FF005F83F0000"); // LDDQU XMM0,[3FF8].
        cpu.State.Cr4 = CpuState.Cr4OsFxsr;
        XmmValue original = XmmValue.FromHalves(0x123456789ABCDEF0, 0xFEDCBA9876543210);
        cpu.Sse.Xmm(0) = original;
        bus.WriteDword(0x3FF8, 1);
        cpu.Step();

        Assert.Equal(0x7000u, cpu.State.Eip);
        Assert.Equal(0x4000u, cpu.State.Cr2);
        Assert.Equal(0u, bus.ReadDword(cpu.State.Esp)); // Supervisor read, page not present.
        Assert.Equal(original, cpu.Sse.Xmm(0));
    }

    [Fact]
    public void LddquAtTheEndOfAMappedPageDoesNotReadPastItsSixteenBytes()
    {
        using var bus = new PhysicalMemoryBus();
        Cpu cpu = Prepare(bus, "F20FF005F03F0000"); // Exactly 3FF0..3FFF are readable.
        cpu.State.Cr4 = CpuState.Cr4OsFxsr;
        bus.WriteDword(0x3FF0, 0x12345678);
        bus.WriteDword(0x3FFC, 0x87654321);
        cpu.Step();

        Assert.Equal(0x1008u, cpu.State.Eip);
        Assert.Equal(0x12345678u, cpu.Sse.Xmm(0).D0);
        Assert.Equal(0x87654321u, cpu.Sse.Xmm(0).D3);
    }
}
