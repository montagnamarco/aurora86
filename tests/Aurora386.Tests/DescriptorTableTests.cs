using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Diagnostics;

namespace Aurora386.Tests;

/// <summary>
/// The decoder for the tables that define protected mode. Its whole job is
/// putting back together fields the 286 scattered for reasons of packaging:
/// a base split across three places, a limit across two, and a meaning that
/// changes with one bit of the access byte. Every test here builds a
/// descriptor the way the hardware expects to find one and checks that the
/// decoder reads back what was written.
/// </summary>
public class DescriptorTableTests
{
    private const uint GdtBase = 0x00040000;
    private const uint IdtBase = 0x00041000;
    private const uint TssBase = 0x00042000;

    private static void WriteDescriptor(Machine machine, uint tableBase, int index,
                                        uint baseAddress, uint limit, byte access,
                                        byte granularity = 0x40)
    {
        uint address = tableBase + (uint)(index * 8);
        machine.Memory.WriteWord(address, (ushort)(limit & 0xFFFF));
        machine.Memory.WriteWord(address + 2, (ushort)(baseAddress & 0xFFFF));
        machine.Memory.WriteByte(address + 4, (byte)((baseAddress >> 16) & 0xFF));
        machine.Memory.WriteByte(address + 5, access);
        machine.Memory.WriteByte(address + 6, (byte)((granularity & 0xF0) | ((limit >> 16) & 0x0F)));
        machine.Memory.WriteByte(address + 7, (byte)((baseAddress >> 24) & 0xFF));
    }

    private static Machine WithTables()
    {
        var machine = new Machine();
        machine.RunInstructions(200);

        // A flat code segment at ring 0 and a data segment at ring 3, which
        // between them exercise both families and both privilege levels.
        WriteDescriptor(machine, GdtBase, 1, 0, 0xFFFFF, 0x9A, 0xC0);
        WriteDescriptor(machine, GdtBase, 2, 0x00120000, 0x0FFF, 0xF2, 0x40);

        machine.Cpu.State.GdtBase = GdtBase;
        machine.Cpu.State.GdtLimit = 0x17;          // three entries: null, 1, 2
        machine.Cpu.State.IdtBase = IdtBase;
        machine.Cpu.State.IdtLimit = 0x7FF;         // 256 gates of eight bytes
        return machine;
    }

    [Fact]
    public void ADescriptorsBaseAndLimitAreReassembledFromTheirScatteredFields()
    {
        using Machine machine = WithTables();
        var tables = new DescriptorTables(machine);

        DescriptorTables.Descriptor entry = tables.ReadGlobalTable()[2];

        Assert.Equal((ushort)0x10, entry.Selector);      // index 2, GDT, RPL 0
        Assert.Equal(0x00120000u, entry.Base);
        Assert.Equal(0x0FFFu, entry.Limit);              // byte granularity: as written
        Assert.Equal(3, entry.Dpl);
        Assert.True(entry.Present);
        Assert.False(entry.IsCode);
        Assert.Contains("data", entry.TypeName);
        Assert.Contains("writable", entry.TypeName);
    }

    [Fact]
    public void PageGranularityMultipliesTheLimitByTheSizeOfAPage()
    {
        // The granularity bit is the difference between a segment of a
        // megabyte and one of four gigabytes, and it is one bit in a byte
        // nobody reads twice.
        using Machine machine = WithTables();
        var tables = new DescriptorTables(machine);

        DescriptorTables.Descriptor entry = tables.ReadGlobalTable()[1];

        Assert.True(entry.Granular);
        Assert.Equal(0xFFFFFFFFu, entry.Limit);          // 0xFFFFF pages, minus one byte
        Assert.True(entry.IsCode);
        Assert.True(entry.Is32Bit);
    }

    [Fact]
    public void ASystemDescriptorIsNamedByItsTypeRatherThanItsFlags()
    {
        using Machine machine = WithTables();
        WriteDescriptor(machine, GdtBase, 3, TssBase, 0x67, 0x89, 0x00);   // 386 TSS, available
        machine.Cpu.State.GdtLimit = 0x1F;

        var tables = new DescriptorTables(machine);
        DescriptorTables.Descriptor entry = tables.ReadGlobalTable()[3];

        Assert.True(entry.IsSystem);
        Assert.Equal("386 TSS (available)", entry.TypeName);
    }

    [Fact]
    public void TheTableStopsAtItsLimitRatherThanReadingPastIt()
    {
        // The limit is the offset of the last valid byte, so three entries
        // means 0x17. Reading one more would hand back whatever memory holds.
        using Machine machine = WithTables();
        var tables = new DescriptorTables(machine);

        Assert.Equal(3, tables.ReadGlobalTable().Count);
    }

    [Fact]
    public void AnInterruptGateGivesUpItsSelectorOffsetAndPrivilege()
    {
        // The limit is the offset of the last valid byte, so a table with a
        // gate for vector 0x21 in it needs at least 0x21*8+7 = 0x10F. This is
        // the arithmetic a guest gets wrong and then wonders why its handler
        // is never called.
        using Machine machine = WithTables();

        // Vector 0x21: a 386 interrupt gate at 0008:00001234, reachable from
        // ring 3 -- the shape every DOS extender installs.
        machine.Memory.WriteWord(IdtBase + 0x21 * 8, 0x1234);
        machine.Memory.WriteWord(IdtBase + 0x21 * 8 + 2, 0x0008);
        machine.Memory.WriteByte(IdtBase + 0x21 * 8 + 5, 0xEE);
        machine.Memory.WriteWord(IdtBase + 0x21 * 8 + 6, 0x0000);

        var tables = new DescriptorTables(machine);
        DescriptorTables.Gate gate = tables.ReadInterruptTable()[0x21];

        Assert.Equal((ushort)0x0008, gate.Selector);
        Assert.Equal(0x1234u, gate.Offset);
        Assert.Equal(3, gate.Dpl);
        Assert.True(gate.Present);
        Assert.Equal("386 interrupt gate", gate.TypeName);
    }

    [Fact]
    public void TheRingZeroStackIsReadFromTheRightPlaceForEachKindOfTaskSegment()
    {
        // The 286 and 386 layouts put it in different places, and reading the
        // wrong pair gives a stack pointer made of two unrelated halves --
        // which is how a guest dies on its first fault rather than on the
        // instruction that was actually wrong.
        using Machine machine = WithTables();

        WriteDescriptor(machine, GdtBase, 3, TssBase, 0x67, 0x89, 0x00);   // 386 TSS
        machine.Cpu.State.GdtLimit = 0x1F;
        machine.Cpu.State.TaskRegister = 0x18;
        machine.Memory.WriteDword(TssBase + 4, 0x00009000);                // ESP0
        machine.Memory.WriteDword(TssBase + 8, 0x00000010);                // SS0

        var tables = new DescriptorTables(machine);
        DescriptorTables.TaskState state = Assert.NotNull(tables.ReadTaskState());

        Assert.True(state.Is386);
        Assert.Equal(0x9000u, state.Esp0);
        Assert.Equal((ushort)0x0010, state.Ss0);

        // The same segment declared as a 286 one takes the stack from the
        // sixteen-bit fields instead.
        WriteDescriptor(machine, GdtBase, 3, TssBase, 0x2B, 0x81, 0x00);   // 286 TSS
        machine.Memory.WriteWord(TssBase + 2, 0x7788);                     // SP0
        machine.Memory.WriteWord(TssBase + 4, 0x0028);                     // SS0

        state = Assert.NotNull(new DescriptorTables(machine).ReadTaskState());
        Assert.False(state.Is386);
        Assert.Equal(0x7788u, state.Esp0);
        Assert.Equal((ushort)0x0028, state.Ss0);
    }

    [Fact]
    public void WithNoTaskLoadedThereIsNoTaskStateToShow()
    {
        using Machine machine = WithTables();
        machine.Cpu.State.TaskRegister = 0;

        Assert.Null(new DescriptorTables(machine).ReadTaskState());
    }

    [Fact]
    public void ATranslationSaysWhereItStopsWhenThePageIsNotThere()
    {
        using Machine machine = WithTables();
        var tables = new DescriptorTables(machine);

        // Paging off: linear and physical are the same thing, and the decoder
        // says so rather than pretending to have walked tables.
        Assert.Contains("paging off", tables.DescribeTranslation(0x1234));

        machine.Memory.WriteDword(0x50000 + (0x00400000u >> 22) * 4, 0x51000 | 7);
        machine.Memory.WriteDword(0x51000 + ((0x00400000u >> 12) & 0x3FF) * 4, 0x00330000 | 7);
        machine.Cpu.State.Cr3 = 0x50000;
        machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;

        Assert.Contains("00330000", tables.DescribeTranslation(0x00400000));
        Assert.Contains("writable", tables.DescribeTranslation(0x00400000));
        Assert.Contains("not present", tables.DescribeTranslation(0x00500000));
    }
}
