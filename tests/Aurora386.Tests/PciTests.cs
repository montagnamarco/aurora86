using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Firmware;
using Aurora386.Core.IO;

namespace Aurora386.Tests;

public class PciTests
{
    private static Machine Boot()
    {
        var m = new Machine();
        m.RunInstructions(3000);
        m.Cpu.SetSegment(SegReg.Cs, 0);
        m.Cpu.SetSegment(SegReg.Ss, 0);
        return m;
    }

    private static void Run(Machine m, params byte[] code)
    {
        for (int i = 0; i < code.Length; i++) m.Memory.WriteByte(0x2000u + (uint)i, code[i]);
        m.Cpu.State.Eip = 0x2000;
        m.Cpu.State.Esp = 0x7000;
        m.Cpu.State.Halted = false;
        for (int i = 0; i < 200 && !m.Cpu.State.Halted; i++) m.Cpu.Step();
        Assert.True(m.Cpu.State.Halted);
        Assert.Equal(0x7000u, m.Cpu.State.Esp);
    }

    private static void Bios(Machine m, ushort function)
    {
        m.Cpu.State.Ax = function;
        Run(m, 0xCD, 0x1A, 0xF4);
    }

    [Fact]
    public void ConfigurationAddressRequiresADwordAndAbsentCyclesFloatHigh()
    {
        using var m = new Machine();
        Assert.Equal(uint.MaxValue, m.Io.ReadDword(0xCFC));
        m.Io.WriteWord(0xCF8, 0);
        m.Io.WriteWord(0xCFA, 0x8000);
        Assert.Equal(0u, m.Io.ReadDword(0xCF8));
        m.Io.WriteDword(0xCF8, 0xFF000003);
        Assert.Equal(0x80000000u, m.Io.ReadDword(0xCF8));
        Assert.Equal(0x122D8086u, m.Io.ReadDword(0xCFC));
        Assert.Equal(0x8086, m.Io.ReadWord(0xCFC));
        Assert.Equal(0x12, m.Io.ReadByte(0xCFF));
        m.Io.WriteByte(0xCFB, 0);
        Assert.Equal(0x80000000u, m.Io.ReadDword(0xCF8));
        Assert.Equal(0xFFFF, m.Io.ReadWord(0xCF8));
        foreach (uint missing in new uint[] { 0x80010000, 0x80001000, 0x80000A00 })
        {
            m.Io.WriteDword(0xCF8, missing);
            Assert.Equal(uint.MaxValue, m.Io.ReadDword(0xCFC));
            m.Io.WriteDword(0xCFC, 0);
            Assert.Equal(uint.MaxValue, m.Io.ReadDword(0xCFC));
        }
    }

    [Fact]
    public void CpuOutAndInUseOneDwordConfigurationCycle()
    {
        using var m = Boot();
        // MOV DX,CF8 / MOV EAX,80000800 / OUT DX,EAX / MOV DX,CFC / IN EAX,DX.
        Run(m, 0xBA, 0xF8, 0x0C, 0x66, 0xB8, 0, 8, 0, 0x80,
            0x66, 0xEF, 0xBA, 0xFC, 0x0C, 0x66, 0xED, 0xF4);
        Assert.Equal(0x122E8086u, m.Cpu.State.Eax);
    }

    [Fact]
    public void BarProbesPreserveTypeSizeAndReadOnlyIdentity()
    {
        var io = new IoBus();
        var bus = new PciBus();
        var device = new PciFunction(0, 0x1234, 0x5678, 0x020000);
        device.DefineBar(0, 0x100, true, 0xC000);
        device.DefineBar(1, 0x100000, false, 0xE0000000);
        bus.Add(device);
        bus.Register(io);
        io.WriteDword(0xCF8, 0x80000000);
        io.WriteDword(0xCFC, uint.MaxValue);
        Assert.Equal(0x56781234u, io.ReadDword(0xCFC));
        io.WriteDword(0xCF8, 0x80000010);
        io.WriteDword(0xCFC, uint.MaxValue);
        Assert.Equal(0x0000FF01u, io.ReadDword(0xCFC));   // I/O BAR bits 31:16 hardwired to zero
        io.WriteDword(0xCFC, 0xC123);
        Assert.Equal(0xC101u, io.ReadDword(0xCFC));
        io.WriteDword(0xCF8, 0x80000014);
        io.WriteDword(0xCFC, uint.MaxValue);
        Assert.Equal(0xFFF00000u, io.ReadDword(0xCFC));
        io.WriteDword(0xCFC, 0xE0123456);
        Assert.Equal(0xE0100000u, io.ReadDword(0xCFC));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotsDistinguishDisabledChannelsFromPreviouslyMissingTimingRegisters(bool legacy)
    {
        using var machine = new Machine();
        machine.Pci.WriteConfiguration(9, 0x40, 4, 0);
        string path = Path.GetTempFileName();
        try
        {
            if (legacy)
            {
                // The preceding format had no IDETIM-presence flag and
                // recorded zero for these then-unimplemented registers.
                LegacySnapshotFile.Write(machine, path, LegacySnapshotEnd.AfterCompactDisc);
            }
            else
            {
                MachineSnapshot.Capture(machine).Save(path);
            }
            machine.Pci.Reset();
            MachineSnapshot.Load(path).RestoreTo(machine);
            uint expectedTiming = legacy ? 0x80008000u : 0u;
            Assert.Equal(expectedTiming, machine.Pci.ReadConfiguration(9, 0x40, 4));
            machine.Io.WriteByte(0x1F2, 0x27);
            Assert.Equal(legacy ? 0x27 : 0xFF, machine.Io.ReadByte(0x1F2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0x40, 0x1F2, 0x172)]
    [InlineData(0x42, 0x172, 0x1F2)]
    public void PiixTimingRegistersEnableEachIdeChannelIndependently(int offset, int port, int otherPort)
    {
        using var machine = new Machine();
        machine.Io.WriteByte((ushort)port, 0x27);
        machine.Io.WriteByte((ushort)otherPort, 0x39);
        Assert.Equal(0x8000u, machine.Pci.ReadConfiguration(9, offset, 2));

        machine.Pci.WriteConfiguration(9, offset, 2, 0);
        Assert.Equal(0xFF, machine.Io.ReadByte((ushort)port));
        Assert.Equal(0x39, machine.Io.ReadByte((ushort)otherPort));

        machine.Pci.WriteConfiguration(9, offset, 2, 0xFFFF);
        Assert.Equal(0xB3FFu, machine.Pci.ReadConfiguration(9, offset, 2));
        Assert.Equal(0x27, machine.Io.ReadByte((ushort)port));
    }

    [Fact]
    public void IdeCommandControlsBothRealCompatibilityChannelsAndResetRestoresThem()
    {
        using var m = new Machine();
        m.Io.WriteByte(0x1F2, 0x27);
        m.Io.WriteByte(0x172, 0x39);
        m.Io.WriteDword(0xCF8, 0x80000904);
        m.Io.WriteByte(0xCFC, 0);
        Assert.Equal(0xFF, m.Io.ReadByte(0x1F2));
        Assert.Equal(0xFF, m.Io.ReadByte(0x172));
        Assert.Equal(0xFF, m.Io.ReadByte(0x3F6));
        Assert.Equal(0xFF, m.Io.ReadByte(0x376));
        m.Io.WriteByte(0xCFC, 0xFF);
        Assert.Equal(1u, m.Io.ReadDword(0xCFC));
        Assert.Equal(0x27, m.Io.ReadByte(0x1F2));
        Assert.Equal(0x39, m.Io.ReadByte(0x172));
        m.Io.WriteByte(0xCFC, 0);
        m.Pci.Reset();
        Assert.Equal(0x27, m.Io.ReadByte(0x1F2));
        Assert.Equal(0u, m.Io.ReadDword(0xCF8));
    }

    [Fact]
    public void Int1AEnumeratesTheMultifunctionBridgeAndPreservesUnspecifiedRegisters()
    {
        using var m = Boot();
        m.Cpu.State.Ecx = 0xABCD1200;
        m.Cpu.State.Edi = 0x12345678;
        m.Cpu.Flags.Carry = true;
        Bios(m, 0xB101);
        Assert.Equal(1, m.Cpu.State.Ax);
        Assert.Equal(0x0210, m.Cpu.State.Bx);
        Assert.Equal(0x20494350u, m.Cpu.State.Edx);
        Assert.Equal(0xABCD1200u, m.Cpu.State.Ecx);
        Assert.Equal(0x12345678u, m.Cpu.State.Edi);
        Assert.False(m.Cpu.Flags.Carry);
        m.Cpu.State.Dx = 0x8086; m.Cpu.State.Cx = 0x1230; m.Cpu.State.Si = 0;
        Bios(m, 0xB102);
        Assert.Equal(9, m.Cpu.State.Bx);
        Assert.False(m.Cpu.Flags.Carry);
        m.Cpu.State.Ecx = 0x010100;
        Bios(m, 0xB103);
        Assert.Equal(9, m.Cpu.State.Bx);
        m.Cpu.State.Si = 1;
        Bios(m, 0xB103);
        Assert.Equal(0x86, m.Cpu.State.GetReg8(4));
        Assert.True(m.Cpu.Flags.Carry);
        m.Cpu.State.Dx = 0xFFFF;
        Bios(m, 0xB102);
        Assert.Equal(0x83, m.Cpu.State.GetReg8(4));
    }

    [Theory]
    [InlineData(0xB108, 0xB10B, 0xABCDEF34u)]
    [InlineData(0xB109, 0xB10C, 0xABCD1234u)]
    [InlineData(0xB10A, 0xB10D, 0x00801234u)]
    public void BiosConfigurationUsesTheSameByteLanesAsPorts(int read, int write, uint expected)
    {
        using var m = Boot();
        m.Cpu.State.Bx = 8; m.Cpu.State.Di = 12;
        m.Cpu.State.Ecx = 0x1234;
        Bios(m, (ushort)write);
        m.Cpu.State.Ecx = 0xABCDEF00;
        Bios(m, (ushort)read);
        Assert.False(m.Cpu.Flags.Carry);
        Assert.Equal(expected, m.Cpu.State.Ecx);
        m.Io.WriteDword(0xCF8, 0x8000080C);
        Assert.Equal(read == 0xB108 ? 0x00800034u : 0x00801234u, m.Io.ReadDword(0xCFC));
    }

    [Theory]
    [InlineData(0xB109, 1)]
    [InlineData(0xB10A, 254)]
    [InlineData(0xB10D, 2)]
    [InlineData(0xB108, 256)]
    public void BiosRejectsMisalignedOrOutOfRangeRegisters(int function, int offset)
    {
        using var m = Boot();
        m.Cpu.State.Di = (ushort)offset;
        Bios(m, (ushort)function);
        Assert.Equal(0x87, m.Cpu.State.GetReg8(4));
        Assert.True(m.Cpu.Flags.Carry);
    }

    [Fact]
    public void AbsentConfigurationReadsSucceedAndUnsupportedServicesSetCarry()
    {
        using var m = Boot();
        m.Cpu.State.Bx = 0xFFFF; m.Cpu.State.Di = 0;
        Bios(m, 0xB10A);
        Assert.Equal(uint.MaxValue, m.Cpu.State.Ecx);
        Assert.False(m.Cpu.Flags.Carry);
        foreach (ushort function in new ushort[] { 0xB106 })
        {
            Bios(m, function);
            Assert.Equal(0x81, m.Cpu.State.GetReg8(4));
            Assert.True(m.Cpu.Flags.Carry);
        }
    }

    [Fact]
    public void BiosReturnsRoutingOptionsAndProgramsTheAdvertisedSlot()
    {
        using var m = Boot();
        m.Cpu.SetSegment(SegReg.Es, 0);
        m.Cpu.State.Di = 0x3000;
        m.Memory.WriteWord(0x3000, 0);
        m.Memory.WriteByte(0x4000, 0xCC);
        Bios(m, 0xB10E);
        Assert.Equal(0x89, m.Cpu.State.GetReg8(4));
        // Sixteen bytes per slot entry; the i430FX board has no integrated USB entry.
        int slotEntriesLength = PciInterruptRouter.SlotCount * 16;
        Assert.Equal(slotEntriesLength, m.Memory.ReadWord(0x3000));
        Assert.Equal(0xCC, m.Memory.ReadByte(0x4000));
        m.Memory.WriteWord(0x3002, 0x4000); m.Memory.WriteWord(0x3004, 0);
        Bios(m, 0xB10E);
        Assert.False(m.Cpu.Flags.Carry);
        Assert.Equal(0, m.Cpu.State.Bx);
        for (uint i = 0; i < slotEntriesLength; i++)
            Assert.Equal(m.Memory.ReadByte(BiosRom.Base + BiosRom.Entry.PciRoutingTable + 32 + i),
                m.Memory.ReadByte(0x4000 + i));
        m.Cpu.State.Bx = 0x10; m.Cpu.State.Cx = 0x0B0A;
        Bios(m, 0xB10F);
        Assert.False(m.Cpu.Flags.Carry);
        Assert.Equal(11u, m.Pci.ReadConfiguration(8, 0x60, 1));
        Assert.Equal(8, m.Io.ReadByte(0x4D1));
        m.Cpu.State.Cx = 0x080A;
        Bios(m, 0xB10F);
        Assert.True(m.Cpu.Flags.Carry);
        Assert.Equal(0x88, m.Cpu.State.GetReg8(4));
    }

    [Fact]
    public void Bios32HeaderAndFarCallsWorkFromAFlat32BitCaller()
    {
        using var m = Boot();
        uint header = BiosRom.Base + BiosRom.Entry.Bios32Header;
        Assert.Equal(0x5F32335Fu, m.Memory.ReadDword(header));
        Assert.Equal(1, m.Memory.ReadByte(header + 9));
        int sum = 0;
        for (uint i = 0; i < 16; i++) sum += m.Memory.ReadByte(header + i);
        Assert.Equal(0, sum & 255);
        // Flat, 32-bit code/data descriptors.
        m.Memory.WriteDword(0x1008, 0x0000FFFF);
        m.Memory.WriteDword(0x100C, 0x00CF9A00);
        m.Memory.WriteDword(0x1010, 0x0000FFFF);
        m.Memory.WriteDword(0x1014, 0x00CF9200);
        m.Cpu.State.GdtBase = 0x1000; m.Cpu.State.GdtLimit = 23;
        m.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable;
        m.Cpu.SetSegment(SegReg.Cs, 8);
        m.Cpu.SetSegment(SegReg.Ss, 16);
        m.Cpu.SetSegment(SegReg.Ds, 16);
        void FarCall(uint address) => Run(m, 0x9A, (byte)address, (byte)(address >> 8),
            (byte)(address >> 16), (byte)(address >> 24), 8, 0, 0xF4);
        m.Cpu.State.Eax = 0x49435024; m.Cpu.State.Ebx = 0;
        m.Cpu.Flags.Carry = true;
        FarCall(m.Memory.ReadDword(header + 4));
        Assert.Equal(0, m.Cpu.State.GetReg8(0));
        Assert.True(m.Cpu.Flags.Carry); // directory returns AL, leaves flags untouched
        Assert.Equal(BiosRom.Base, m.Cpu.State.Ebx);
        Assert.Equal(0x10000u, m.Cpu.State.Ecx);
        uint entry = m.Cpu.State.Ebx + m.Cpu.State.Edx;
        m.Cpu.State.Ax = 0xB101;
        FarCall(entry);
        Assert.Equal(1, m.Cpu.State.Ax);
        Assert.False(m.Cpu.Flags.Carry);
        m.Cpu.State.Ax = 0xB10A; m.Cpu.State.Bx = 9; m.Cpu.State.Di = 0;
        FarCall(entry);
        Assert.Equal(0x12308086u, m.Cpu.State.Ecx);
        m.Cpu.SetSegment(SegReg.Es, 16);
        m.Cpu.State.Edi = 0x13000;
        m.Memory.WriteWord(0x13000, PciInterruptRouter.SlotCount * 16);   // a buffer exactly the size of the slot entries
        m.Memory.WriteDword(0x13002, 0x14000);
        m.Memory.WriteWord(0x13006, 16);
        m.Cpu.State.Ax = 0xB10E;
        FarCall(entry);
        Assert.False(m.Cpu.Flags.Carry);
        Assert.Equal(0x60, m.Memory.ReadByte(0x14002));
        Assert.Equal(5, m.Memory.ReadByte(0x14031) >> 3);
        m.Cpu.State.Ax = 0xB10F;
        FarCall(entry);
        Assert.True(m.Cpu.Flags.Carry);
        m.Cpu.State.Eax = 0; m.Cpu.State.Ebx = 0;
        FarCall(m.Memory.ReadDword(header + 4));
        Assert.Equal(0x80, m.Cpu.State.GetReg8(0));
    }

    [Fact]
    public void SnapshotRestoresConfigurationLatchAndIdeDecodingAndAcceptsOlderFiles()
    {
        using var m = Boot();
        m.Io.WriteDword(0xCF8, 0x80000904);
        m.Io.WriteByte(0xCFC, 0);
        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(m).Save(path);
            m.Pci.Reset();
            MachineSnapshot.Load(path).RestoreTo(m);
            Assert.Equal(0x80000904u, m.Io.ReadDword(0xCF8));
            Assert.Equal(0u, m.Io.ReadDword(0xCFC));
            Assert.Equal(0xFF, m.Io.ReadByte(0x1F2));
            // The earlier format, which ended after the DOS state: no PCI
            // block, no USB block and neither device-state flag.
            LegacySnapshotFile.Write(m, path, LegacySnapshotEnd.AfterDos);
            MachineSnapshot.Load(path).RestoreTo(m);
            Assert.Equal(1u, m.Pci.ReadConfiguration(9, 4, 2));
            Assert.Equal(0u, m.Io.ReadDword(0xCF8));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The display adapter on the PCI bus.
    ///
    /// Windows 95 will not use a card-specific display driver unless it can
    /// identify the card, and for an S3 of this generation it identifies it
    /// by PCI vendor and device ID: its driver database lists the Trio64V+ as
    /// PCI\VEN_5333&DEV_8811 and nothing else.
    /// </summary>
    [Fact]
    public void TheDisplayAdapterAnswersAsAnS3Trio64()
    {
        using var machine = new Machine();
        machine.RunInstructions(100);

        uint identity = machine.Pci.ReadConfiguration(0x20, 0, 4);
        Assert.Equal(0x88115333u, identity);

        // Class 03, subclass 00: a VGA-compatible display controller.
        uint classCode = machine.Pci.ReadConfiguration(0x20, 8, 4) >> 8;
        Assert.Equal(0x030000u, classCode);
    }

    /// <summary>
    /// Windows 95's PCI enumerator reads the command register of a VGA-class
    /// function to decide whether it is the display in use. A Trio whose
    /// decoders read as disabled was left without legacy VGA resources and
    /// reported Code 23 by the VDD. POST enables both decoders, as a system
    /// BIOS does, and the chip's three command bits stay writable.
    /// </summary>
    [Fact]
    public void TheDisplayAdapterReportsEnabledDecodersAfterPost()
    {
        using var machine = new Machine();
        machine.RunInstructions(100);

        Assert.Equal(0x0003u, machine.Pci.ReadConfiguration(0x20, 4, 2));

        // I/O space, memory space and VGA palette snoop accept writes;
        // bus mastering and the rest read back as zero.
        machine.Pci.WriteConfiguration(0x20, 4, 2, 0xFFFF);
        Assert.Equal(0x0023u, machine.Pci.ReadConfiguration(0x20, 4, 2));
        machine.Pci.WriteConfiguration(0x20, 4, 2, 0x0000);
        Assert.Equal(0x0000u, machine.Pci.ReadConfiguration(0x20, 4, 2));
        machine.Pci.WriteConfiguration(0x20, 4, 2, 0x0003);
        Assert.Equal(0x0003u, machine.Pci.ReadConfiguration(0x20, 4, 2));
    }

    /// <summary>
    /// Sizing the framebuffer aperture: software writes all ones to the base
    /// address register and reads back, and the bits that stay clear say how
    /// much the card decodes. The answer has to be the memory that is really
    /// there -- a larger claim gets the driver a mapping of addresses the
    /// card never answers at.
    /// </summary>
    [Fact]
    public void TheFramebufferApertureSizesToTheInstalledVideoMemory()
    {
        using var machine = new Machine();
        machine.RunInstructions(100);

        uint original = machine.Pci.ReadConfiguration(0x20, 0x10, 4);
        Assert.Equal(machine.LinearFrameBufferBase, original & 0xFFFFFFF0u);
        Assert.Equal(0x08u, original & 0x0Fu);          // prefetchable memory

        machine.Pci.WriteConfiguration(0x20, 0x10, 4, uint.MaxValue);
        uint sized = machine.Pci.ReadConfiguration(0x20, 0x10, 4);

        // The decoded size is the lowest settable bit of the address field.
        uint size = ~(sized & 0xFFFFFFF0u) + 1;
        Assert.Equal((uint)Aurora386.Core.Devices.Video.VgaMemory.TotalSize, size);

        machine.Pci.WriteConfiguration(0x20, 0x10, 4, original);
        Assert.Equal(original, machine.Pci.ReadConfiguration(0x20, 0x10, 4));
    }
}
