using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Cards;
using Aurora86.Core.Devices.Interrupts;
using Aurora86.Core.Devices.Pci;
using Aurora86.Core.IO;
using Aurora86.Core.Memory;

namespace Aurora86.Tests;

/// <summary>
/// F1.5: BARs that really move, command-register decode gating, bus-master
/// gating, the Interrupt Line written by the BIOS, and the bus-master DMA
/// memory interface. See <c>plans/specs/pci-piix4.md</c> sections "Today"
/// and 1, whose numbered test cases this covers (1-4, 10) alongside the
/// sizing probe, restore, and the Interrupt Line write the same section
/// asks for.
/// </summary>
public class PciBarTests
{
    private sealed class FakeMemoryDevice : IMemoryDevice
    {
        public byte Value = 0x42;
        public byte ReadByte(uint offset) => Value;
        public void WriteByte(uint offset, byte value) => Value = value;
    }

    private sealed class FakeIoDevice : IIoDevice
    {
        public byte Value = 0x99;
        public byte ReadByte(ushort port) => Value;
        public void WriteByte(ushort port, byte value) => Value = value;
    }

    // ---------------------------------------------------------- test case 1

    [Fact]
    public void MemoryBarSizingReturnsTheMask()
    {
        var function = new PciFunction(0, 0x1234, 0x5678, 0x030000, commandMask: 2);
        function.DefineBar(0, 0x10000, false, 0x180000);
        using var memory = new PhysicalMemoryBus(0x100000, 0x200000);
        memory.A20Enabled = true;   // this test is about BAR decode, not the 8086 wrap
        var window = new PciMemoryBarWindow(function, 0, 0x10000, memory, new FakeMemoryDevice());

        function.Write(0x10, 4, uint.MaxValue);
        uint sized = function.Read(0x10, 4);
        uint size = ~(sized & 0xFFFFFFF0u) + 1;

        Assert.Equal(0x10000u, size);
        Assert.Null(window.MappedBase);   // the probe itself must not decode
    }

    // ---------------------------------------------------------- test case 2

    [Fact]
    public void MemoryDecodeOffReadsFFhOnRestoresTheDeviceAndTheBarStaysUnchanged()
    {
        var function = new PciFunction(0, 0x1234, 0x5678, 0x030000, commandMask: 2);
        function.DefineBar(0, 0x10000, false, 0x180000);
        using var memory = new PhysicalMemoryBus(0x100000, 0x200000);
        memory.A20Enabled = true;   // this test is about BAR decode, not the 8086 wrap
        var device = new FakeMemoryDevice();
        var window = new PciMemoryBarWindow(function, 0, 0x10000, memory, device);

        // Decode starts off, as every PCI function's command register does
        // at reset: the aperture reads as an empty bus.
        Assert.Null(window.MappedBase);
        Assert.Equal(0xFF, memory.ReadByte(0x180000));

        function.Write(4, 2, 2);   // set the memory-space bit
        Assert.Equal(0x180000u, window.MappedBase);
        Assert.Equal(device.Value, memory.ReadByte(0x180000));

        function.Write(4, 2, 0);   // clear it again
        Assert.Null(window.MappedBase);
        Assert.Equal(0xFF, memory.ReadByte(0x180000));

        // Toggling the command register never touched the base itself.
        Assert.Equal(0x180000u, function.Read(0x10, 4));
    }

    // ---------------------------------------------------------- test case 3

    [Fact]
    public void AMovedIoBarAnswersOnlyAtTheNewBaseNeverBoth()
    {
        var function = new PciFunction(0, 0x1234, 0x5678, 0x0C0300, commandMask: 1);
        function.DefineBar(0, 32, true, 0xC100);
        var io = new IoBus();
        var device = new FakeIoDevice();
        var window = new PciIoBarWindow(function, 0, 32, io, device);

        function.Write(4, 2, 1);   // enable I/O decode at the default base
        Assert.Equal((ushort?)0xC100, window.MappedBase);
        Assert.Equal(device.Value, io.ReadByte(0xC100));

        function.Write(0x10, 4, 0xC400);   // move it, decode left on throughout
        Assert.Equal((ushort?)0xC400, window.MappedBase);
        Assert.Equal(device.Value, io.ReadByte(0xC400));
        Assert.Equal(0xFF, io.ReadByte(0xC100));   // the old base is empty again, not doubled up
    }

    /// <summary>The same relocation as above, on the real UHCI I/O BAR the PIIX fits.</summary>
    [Fact]
    public void AMovedUhciIoBarAnswersOnlyAtTheNewBase()
    {
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "uhci-piix" } };
        using var m = new Machine(4 * 1024 * 1024, hardware);
        m.RunInstructions(3000);

        m.Pci.WriteConfiguration(0x10, 4, 2, 1);
        Assert.Equal(0x20, m.Io.ReadWord(0xC102));   // the status register, at the default base

        m.Pci.WriteConfiguration(0x10, 0x20, 4, 0xC400);
        Assert.Equal(0x20, m.Io.ReadWord(0xC402));
        Assert.Equal(0xFFFF, m.Io.ReadWord(0xC102));
    }

    // --------------------------------------------------------- sizing probe

    [Fact]
    public void ABarHoldingTheSizingProbeDecodesNothingEvenIfDecodeWasLeftOn()
    {
        var function = new PciFunction(0, 0x1234, 0x5678, 0x0C0300, commandMask: 1);
        function.DefineBar(0, 32, true, 0xC100);
        var io = new IoBus();
        var device = new FakeIoDevice();
        var window = new PciIoBarWindow(function, 0, 32, io, device);
        function.Write(4, 2, 1);
        Assert.Equal((ushort?)0xC100, window.MappedBase);

        // Software is expected to disable decode before sizing, but nothing
        // enforces that; this machine must not decode at the probe value
        // even when a guest left the command register's bit alone.
        function.Write(0x10, 4, uint.MaxValue);

        Assert.Null(window.MappedBase);
        Assert.Equal(0xFF, io.ReadByte(0xC100));   // the old base stopped answering too
    }

    // -------------------------------------------------- test case 4 and BIOS

    [Fact]
    public void PirqRoutesToIrq11WritesTheInterruptLineThenADisallowedIrqIsRefusedAndLeavesItAlone()
    {
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "uhci-piix" } };
        using var m = new Machine(4 * 1024 * 1024, hardware);
        m.RunInstructions(3000);
        m.Cpu.SetSegment(SegReg.Cs, 0);
        m.Cpu.SetSegment(SegReg.Ss, 0);

        static void Run(Machine machine, params byte[] code)
        {
            for (int i = 0; i < code.Length; i++) machine.Memory.WriteByte(0x2000u + (uint)i, code[i]);
            machine.Cpu.State.Eip = 0x2000;
            machine.Cpu.State.Esp = 0x7000;
            machine.Cpu.State.Halted = false;
            for (int i = 0; i < 200 && !machine.Cpu.State.Halted; i++) machine.Cpu.Step();
            Assert.True(machine.Cpu.State.Halted);
        }

        void Bios(ushort function)
        {
            m.Cpu.State.Ax = function;
            Run(m, 0xCD, 0x1A, 0xF4);
        }

        // The storage controller (device 0x18) already has a POST default
        // route (link B -> IRQ 10, see the next test): routing it again here,
        // to a different allowed IRQ, is what tells apart the BIOS call's own
        // write to 3Ch from the one POST already made.
        int link = PciInterruptRouter.Link(0x18, 1);
        int linkRegister = 0x60 + link;

        // BX addresses the storage UHCI function, CL selects its pin (INTA,
        // 0Ah), CH carries the IRQ to route.
        m.Cpu.State.Bx = 0x18;
        m.Cpu.State.Cx = 0x050A;
        Bios(0xB10F);
        Assert.False(m.Cpu.Flags.Carry);
        Assert.Equal(5u, m.Pci.ReadConfiguration(8, linkRegister, 1));   // the link now routed to IRQ 5
        Assert.Equal(5u, m.Pci.ReadConfiguration(0x18, 0x3C, 1));        // and the function told about it

        m.Cpu.State.Bx = 0x18;
        m.Cpu.State.Cx = 0x080A;   // IRQ 8 is reserved: PciInterruptRouter.AllowedIrqs excludes it
        Bios(0xB10F);
        Assert.True(m.Cpu.Flags.Carry);
        Assert.Equal(0x88, m.Cpu.State.GetReg8(4));
        Assert.Equal(5u, m.Pci.ReadConfiguration(0x18, 0x3C, 1));   // refused: the line is left alone
    }

    [Fact]
    public void PostRoutesEveryFunctionWithAnInterruptPinAndWritesItsInterruptLine()
    {
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "uhci-piix" } };
        using var m = new Machine(4 * 1024 * 1024, hardware);

        // The very first POST, run by the reset vector once instructions
        // start executing -- before any guest ever calls INT 1Ah function
        // 0Fh, exactly the guest that never asks and still needs a working
        // interrupt.
        m.RunInstructions(3000);

        uint keyboardMouseIrq = m.Pci.ReadConfiguration(0x10, 0x3C, 1);
        uint storageIrq = m.Pci.ReadConfiguration(0x18, 0x3C, 1);
        Assert.NotEqual(0xFFu, keyboardMouseIrq);   // routed, not left at the reset "unknown" value
        Assert.NotEqual(0xFFu, storageIrq);
        Assert.NotEqual(keyboardMouseIrq, storageIrq);   // the two controllers share no PIRQ link

        // The bridge's own link registers (60h-63h) name the same IRQs.
        int keyboardMouseLink = PciInterruptRouter.Link(0x10, 1);
        int storageLink = PciInterruptRouter.Link(0x18, 1);
        Assert.Equal(keyboardMouseIrq, m.Pci.ReadConfiguration(8, 0x60 + keyboardMouseLink, 1));
        Assert.Equal(storageIrq, m.Pci.ReadConfiguration(8, 0x60 + storageLink, 1));

        // A function with no interrupt pin -- the host bridge -- is left at
        // its reset value and never touched by the router.
        Assert.Equal(0u, m.Pci.ReadConfiguration(0, 0x3C, 1));
    }

    // --------------------------------------------------------------- restore

    [Fact]
    public void RestoringASnapshotReappliesTheMovedBarAndTheCommandBit()
    {
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "uhci-piix" } };
        using var m = new Machine(4 * 1024 * 1024, hardware);
        m.RunInstructions(3000);

        m.Pci.WriteConfiguration(0x10, 0x20, 4, 0xC400);
        m.Pci.WriteConfiguration(0x10, 4, 2, 5);
        Assert.Equal(0x20, m.Io.ReadWord(0xC402));
        Assert.Equal(0xFFFF, m.Io.ReadWord(0xC102));

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(m).Save(path);

            // Move it again and disable decode, so the restore has to undo both.
            m.Pci.WriteConfiguration(0x10, 4, 2, 0);
            m.Pci.WriteConfiguration(0x10, 0x20, 4, 0xC600);
            Assert.Equal(0xFFFF, m.Io.ReadWord(0xC402));

            MachineSnapshot.Load(path).RestoreTo(m);

            Assert.Equal(0x20, m.Io.ReadWord(0xC402));    // the captured window answers again
            Assert.Equal(0xFFFF, m.Io.ReadWord(0xC602));  // the base only set after capture does not
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ S3

    /// <summary>
    /// The Trio64V+'s linear framebuffer BAR sizes honestly (see
    /// <c>PciTests.TheFramebufferApertureSizesToTheInstalledVideoMemory</c>)
    /// but does not move. Its command register's I/O and memory bits are
    /// writable as on the chip and read as enabled after POST (see
    /// <c>PciTests.TheDisplayAdapterReportsEnabledDecodersAfterPost</c>);
    /// the decoders themselves stay statically mapped whatever they hold.
    /// </summary>
    [Fact]
    public void TheDisplayAdapterBarStaysFixedAndDoesNotMove()
    {
        using var m = new Machine();
        m.RunInstructions(100);

        Assert.Equal(3u, m.Pci.ReadConfiguration(0x20, 4, 2) & 3);

        uint original = m.Pci.ReadConfiguration(0x20, 0x10, 4);

        // Flip the aperture base's own only set bit -- safely inside the
        // BAR's writable mask, since it is what sizing found the base
        // aligned to -- landing the "moved to" address at physical zero,
        // ordinary conventional RAM nowhere near the real aperture.
        uint attempted = original ^ m.LinearFrameBufferBase;
        m.Pci.WriteConfiguration(0x20, 0x10, 4, attempted);
        Assert.Equal(attempted, m.Pci.ReadConfiguration(0x20, 0x10, 4));   // accepted into configuration space...

        uint wouldHaveMovedTo = attempted & 0xFFFFFFF0u;
        m.Memory.WriteByte(wouldHaveMovedTo, 0x11);
        Assert.Equal(0x11, m.Memory.ReadByte(wouldHaveMovedTo));   // ...but is still plain, writable RAM

        m.Memory.WriteByte(m.LinearFrameBufferBase, 0x33);
        Assert.Equal(0x33, m.Memory.ReadByte(m.LinearFrameBufferBase));   // the real aperture, unmoved

        m.Pci.WriteConfiguration(0x20, 0x10, 4, original);
    }

    // ------------------------------------------------------------------ DMA

    [Fact]
    public void ABusMasterDmaWriteIntoRamIsVisibleToTheCpuAndRaisesTheWriteHook()
    {
        using var m = new Machine();
        m.RunInstructions(3000);
        m.Cpu.SetSegment(SegReg.Cs, 0);
        m.Cpu.SetSegment(SegReg.Ss, 0);

        IDmaMemory? dma = m.Pci.DmaMemory;
        Assert.NotNull(dma);

        var notifications = new List<(uint Address, uint Length)>();
        dma.Written += (address, length) => notifications.Add((address, length));

        const uint bufferAddress = 0x50000;
        dma.WriteByte(bufferAddress, 0x7A);

        Assert.Equal([(bufferAddress, 1u)], notifications);
        Assert.Equal(0x7A, m.Memory.ReadByte(bufferAddress));   // the memory bus sees it directly

        // MOV AX,5000h / MOV DS,AX / MOV AL,[0000h] / HLT -- the CPU reads
        // the same byte the "device" just placed in RAM without its help.
        byte[] code = [0xB8, 0x00, 0x50, 0x8E, 0xD8, 0xA0, 0x00, 0x00, 0xF4];
        for (int i = 0; i < code.Length; i++) m.Memory.WriteByte(0x2000u + (uint)i, code[i]);
        m.Cpu.State.Eip = 0x2000;
        m.Cpu.State.Esp = 0x7000;
        m.Cpu.State.Halted = false;
        for (int i = 0; i < 200 && !m.Cpu.State.Halted; i++) m.Cpu.Step();

        Assert.True(m.Cpu.State.Halted);
        Assert.Equal(0x7Au, m.Cpu.State.Eax & 0xFF);
    }

    [Fact]
    public void DmaMemoryReadsAndWritesOutsideInstalledRamDecodeNothing()
    {
        using var m = new Machine(ramSize: 4 * 1024 * 1024);
        IDmaMemory dma = m.Pci.DmaMemory!;

        bool written = false;
        dma.Written += (_, _) => written = true;

        uint pastRam = m.Memory.RamSize;   // one byte past the installed RAM
        Assert.Equal(0xFF, dma.ReadByte(pastRam));
        Assert.Equal(0xFFFF, dma.ReadWord(pastRam));
        Assert.Equal(uint.MaxValue, dma.ReadDword(pastRam));

        dma.WriteByte(pastRam, 0x11);
        Assert.False(written);
    }

    // ------------------------------------------------------ review findings

    [Fact]
    public void AnIoBarMovedOntoTheConfigurationPortsNeverTakesThemAndTheyStillWorkAfterward()
    {
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "uhci-piix" } };
        using var m = new Machine(4 * 1024 * 1024, hardware);
        m.RunInstructions(3000);

        // CE0h-CFFh covers configuration mechanism 1 at CF8h-CFFh.
        m.Pci.WriteConfiguration(0x10, 0x20, 4, 0xCE0);
        Assert.Equal(0x70208086u, ReadThroughMechanismOne(m, 0x10, 0));

        // Moving it away again leaves the ports with their owner.
        m.Pci.WriteConfiguration(0x10, 0x20, 4, 0xC400);
        Assert.Equal(0x70208086u, ReadThroughMechanismOne(m, 0x10, 0));
        Assert.Equal(0x20, m.Io.ReadWord(0xC402));
    }

    [Fact]
    public void AnIoBarAtZeroDecodesNothingAndTheDmaControllerKeepsItsPorts()
    {
        var io = new IoBus();
        var motherboard = new FakeIoDevice();
        io.Register(0, 0x20, motherboard);
        var function = new PciFunction(0x10, 0x8086, 0x7020, 0x0C0300, commandMask: 1);
        function.DefineBar(4, 32, true, 0xC100);
        var card = new FakeIoDevice { Value = 0x42 };
        var window = new PciIoBarWindow(function, 4, 32, io, card);
        function.Write(4, 2, 1);
        Assert.Equal((ushort)0xC100, window.MappedBase);

        function.Write(0x20, 4, 0);   // an unassigned BAR, on top of the DMA controller's ports
        Assert.Null(window.MappedBase);
        Assert.Equal(motherboard.Value, io.ReadByte(0));

        function.Write(0x20, 4, 0xC100);
        Assert.Equal(card.Value, io.ReadByte(0xC100));
        Assert.Equal(motherboard.Value, io.ReadByte(0));   // still the motherboard's after the move back
    }

    [Fact]
    public void AMemoryBarOverRamIsRefusedAndRamIsNotLost()
    {
        var function = new PciFunction(0, 0x1234, 0x5678, 0x030000, commandMask: 2);
        function.DefineBar(0, 0x10000, false, 0x180000);
        using var memory = new PhysicalMemoryBus(0x100000, 0x200000);
        memory.A20Enabled = true;
        var window = new PciMemoryBarWindow(function, 0, 0x10000, memory, new FakeMemoryDevice());
        memory.WriteByte(0x50000, 0x5A);

        function.Write(4, 2, 2);
        function.Write(0x10, 4, 0x50000);
        Assert.Null(window.MappedBase);
        Assert.Equal(0x5A, memory.ReadByte(0x50000));

        function.Write(0x10, 4, 0x180000);
        Assert.Equal(0x180000u, window.MappedBase);
        Assert.Equal(0x5A, memory.ReadByte(0x50000));
    }

    [Fact]
    public void BusMasterDmaIgnoresA20()
    {
        using var m = new Machine(ramSize: 4 * 1024 * 1024);
        m.Memory.A20Enabled = false;
        IDmaMemory dma = m.Pci.DmaMemory!;

        m.Memory.WriteByte(0x050000, 0x00);
        dma.WriteByte(0x150000, 0xA5);

        Assert.Equal(0x00, m.Memory.ReadByte(0x050000));   // conventional memory untouched
        Assert.Equal(0xA5, dma.ReadByte(0x150000));
        m.Memory.A20Enabled = true;
        Assert.Equal(0xA5, m.Memory.ReadByte(0x150000));
    }

    [Fact]
    public void PostMakesTheRoutedIrqsLevelTriggered()
    {
        var hardware = new HardwareConfiguration { Usb = new CardSettings { Card = "uhci-piix" } };
        using var m = new Machine(4 * 1024 * 1024, hardware);
        m.RunInstructions(3000);

        byte elcr2 = m.Io.ReadByte(0x4D1);
        Assert.NotEqual(0, elcr2 & (1 << (11 - 8)));   // link A, the keyboard and mouse UHCI
        Assert.NotEqual(0, elcr2 & (1 << (10 - 8)));   // link B, the storage UHCI
    }

    private static uint ReadThroughMechanismOne(Machine m, int deviceFunction, int offset)
    {
        m.Io.WriteDword(0xCF8, 0x80000000u | ((uint)deviceFunction << 8) | (uint)offset);
        return m.Io.ReadDword(0xCFC);
    }
}
