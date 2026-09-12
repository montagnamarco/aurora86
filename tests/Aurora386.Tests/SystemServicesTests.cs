using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Firmware;

namespace Aurora386.Tests;

/// <summary>
/// INT 15h services Windows 95 and later lean on: the memory map in its
/// two shapes, and Advanced Power Management.
/// </summary>
public class SystemServicesTests
{
    private static Machine Boot(uint ram = 16 * 1024 * 1024)
    {
        var machine = new Machine(ram);
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;
        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.SetSegment(SegReg.Ds, 0);
        machine.Cpu.SetSegment(SegReg.Es, 0);
        machine.Cpu.State.Esp = 0x7000;
        return machine;
    }

    /// <summary>Executes INT 15h with the registers as set, and reports the carry flag.</summary>
    private static bool Int15(Machine machine)
    {
        machine.Memory.WriteByte(0x2000, 0xCD);
        machine.Memory.WriteByte(0x2001, 0x15);
        machine.Memory.WriteByte(0x2002, 0xF4);   // HLT
        machine.Cpu.State.Eip = 0x2000;
        machine.Cpu.State.Halted = false;
        for (int i = 0; i < 20 && !machine.Cpu.State.Halted; i++) machine.Cpu.Step();
        Assert.True(machine.Cpu.State.Halted);
        return machine.Cpu.Flags.Carry;
    }

    // --- memory ------------------------------------------------------------------------------

    [Fact]
    public void E801ReportsMemoryBelowAndAboveSixteenMegabytes()
    {
        using Machine machine = Boot(32 * 1024 * 1024);
        machine.Cpu.State.Ax = 0xE801;
        Assert.False(Int15(machine));
        Assert.Equal(15 * 1024, machine.Cpu.State.Ax);       // 1-16 MB, capped at 15 MB
        Assert.Equal(15 * 1024, machine.Cpu.State.Cx);
        Assert.Equal(16 * 1024 * 1024 / 65536, machine.Cpu.State.Bx);
        Assert.Equal(machine.Cpu.State.Bx, machine.Cpu.State.Dx);
    }

    [Fact]
    public void E801WithEightMegabytesHasNothingAboveSixteen()
    {
        using Machine machine = Boot(8 * 1024 * 1024);
        machine.Cpu.State.Ax = 0xE801;
        Assert.False(Int15(machine));
        Assert.Equal(7 * 1024, machine.Cpu.State.Ax);
        Assert.Equal(0, machine.Cpu.State.Bx);
    }

    [Fact]
    public void AHundredAndTwentyEightMegabytesAreReportedEverywhereTheyShouldBe()
    {
        using Machine machine = Boot(128 * 1024 * 1024);

        // INT 15h E801h: 15 MB below the hole, then (128 - 16) MB in 64 KB blocks.
        machine.Cpu.State.Ax = 0xE801;
        Assert.False(Int15(machine));
        Assert.Equal(15 * 1024, machine.Cpu.State.Ax);
        Assert.Equal((128 - 16) * 16, machine.Cpu.State.Bx);

        // The CMOS: the 16-bit count above 1 MB saturates, the pair above
        // 16 MB carries the rest.
        byte Cmos(byte register) { machine.Io.WriteByte(0x70, register); return machine.Io.ReadByte(0x71); }
        Assert.Equal(0xFFFF, Cmos(0x30) | (Cmos(0x31) << 8));
        Assert.Equal((128 - 16) * 16, Cmos(0x34) | (Cmos(0x35) << 8));

        // And the map counts every byte of it as usable.
        var usable = machine.MemoryMap().Where(e => e.Type == 1).Sum(e => (long)e.Length);
        Assert.Equal(128L * 1024 * 1024 - (0xA0000 - 0x9FC00) - 0x60000, usable);
    }

    [Fact]
    public void E820WalksTheMapAndEndsWithZeroContinuation()
    {
        using Machine machine = Boot(32 * 1024 * 1024);
        var entries = new List<(ulong Start, ulong Length, uint Type)>();
        uint continuation = 0;
        for (int guard = 0; guard < 16; guard++)
        {
            machine.Cpu.State.Eax = 0xE820;
            machine.Cpu.State.Edx = 0x534D4150;
            machine.Cpu.State.Ecx = 20;
            machine.Cpu.State.Ebx = continuation;
            machine.Cpu.State.Edi = 0x3000;
            Assert.False(Int15(machine));
            Assert.Equal(0x534D4150u, machine.Cpu.State.Eax);
            Assert.Equal(20u, machine.Cpu.State.Ecx);

            ulong start = machine.Memory.ReadDword(0x3000) | ((ulong)machine.Memory.ReadDword(0x3004) << 32);
            ulong length = machine.Memory.ReadDword(0x3008) | ((ulong)machine.Memory.ReadDword(0x300C) << 32);
            entries.Add((start, length, machine.Memory.ReadDword(0x3010)));
            continuation = machine.Cpu.State.Ebx;
            if (continuation == 0) break;
        }

        Assert.Equal(machine.MemoryMap(), entries);
        Assert.Contains((0ul, 0x9FC00ul, 1u), entries);
        Assert.Contains((0x100000ul, 31ul * 1024 * 1024, 1u), entries);
        Assert.Contains(entries, e => e.Start == 0xF0000 && e.Type == 2);
        // Usable ranges never overlap reserved ones.
        foreach (var a in entries.Where(e => e.Type == 1))
            foreach (var b in entries.Where(e => e.Type == 2))
                Assert.True(a.Start + a.Length <= b.Start || b.Start + b.Length <= a.Start);
    }

    [Fact]
    public void E820WithoutTheSignatureIsRefused()
    {
        using Machine machine = Boot();
        machine.Cpu.State.Eax = 0xE820;
        machine.Cpu.State.Edx = 0;
        machine.Cpu.State.Ecx = 20;
        machine.Cpu.State.Edi = 0x3000;
        Assert.True(Int15(machine));
        Assert.Equal(0x86, machine.Cpu.State.GetReg8(4));
    }

    // --- APM ---------------------------------------------------------------------------------

    private static bool Apm(Machine machine, byte function, ushort bx = 0, ushort cx = 0)
    {
        machine.Cpu.State.Ax = (ushort)(0x5300 | function);
        machine.Cpu.State.Bx = bx;
        machine.Cpu.State.Cx = cx;
        return Int15(machine);
    }

    [Fact]
    public void InstallationCheckSaysVersion12WithBothProtectedInterfaces()
    {
        using Machine machine = Boot();
        Assert.False(Apm(machine, 0x00));
        Assert.Equal(0x0102, machine.Cpu.State.Ax);
        Assert.Equal(0x504D, machine.Cpu.State.Bx);    // "PM"
        Assert.Equal(0x0003, machine.Cpu.State.Cx & 3);
    }

    [Fact]
    public void ConnectingTwiceIsRefusedAndDisconnectingAllowsItAgain()
    {
        using Machine machine = Boot();
        Assert.False(Apm(machine, 0x01));
        Assert.Equal(ApmBios.Connection.RealMode, machine.PowerManagement.Connected);
        Assert.True(Apm(machine, 0x01));
        Assert.Equal(0x02, machine.Cpu.State.GetReg8(4));
        Assert.False(Apm(machine, 0x04));
        Assert.False(Apm(machine, 0x02));
        Assert.Equal(ApmBios.Connection.Protected16, machine.PowerManagement.Connected);
    }

    [Fact]
    public void ThirtyTwoBitConnectHandsOutTheRomEntryPoint()
    {
        using Machine machine = Boot();
        Assert.False(Apm(machine, 0x03));
        Assert.Equal(0xF000, machine.Cpu.State.Ax);
        Assert.Equal((uint)BiosRom.Entry.AdvancedPowerManagement, machine.Cpu.State.Ebx);
        Assert.Equal(0xF000, machine.Cpu.State.Dx);

        uint entry = 0xF0000 + machine.Cpu.State.Ebx;
        Assert.Equal(0x0F, machine.Memory.ReadByte(entry));
        Assert.Equal(0xFF, machine.Memory.ReadByte(entry + 1));
        Assert.Equal(0xCB, machine.Memory.ReadByte(entry + 3));
    }

    [Fact]
    public void DriverVersionIsNegotiatedDownToOneTwo()
    {
        using Machine machine = Boot();
        Apm(machine, 0x01);
        Assert.False(Apm(machine, 0x0E, 0, 0x0103));    // driver claims 1.3
        Assert.Equal(0x0102, machine.Cpu.State.Ax);
        Assert.Equal((1, 2), machine.PowerManagement.DriverVersion);
    }

    [Fact]
    public void PowerStatusSaysAcLineAndNoBattery()
    {
        using Machine machine = Boot();
        Apm(machine, 0x01);
        Assert.False(Apm(machine, 0x0A, 0x0001));
        Assert.Equal(0x01, machine.Cpu.State.GetReg8(7));   // BH: on line
        Assert.Equal(0xFF, machine.Cpu.State.GetReg8(3));   // BL: battery unknown
    }

    [Fact]
    public void GetEventReportsNoneWithCarrySet()
    {
        using Machine machine = Boot();
        Apm(machine, 0x01);
        Assert.True(Apm(machine, 0x0B));
        Assert.Equal(0x80, machine.Cpu.State.GetReg8(4));
    }

    [Fact]
    public void SettingAllDevicesOffRaisesPowerOff()
    {
        using Machine machine = Boot();
        int raised = 0;
        machine.PowerOffRequested += () => raised++;
        Apm(machine, 0x01);
        Assert.False(Apm(machine, 0x07, 0x0001, 0x0001));   // standby: accepted, nothing happens
        Assert.Equal(0, raised);
        Assert.False(Apm(machine, 0x07, 0x0001, 0x0003));   // off
        Assert.Equal(1, raised);
    }

    [Fact]
    public void AnUnknownPowerStateIsAnError()
    {
        using Machine machine = Boot();
        Apm(machine, 0x01);
        Assert.True(Apm(machine, 0x07, 0x0001, 0x0007));
        Assert.Equal(0x0A, machine.Cpu.State.GetReg8(4));
    }

    [Fact]
    public void TheProtectedModeEntryAnswersAFarCallWithTheFunctionInAx()
    {
        // 16-bit protected mode: selector 8 is the BIOS code at F0000,
        // selector 16 flat data, 24 the caller's code. The APM entry is
        // called far with AX = 5300h, the installation check.
        using Machine machine = Boot();
        const uint gdt = 0x1000;
        void Descriptor(int index, uint baseAddress, uint limit, byte access)
        {
            uint a = gdt + (uint)(index * 8);
            machine.Memory.WriteWord(a, (ushort)limit);
            machine.Memory.WriteWord(a + 2, (ushort)baseAddress);
            machine.Memory.WriteByte(a + 4, (byte)(baseAddress >> 16));
            machine.Memory.WriteByte(a + 5, access);
            machine.Memory.WriteByte(a + 6, 0);
            machine.Memory.WriteByte(a + 7, (byte)(baseAddress >> 24));
        }
        Descriptor(1, 0xF0000, 0xFFFF, 0x9A);
        Descriptor(2, 0, 0xFFFF, 0x92);
        Descriptor(3, 0, 0xFFFF, 0x9A);
        machine.Cpu.State.GdtBase = gdt;
        machine.Cpu.State.GdtLimit = 0x1F;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.SetSegment(SegReg.Cs, 24);
        machine.Cpu.SetSegment(SegReg.Ss, 16);
        machine.Cpu.SetSegment(SegReg.Ds, 16);

        byte[] code =
        [
            0xB8, 0x00, 0x53,                     // MOV AX, 5300h
            0x31, 0xDB,                           // XOR BX, BX
            0x9A, unchecked((byte)BiosRom.Entry.AdvancedPowerManagement), (byte)(BiosRom.Entry.AdvancedPowerManagement >> 8), 0x08, 0x00,
            0xF4,
        ];
        for (int i = 0; i < code.Length; i++) machine.Memory.WriteByte(0x2000 + (uint)i, code[i]);
        machine.Cpu.State.Eip = 0x2000;
        machine.Cpu.State.Esp = 0x7000;
        for (int i = 0; i < 20 && !machine.Cpu.State.Halted; i++) machine.Cpu.Step();

        Assert.True(machine.Cpu.State.Halted);
        Assert.Equal(0x0102, machine.Cpu.State.Ax);
        Assert.Equal(0x504D, machine.Cpu.State.Bx);
        Assert.Equal(0x7000u, machine.Cpu.State.Esp);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ApmFarCallsReturnCarryWithoutChangingTheCallersStack(bool wide, bool unsupported)
    {
        using Machine machine = Boot();
        const uint descriptorTable = 0x1000;
        void WriteDescriptor(int index, uint baseAddress, byte access)
        {
            uint address = descriptorTable + (uint)(index * 8);
            machine.Memory.WriteWord(address, 0xFFFF);
            machine.Memory.WriteWord(address + 2, (ushort)baseAddress);
            machine.Memory.WriteByte(address + 4, (byte)(baseAddress >> 16));
            machine.Memory.WriteByte(address + 5, access);
            machine.Memory.WriteByte(address + 6, wide ? (byte)0x40 : (byte)0);
            machine.Memory.WriteByte(address + 7, (byte)(baseAddress >> 24));
        }
        WriteDescriptor(1, 0xF0000, 0x9A);
        WriteDescriptor(2, 0, 0x92);
        WriteDescriptor(3, 0, 0x9A);
        machine.Cpu.State.GdtBase = descriptorTable;
        machine.Cpu.State.GdtLimit = 0x1F;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        machine.Cpu.SetSegment(SegReg.Cs, 24);
        machine.Cpu.SetSegment(SegReg.Ss, 16);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = 0x2000;
        machine.Cpu.State.Ax = unsupported ? (ushort)0x5312 : (ushort)0x5300;
        machine.Cpu.State.Bx = 0;
        machine.Cpu.Flags.Carry = !unsupported;
        const uint stackSentinel = 0xAABBCCDD;
        machine.Memory.WriteDword(0x7000, stackSentinel);

        // A 32-bit far return places CS at ESP+4, where an interrupt's
        // 16-bit FLAGS would be. APM must leave that selector untouched.
        byte[] code = wide
            ? [0x9A, 0x50, 0x1A, 0x00, 0x00, 0x08, 0x00, 0xF4]
            : [0x9A, 0x50, 0x1A, 0x08, 0x00, 0xF4];
        for (int index = 0; index < code.Length; index++)
        {
            machine.Memory.WriteByte(0x2000 + (uint)index, code[index]);
        }
        var exceptions = new List<byte>();
        machine.Cpu.ProcessorException += (vector, _, _, _) => exceptions.Add(vector);
        for (int instruction = 0; instruction < 20 && !machine.Cpu.State.Halted; instruction++)
        {
            machine.Cpu.Step();
        }

        Assert.Empty(exceptions);
        Assert.True(machine.Cpu.State.Halted);
        Assert.Equal(unsupported, machine.Cpu.Flags.Carry);
        Assert.Equal(unsupported ? 0x8612 : 0x0102, machine.Cpu.State.Ax);
        Assert.Equal(24, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x7000u, machine.Cpu.State.Esp);
        Assert.Equal(stackSentinel, machine.Memory.ReadDword(0x7000));
    }

}
