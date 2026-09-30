using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Video;
using Aurora86.Core.IO;

namespace Aurora86.Tests;

/// <summary>
/// BIOS colour text services run in ROM so a virtual display device can
/// redirect a DOS console without changing the Windows desktop.
/// These tests exercise the interrupt instead of calling video services,
/// because the order and origin of the guest stores are the contract.
/// </summary>
public sealed class VideoTextRomTests
{
    /// <summary>
    /// Runs the ROM through a 16-bit ring-zero protected-mode caller whose
    /// page zero and B8000 page are private guest memory.  This is the shape
    /// of a display monitor: literal BIOS selectors still name the BDA, ROM
    /// and text aperture, while paging decides which backing bytes the guest
    /// can change.
    /// </summary>
    private sealed class ProtectedTextModeFixture : IDisposable
    {
        // The GDT and IDT sit outside the remapped pages, as a monitor's
        // descriptor tables do; page zero and B8000 alone diverge below.
        private const uint Gdt = 0x100000;
        private const uint Idt = 0x120000;
        private const uint PageDirectory = 0x140000;
        private const uint PageTable = 0x141000;
        private const uint PrivateBda = 0x150000;
        private const uint PrivateVideo = 0x151000;

        private readonly VgaRegisters _shadow = new();

        public Machine Machine { get; } = new();
        public byte[] PhysicalRegistersBefore { get; }
        public byte[] PhysicalVideoBefore { get; }
        public byte[] PhysicalBdaBefore { get; }
        public byte PhysicalVideoModeBefore { get; }
        public int PhysicalTextColumnsBefore { get; }
        public int PhysicalTextRowsBefore { get; }
        public int PhysicalCharacterHeightBefore { get; }
        public VgaRegisters Shadow => _shadow;

        public ProtectedTextModeFixture(bool videoPagePresent, ushort requestedMode = 0x0003, bool direction = false)
        {
            Machine.RunInstructions(3000);
            Machine.VideoServices.SetVideoMode(0x13);
            Machine.VideoRegisters.SetVesaMode(VesaModes.Get(0x101, VgaMemory.TotalSize), false);
            Machine.VideoRegisters.VesaWindowBank = 3;
            Machine.VideoRegisters.VesaScanLineBytes = 704;
            Machine.VideoRegisters.VesaDisplayStart = 1024;

            foreach (ushort port in new ushort[] { 0x3B4, 0x3B5, 0x3C0, 0x3C1, 0x3C2, 0x3C4, 0x3C5, 0x3C8, 0x3C9, 0x3CC, 0x3CE, 0x3CF, 0x3D4, 0x3D5, 0x3DA })
            {
                Machine.Io.Register(port, _shadow);
            }

            // These are 16-bit ring-zero selectors, including the literal
            // segments the BIOS instructions use for BDA, VRAM and ROM.
            WriteDescriptor(0x0040, 0x400, 0x92);
            WriteDescriptor(0x2000, 0x20000, 0x9A);
            WriteDescriptor(0x3000, 0x30000, 0x92);
            WriteDescriptor(0xB800, 0xB8000, 0x92);
            WriteDescriptor(0xF000, 0xF0000, 0x9A);
            WriteGate(0x10, 0xF000, 0x1000);
            WriteGate(0x0E, 0x2000, 0x0200);

            // Identity-map four megabytes, then replace page zero and B8000
            // so their guest stores cannot reach the physical desktop.
            for (uint page = 0; page < 1024; page++)
            {
                Machine.Memory.WriteDword(PageTable + page * 4, (page << 12) | 3);
            }

            for (uint offset = 0; offset < 0x1000; offset++)
            {
                Machine.Memory.WriteByte(PrivateBda + offset, Machine.Memory.ReadByte(offset));
                Machine.Memory.WriteByte(PrivateVideo + offset, 0xA5);
            }

            Machine.Memory.WriteDword(PageDirectory, PageTable | 3);
            Machine.Memory.WriteDword(PageTable, PrivateBda | 3);
            Machine.Memory.WriteDword(PageTable + 0xB8 * 4, PrivateVideo | (videoPagePresent ? 3u : 2u));
            Machine.Memory.WriteByte(0x20100, 0xB8);
            Machine.Memory.WriteWord(0x20101, requestedMode);
            Machine.Memory.WriteByte(0x20103, 0xCD);
            Machine.Memory.WriteByte(0x20104, 0x10);
            Machine.Memory.WriteByte(0x20105, 0xF4);
            Machine.Memory.WriteByte(0x20200, 0xF4);

            PhysicalRegistersBefore = Machine.VideoRegisters.CaptureRegisters();
            PhysicalVideoBefore = Machine.VideoMemory.CaptureMemory();
            PhysicalBdaBefore = Enumerable.Range(0, 0x100)
                .Select(index => Machine.Memory.ReadByte(0x400u + (uint)index)).ToArray();
            PhysicalVideoModeBefore = Machine.VideoRegisters.VideoMode;
            PhysicalTextColumnsBefore = Machine.VideoRegisters.TextColumns;
            PhysicalTextRowsBefore = Machine.VideoRegisters.TextRows;
            PhysicalCharacterHeightBefore = Machine.VideoRegisters.CharacterHeight;

            CpuState state = Machine.Cpu.State;
            state.GdtBase = Gdt;
            state.GdtLimit = 0xF007;
            state.IdtBase = Idt;
            state.IdtLimit = 0x7FF;
            state.Cr3 = PageDirectory;
            state.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
            Machine.Cpu.SetSegment(SegReg.Cs, 0x2000);
            Machine.Cpu.SetSegment(SegReg.Ss, 0x3000);
            Machine.Cpu.SetSegment(SegReg.Ds, 0x0040);
            Machine.Cpu.SetSegment(SegReg.Es, 0x3000);
            state.Eip = 0x100;
            state.Esp = 0xFFFE;
            state.Eax = 0xA1B2C3D4;
            state.Ebx = 0xB2C3D4E5;
            state.Ecx = 0xC3D4E5F6;
            state.Edx = 0xD4E5F607;
            state.Esi = 0xE5F60718;
            state.Edi = 0xF6071829;
            state.Ebp = 0x0718293A;
            state.Halted = false;
            Machine.Cpu.Flags.Interrupt = false;
            Machine.Cpu.Flags.Direction = direction;
        }

        public byte PrivateBdaByte(uint offset) => Machine.Memory.ReadByte(PrivateBda + offset);
        public ushort PrivateBdaWord(uint offset) => Machine.Memory.ReadWord(PrivateBda + offset);
        public ushort PrivateVideoWord(uint offset) => Machine.Memory.ReadWord(PrivateVideo + offset);
        public byte PrivateVideoByte(uint offset) => Machine.Memory.ReadByte(PrivateVideo + offset);

        public void SetGuestTextState(ushort cursor = 0, ushort columns = 80, byte mode = 3)
        {
            Machine.Memory.WriteByte(PrivateBda + 0x449, mode);
            Machine.Memory.WriteWord(PrivateBda + 0x44A, columns);
            Machine.Memory.WriteWord(PrivateBda + 0x450, cursor);
            Machine.Memory.WriteWord(PrivateBda + 0x460, 0x0607);
            Machine.Memory.WriteByte(PrivateBda + 0x462, 2);
            Machine.Memory.WriteByte(PrivateBda + 0x484, 24);
        }

        public void SetGuestVideoWord(uint offset, ushort value)
        {
            Machine.Memory.WriteWord(PrivateVideo + offset, value);
        }

        public void MapVideoPage(uint page, bool present)
        {
            Machine.Memory.WriteDword(PageTable + page * 4,
                (PrivateVideo + (page - 0xB8) * 0x1000) | (present ? 3u : 2u));
            Machine.Cpu.InvalidatePage(page << 12);
        }

        public void InstallFaultReturn()
        {
            // The 16-bit fault frame starts with its error code. Return to
            // the faulting ROM instruction, preserving its REP progress.
            Machine.Memory.WriteByte(0x20200, 0x83);
            Machine.Memory.WriteByte(0x20201, 0xC4);
            Machine.Memory.WriteByte(0x20202, 0x02);
            Machine.Memory.WriteByte(0x20203, 0xCF);
        }

        public void AssertPhysicalDesktopUnchanged()
        {
            Assert.Equal(PhysicalRegistersBefore, Machine.VideoRegisters.CaptureRegisters());
            Assert.Equal(PhysicalVideoBefore, Machine.VideoMemory.CaptureMemory());
            Assert.Equal(PhysicalBdaBefore, Enumerable.Range(0, 0x100)
                .Select(index => Machine.Memory.ReadByte(0x400u + (uint)index)).ToArray());
            Assert.Equal(PhysicalVideoModeBefore, Machine.VideoRegisters.VideoMode);
            Assert.Equal(PhysicalTextColumnsBefore, Machine.VideoRegisters.TextColumns);
            Assert.Equal(PhysicalTextRowsBefore, Machine.VideoRegisters.TextRows);
            Assert.Equal(PhysicalCharacterHeightBefore, Machine.VideoRegisters.CharacterHeight);
        }

        public void Dispose() => Machine.Dispose();

        private void WriteDescriptor(ushort selector, uint baseAddress, byte access)
        {
            uint address = Gdt + selector;
            Machine.Memory.WriteWord(address, 0xFFFF);
            Machine.Memory.WriteWord(address + 2, (ushort)baseAddress);
            Machine.Memory.WriteByte(address + 4, (byte)(baseAddress >> 16));
            Machine.Memory.WriteByte(address + 5, access);
            Machine.Memory.WriteByte(address + 6, 0);
            Machine.Memory.WriteByte(address + 7, (byte)(baseAddress >> 24));
        }

        private void WriteGate(byte vector, ushort selector, ushort offset)
        {
            uint address = Idt + (uint)vector * 8;
            Machine.Memory.WriteWord(address, offset);
            Machine.Memory.WriteWord(address + 2, selector);
            Machine.Memory.WriteByte(address + 4, 0);
            Machine.Memory.WriteByte(address + 5, 0x86);
            Machine.Memory.WriteWord(address + 6, 0);
        }
    }

    private static void RunInt10(Machine machine, ushort ax)
    {
        const uint code = 0x20100;
        machine.Memory.WriteByte(code, 0xB8);
        machine.Memory.WriteWord(code + 1, ax);
        machine.Memory.WriteByte(code + 3, 0xCD);
        machine.Memory.WriteByte(code + 4, 0x10);
        machine.Memory.WriteByte(code + 5, 0xF4);
        machine.Cpu.SetSegment(Aurora86.Core.Cpu.SegReg.Cs, 0x2000);
        machine.Cpu.SetSegment(Aurora86.Core.Cpu.SegReg.Ss, 0x3000);
        machine.Cpu.State.Eip = 0x0100;
        machine.Cpu.State.Esp = 0xFFFE;
        machine.Cpu.State.Halted = false;

        for (int i = 0; i < 20_000 && !machine.Cpu.State.Halted; i++)
        {
            machine.RunInstructions(1);
        }

        Assert.True(machine.Cpu.State.Halted, "the INT 10h caller did not reach HLT");
    }

    private static void SetVesaMode(Machine machine)
    {
        machine.Cpu.State.Bx = 0x8101;
        RunInt10(machine, 0x4F02);
        Assert.NotNull(machine.VideoRegisters.VesaMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AVideoModeQueryReadsTheVirtualBda(bool direction)
    {
        using var fixture = new ProtectedTextModeFixture(true, 0x0F00, direction);
        fixture.SetGuestTextState(columns: 40);
        Machine machine = fixture.Machine;
        RunInt10(machine, 0x0F00);

        Assert.Equal(0xA1B22803u, machine.Cpu.State.Eax);
        Assert.Equal(0xB2C302E5u, machine.Cpu.State.Ebx);
        Assert.Equal(0xC3D4E5F6u, machine.Cpu.State.Ecx);
        Assert.Equal(0xD4E5F607u, machine.Cpu.State.Edx);
        Assert.Equal(0xE5F60718u, machine.Cpu.State.Esi);
        Assert.Equal(0xF6071829u, machine.Cpu.State.Edi);
        Assert.Equal(0x0718293Au, machine.Cpu.State.Ebp);
        Assert.Equal((ushort)0x0040, machine.Cpu.State.Ds.Selector);
        Assert.Equal((ushort)0x3000, machine.Cpu.State.Es.Selector);
        Assert.Equal(direction, machine.Cpu.Flags.Direction);
        fixture.AssertPhysicalDesktopUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextCursorAndReadCallsUseTheVirtualBdaAndCrtc(bool direction)
    {
        using var fixture = new ProtectedTextModeFixture(true, direction: direction);
        fixture.SetGuestTextState(columns: 40);
        Machine machine = fixture.Machine;
        machine.Cpu.State.Dx = 0x0513;
        RunInt10(machine, 0x0200);
        Assert.Equal((ushort)0x0513, fixture.PrivateBdaWord(0x450));
        Assert.Equal((ushort)219, fixture.Shadow.CursorAddress);

        machine.Cpu.State.Dx = 0;
        RunInt10(machine, 0x0300);
        Assert.Equal((ushort)0x0513, machine.Cpu.State.Dx);
        Assert.Equal((ushort)0x0607, machine.Cpu.State.Cx);

        fixture.Shadow.WriteByte(0x3D4, 0x0C);
        fixture.Shadow.WriteByte(0x3D5, 0);
        fixture.Shadow.WriteByte(0x3D4, 0x0D);
        fixture.Shadow.WriteByte(0x3D5, 43);
        fixture.SetGuestVideoWord(524, 0x1E41);
        RunInt10(machine, 0x0800);
        Assert.Equal((ushort)0x1E41, machine.Cpu.State.Ax);
        Assert.Equal(0xB2C3D4E5u, machine.Cpu.State.Ebx);
        Assert.Equal(0xE5F60718u, machine.Cpu.State.Esi);
        Assert.Equal(0xF6071829u, machine.Cpu.State.Edi);
        Assert.Equal(0x0718293Au, machine.Cpu.State.Ebp);
        Assert.Equal(direction, machine.Cpu.Flags.Direction);
        fixture.AssertPhysicalDesktopUnchanged();
    }

    [Fact]
    public void ATeletypeCharacterUsesTheGuestVideoPageAndGeometry()
    {
        using var fixture = new ProtectedTextModeFixture(true, 0x0E76);
        fixture.SetGuestTextState(0x0513);
        fixture.SetGuestVideoWord(0x346, 0x0620);
        fixture.Machine.Cpu.State.Bx = 7;
        RunInt10(fixture.Machine, 0x0E76);

        Assert.Equal((ushort)0x0676, fixture.PrivateVideoWord(0x346));
        Assert.Equal((ushort)0x0514, fixture.PrivateBdaWord(0x450));
        fixture.AssertPhysicalDesktopUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATextCharacterFaultCanRetryWithoutAdvancingTheCursorTwice(bool direction)
    {
        using var fixture = new ProtectedTextModeFixture(false, 0x0E76, direction);
        fixture.SetGuestTextState(0x0513);
        fixture.SetGuestVideoWord(0x346, 0x0620);
        Machine machine = fixture.Machine;
        (byte Vector, uint Error, uint Ip)? fault = null;
        machine.Cpu.ProcessorException += (vector, error, ip, _) => fault = (vector, error, ip);
        for (int instruction = 0; instruction < 2000 && fault is null; instruction++)
        {
            machine.Cpu.Step();
        }

        Assert.Equal((byte)14, fault?.Vector);
        Assert.Equal(2u, fault?.Error);
        Assert.Equal(0xB8346u, machine.Cpu.State.Cr2);
        Assert.Equal((byte)0x26, machine.Cpu.PeekLinear(0xF0000 + fault!.Value.Ip));
        Assert.Equal((byte)0x88, machine.Cpu.PeekLinear(0xF0001 + fault.Value.Ip));
        Assert.Equal((ushort)0x0513, fixture.PrivateBdaWord(0x450));
        Assert.Equal((ushort)0x0620, fixture.PrivateVideoWord(0x346));
        fixture.AssertPhysicalDesktopUnchanged();

        fixture.MapVideoPage(0xB8, true);
        fixture.InstallFaultReturn();
        ContinueToHalt(machine);
        Assert.Equal((ushort)0x0676, fixture.PrivateVideoWord(0x346));
        Assert.Equal((ushort)0x0514, fixture.PrivateBdaWord(0x450));
        AssertTextCallerPreserved(machine, 0x0E76, direction);
        fixture.AssertPhysicalDesktopUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATextScrollRetriesInGuestMemoryAndPreservesAttributes(bool direction)
    {
        using var fixture = new ProtectedTextModeFixture(true, 0x0E0A, direction);
        fixture.SetGuestTextState(0x1800);
        fixture.MapVideoPage(0xB9, false);
        fixture.Shadow.WriteByte(0x3D4, 0x0C);
        fixture.Shadow.WriteByte(0x3D5, 1);                 // Display start = 256 cells
        fixture.Shadow.WriteByte(0x3D4, 0x0D);
        fixture.Shadow.WriteByte(0x3D5, 0);
        const uint screenStart = 512;
        fixture.SetGuestVideoWord(screenStart - 2, 0xCAFE);
        fixture.SetGuestVideoWord(screenStart + 4000, 0xBEEF);
        for (uint row = 0; row < 25; row++)
        {
            for (uint column = 0; column < 80; column++)
            {
                fixture.SetGuestVideoWord(screenStart + (row * 80 + column) * 2,
                    (ushort)(0x1000 + row * 256 + column));
            }
        }

        Machine machine = fixture.Machine;
        (byte Vector, uint Error, uint Ip)? fault = null;
        machine.Cpu.ProcessorException += (vector, error, ip, _) => fault = (vector, error, ip);
        for (int instruction = 0; instruction < 5000 && fault is null; instruction++)
        {
            machine.Cpu.Step();
        }

        Assert.Equal((byte)14, fault?.Vector);
        Assert.Equal(0u, fault?.Error);                    // Missing MOVSW source page
        Assert.Equal(0xB9000u, machine.Cpu.State.Cr2);
        Assert.Equal((byte)0xF3, machine.Cpu.PeekLinear(0xF0000 + fault!.Value.Ip));
        Assert.Equal((byte)0xA5, machine.Cpu.PeekLinear(0xF0001 + fault.Value.Ip));
        Assert.Equal((ushort)0x1100, fixture.PrivateVideoWord(screenStart));
        Assert.Equal((ushort)0x1800, fixture.PrivateBdaWord(0x450));
        fixture.MapVideoPage(0xB9, true);
        fixture.InstallFaultReturn();
        ContinueToHalt(machine);

        for (uint row = 0; row < 25; row++)
        {
            for (uint column = 0; column < 80; column++)
            {
                ushort expected = row == 24 ? (ushort)0x0720 : (ushort)(0x1100 + row * 256 + column);
                Assert.Equal(expected, fixture.PrivateVideoWord(screenStart + (row * 80 + column) * 2));
            }
        }

        Assert.Equal((ushort)0xCAFE, fixture.PrivateVideoWord(screenStart - 2));
        Assert.Equal((ushort)0xBEEF, fixture.PrivateVideoWord(screenStart + 4000));
        Assert.Equal((ushort)0x1800, fixture.PrivateBdaWord(0x450));
        AssertTextCallerPreserved(machine, 0x0E0A, direction);
        fixture.AssertPhysicalDesktopUnchanged();
    }

    [Theory]
    [InlineData(0x004F, 0x41, 0x0050)]
    [InlineData(0x0050, 0x41, 0x0101)]
    [InlineData(0x0050, 0x0D, 0x0000)]
    [InlineData(0x0050, 0x0A, 0x0150)]
    [InlineData(0x0050, 0x08, 0x004F)]
    [InlineData(0x0000, 0x08, 0x0000)]
    [InlineData(0x0050, 0x07, 0x0050)]
    public void TextTeletypeControlsKeepTheExistingWrapBehaviour(int cursor, int character, int expected)
    {
        using var fixture = new ProtectedTextModeFixture(true);
        fixture.SetGuestTextState((ushort)cursor);
        RunInt10(fixture.Machine, (ushort)(0x0E00 | character));
        Assert.Equal((ushort)expected, fixture.PrivateBdaWord(0x450));
        fixture.AssertPhysicalDesktopUnchanged();

        using var forwarded = new Machine();
        forwarded.RunInstructions(3000);
        RunInt10(forwarded, 0x0003);
        forwarded.Cpu.State.Dx = (ushort)cursor;
        RunInt10(forwarded, 0x0200);
        RunInt10(forwarded, (ushort)(0x0E00 | character));
        Assert.Equal((ushort)expected, forwarded.Memory.ReadWord(0x450));
    }

    private static void ContinueToHalt(Machine machine)
    {
        for (int instruction = 0; instruction < 20_000 && !machine.Cpu.State.Halted; instruction++)
        {
            machine.Cpu.Step();
        }

        Assert.True(machine.Cpu.State.Halted);
        Assert.Equal((ushort)0x2000, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x106u, machine.Cpu.State.Eip);
    }

    private static void AssertTextCallerPreserved(Machine machine, ushort ax, bool direction)
    {
        Assert.Equal(0xA1B20000u | ax, machine.Cpu.State.Eax);
        Assert.Equal(0xB2C3D4E5u, machine.Cpu.State.Ebx);
        Assert.Equal(0xC3D4E5F6u, machine.Cpu.State.Ecx);
        Assert.Equal(0xD4E5F607u, machine.Cpu.State.Edx);
        Assert.Equal(0xE5F60718u, machine.Cpu.State.Esi);
        Assert.Equal(0xF6071829u, machine.Cpu.State.Edi);
        Assert.Equal(0x0718293Au, machine.Cpu.State.Ebp);
        Assert.Equal((ushort)0x0040, machine.Cpu.State.Ds.Selector);
        Assert.Equal((ushort)0x3000, machine.Cpu.State.Es.Selector);
        Assert.Equal(direction, machine.Cpu.Flags.Direction);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0x13)]
    public void OtherGuestModesKeepTheNativeTextServiceFallback(byte mode)
    {
        using var fixture = new ProtectedTextModeFixture(true);
        fixture.SetGuestTextState(mode: mode);
        RunInt10(fixture.Machine, 0x0F00);
        Assert.Equal(fixture.Machine.Memory.ReadByte(0x449), fixture.Machine.Cpu.State.GetReg8(0));
        fixture.AssertPhysicalDesktopUnchanged();
    }

    [Fact]
    public void AVirtualizedTextModeSetLeavesThePhysicalDesktopUntouched()
    {
        using var fixture = new ProtectedTextModeFixture(videoPagePresent: true, direction: true);
        Machine machine = fixture.Machine;
        (uint eax, uint ebx, uint ecx, uint edx, uint esi, uint edi, uint ebp, uint esp, ushort cs, ushort ds, ushort es, ushort ss) caller =
            ((machine.Cpu.State.Eax & 0xFFFF0000) | 0x0003u, machine.Cpu.State.Ebx, machine.Cpu.State.Ecx, machine.Cpu.State.Edx,
             machine.Cpu.State.Esi, machine.Cpu.State.Edi, machine.Cpu.State.Ebp, machine.Cpu.State.Esp, machine.Cpu.State.Cs.Selector,
             machine.Cpu.State.Ds.Selector, machine.Cpu.State.Es.Selector, machine.Cpu.State.Ss.Selector);

        for (int instruction = 0; instruction < 30_000 && !machine.Cpu.State.Halted; instruction++)
        {
            machine.Cpu.Step();
        }

        Assert.True(machine.Cpu.State.Halted);
        Assert.Equal((ushort)0x2000, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x0106u, machine.Cpu.State.Eip);
        Assert.Equal((byte)0x03, fixture.PrivateBdaByte(0x449));
        Assert.Equal((ushort)80, fixture.PrivateBdaWord(0x44A));
        Assert.Equal((ushort)4000, fixture.PrivateBdaWord(0x44C));
        Assert.Equal((byte)24, fixture.PrivateBdaByte(0x484));
        Assert.Equal((ushort)0x0720, fixture.PrivateVideoWord(0));
        Assert.Equal((ushort)0x0720, fixture.PrivateVideoWord(3998));
        Assert.Equal((byte)0xA5, fixture.PrivateVideoByte(4000));
        Assert.True(fixture.PhysicalRegistersBefore.SequenceEqual(machine.VideoRegisters.CaptureRegisters()));
        Assert.True(fixture.PhysicalVideoBefore.SequenceEqual(machine.VideoMemory.CaptureMemory()));
        Assert.True(fixture.PhysicalBdaBefore.SequenceEqual(Enumerable.Range(0, 0x100)
            .Select(index => machine.Memory.ReadByte(0x400u + (uint)index))));
        Assert.Equal(0x101, machine.VideoRegisters.VesaMode!.Value.Number);
        Assert.Equal(3, machine.VideoRegisters.VesaWindowBank);
        Assert.Equal(704, machine.VideoRegisters.VesaScanLineBytes);
        Assert.Equal(1024, machine.VideoRegisters.VesaDisplayStart);
        Assert.Equal(fixture.PhysicalVideoModeBefore, machine.VideoRegisters.VideoMode);
        Assert.Equal(fixture.PhysicalTextColumnsBefore, machine.VideoRegisters.TextColumns);
        Assert.Equal(fixture.PhysicalTextRowsBefore, machine.VideoRegisters.TextRows);
        Assert.Equal(fixture.PhysicalCharacterHeightBefore, machine.VideoRegisters.CharacterHeight);
        Assert.Equal(caller, (machine.Cpu.State.Eax, machine.Cpu.State.Ebx, machine.Cpu.State.Ecx, machine.Cpu.State.Edx,
            machine.Cpu.State.Esi, machine.Cpu.State.Edi, machine.Cpu.State.Ebp, machine.Cpu.State.Esp, machine.Cpu.State.Cs.Selector,
            machine.Cpu.State.Ds.Selector, machine.Cpu.State.Es.Selector, machine.Cpu.State.Ss.Selector));
        Assert.True(machine.Cpu.Flags.Direction);
    }

    [Fact]
    public void PreservedFortyColumnTextModeUsesTheGuestCursorPosition()
    {
        using var fixture = new ProtectedTextModeFixture(videoPagePresent: true, requestedMode: 0x0080);
        Machine machine = fixture.Machine;
        machine.Memory.WriteByte(0x150450, 13);
        machine.Memory.WriteByte(0x150451, 5);

        for (int instruction = 0; instruction < 30_000 && !machine.Cpu.State.Halted; instruction++)
        {
            machine.Cpu.Step();
        }

        Assert.True(machine.Cpu.State.Halted);
        Assert.Equal((byte)0, fixture.PrivateBdaByte(0x449));
        Assert.Equal((byte)13, fixture.PrivateBdaByte(0x450));
        Assert.Equal((byte)5, fixture.PrivateBdaByte(0x451));
        Assert.Equal((byte)0, fixture.Shadow.Crtc(0x0E));
        Assert.Equal((byte)213, fixture.Shadow.Crtc(0x0F));
        Assert.Equal((byte)0xA5, fixture.PrivateVideoByte(0));
        Assert.True(fixture.PhysicalRegistersBefore.SequenceEqual(machine.VideoRegisters.CaptureRegisters()));
        Assert.True(fixture.PhysicalVideoBefore.SequenceEqual(machine.VideoMemory.CaptureMemory()));
        Assert.True(fixture.PhysicalBdaBefore.SequenceEqual(Enumerable.Range(0, 0x100)
            .Select(index => machine.Memory.ReadByte(0x400u + (uint)index))));
    }

    [Fact]
    public void ATextModeVideoPageFaultPointsAtTheRomStore()
    {
        using var fixture = new ProtectedTextModeFixture(videoPagePresent: false);
        Machine machine = fixture.Machine;
        (byte vector, uint error, ushort cs, uint ip, uint cr2, byte opcode, byte opcodeSecond)? fault = null;
        machine.Cpu.ProcessorException += (vector, error, offset, _) =>
        {
            CpuState state = machine.Cpu.State;
            fault = (vector, error, state.Cs.Selector, offset, state.Cr2,
                machine.Cpu.PeekLinear(state.Cs.Base + offset),
                machine.Cpu.PeekLinear(state.Cs.Base + offset + 1));
        };

        for (int instruction = 0; instruction < 30_000 && fault is null && !machine.Cpu.State.Halted; instruction++)
        {
            machine.Cpu.Step();
        }

        Assert.Equal((byte)0x0E, fault?.vector);
        Assert.Equal(2u, fault?.error);
        Assert.Equal((ushort)0xF000, fault?.cs);
        Assert.Equal(0x1991u, fault?.ip);
        Assert.Equal(0xB8000u, fault?.cr2);
        Assert.Equal((byte)0xF3, fault?.opcode);
        Assert.Equal((byte)0xAB, fault?.opcodeSecond);
        Assert.Equal((ushort)0x2000, machine.Cpu.State.Cs.Selector);
        Assert.Equal(0x0200u, machine.Cpu.State.Eip);
        Assert.Equal((byte)0xA5, fixture.PrivateVideoByte(0));
        Assert.True(fixture.PhysicalRegistersBefore.SequenceEqual(machine.VideoRegisters.CaptureRegisters()));
        Assert.True(fixture.PhysicalVideoBefore.SequenceEqual(machine.VideoMemory.CaptureMemory()));
        Assert.True(fixture.PhysicalBdaBefore.SequenceEqual(Enumerable.Range(0, 0x100)
            .Select(index => machine.Memory.ReadByte(0x400u + (uint)index))));
    }

    [Fact]
    public void AForwardedTextModeSetReplacesThePhysicalVesaMode()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        SetVesaMode(machine);
        machine.VideoMemory[VgaMemory.TextWindowOffset] = (byte)'X';
        machine.VideoMemory[VgaMemory.TextWindowOffset + 1] = 0x4E;

        RunInt10(machine, 0x0003);

        Assert.Null(machine.VideoRegisters.VesaMode);
        Assert.Equal(0x03, machine.VideoRegisters.VideoMode);
        Assert.Equal((ushort)80, machine.Bda.VideoColumns);
        Assert.Equal((ushort)4000, machine.Bda.VideoPageSize);
        Assert.Equal((byte)24, machine.Bda.VideoRowsMinusOne);
        Assert.Equal((byte)' ', machine.VideoMemory[VgaMemory.TextWindowOffset]);
        Assert.Equal((byte)0x07, machine.VideoMemory[VgaMemory.TextWindowOffset + 1]);
        Assert.Equal((byte)0x2A, machine.VideoRegisters.GetRawPaletteEntry(1).B);
    }

    [Theory]
    [InlineData(0x00, 40, 0xB8000u, 1000)]
    [InlineData(0x01, 40, 0xB8000u, 1000)]
    [InlineData(0x02, 80, 0xB8000u, 2000)]
    [InlineData(0x03, 80, 0xB8000u, 2000)]
    public void TextModesClearOnlyTheirInitialPage(int mode, int columns, uint textBase, int cells)
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        machine.VideoServices.SetVideoMode((byte)mode);
        machine.Memory.WriteByte(textBase + (uint)(cells * 2), 0xA5);
        machine.Cpu.Flags.Direction = true;

        RunInt10(machine, (ushort)mode);

        Assert.Equal((ushort)columns, machine.Bda.VideoColumns);
        Assert.Equal((byte)24, machine.Bda.VideoRowsMinusOne);
        Assert.Equal(0xA5, machine.Memory.ReadByte(textBase + (uint)(cells * 2)));
        Assert.True(machine.Cpu.Flags.Direction);
    }

    [Fact]
    public void TextModePreserveScreenLeavesTheCursorAndPageUntouched()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Bda.CursorColumn = 13;
        machine.Bda.CursorRow = 5;
        machine.VideoMemory[VgaMemory.TextWindowOffset] = (byte)'P';
        machine.VideoRegisters.SetPaletteEntry(1, 0, 0, 0);

        RunInt10(machine, 0x0083);

        Assert.Equal((byte)'P', machine.VideoMemory[VgaMemory.TextWindowOffset]);
        Assert.Equal((byte)13, machine.Bda.CursorColumn);
        Assert.Equal((byte)5, machine.Bda.CursorRow);
        Assert.Equal((byte)0x2A, machine.VideoRegisters.GetRawPaletteEntry(1).B);
        Assert.Equal((byte)0x01, machine.VideoRegisters.Crtc(0x0E));
        Assert.Equal((byte)0x9D, machine.VideoRegisters.Crtc(0x0F));
    }

    [Fact]
    public void MonochromeModeKeepsItsExistingNativeCompletion()
    {
        using var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Bda.CursorColumn = 4;
        machine.Bda.CursorRow = 3;

        RunInt10(machine, 0x0007);

        Assert.Equal((byte)0x07, machine.Bda.VideoMode);
        Assert.Equal((ushort)80, machine.Bda.VideoColumns);
        Assert.Equal((byte)24, machine.Bda.VideoRowsMinusOne);
        Assert.Equal((byte)' ', machine.VideoMemory[VgaMemory.TextWindowOffset]);
        Assert.Equal((byte)0x07, machine.VideoRegisters.VideoMode);
    }
}
