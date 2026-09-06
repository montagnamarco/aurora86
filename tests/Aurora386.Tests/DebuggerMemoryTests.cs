using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Gui.Emulation;
using Aurora386.Gui.ViewModels;

namespace Aurora386.Tests;

/// <summary>
/// The debugger's memory editor: reading, patching and searching the guest's
/// memory while it is stopped.
///
/// The awkward part is not the hex parsing, it is which address space the
/// numbers are in. Physical and linear are the same thing until the guest
/// turns paging on; from that moment a pointer taken out of a Windows data
/// structure means nothing to physical memory, and the view has to walk the
/// page tables the same way the processor does -- without faulting, without
/// setting accessed bits, and without disturbing the TLB.
/// </summary>
public class DebuggerMemoryTests
{
    private static (EmulatorSession Session, DebuggerViewModel Model) Open()
    {
        var session = new EmulatorSession();
        session.WithMachine(machine => machine.RunInstructions(2000));   // POST
        return (session, new DebuggerViewModel(session));
    }

    [Fact]
    public void HexBytesAreWrittenWhereTheAddressSays()
    {
        (EmulatorSession session, DebuggerViewModel model) = Open();

        model.MemoryEditAddressText = "0800:0010";
        model.MemoryEditValueText = "B8 00 4C CD 21";
        model.WriteMemoryCommand.Execute(null);

        session.WithMachine(machine =>
        {
            Assert.Equal(0xB8, machine.Memory.ReadByte(0x8010));
            Assert.Equal(0x4C, machine.Memory.ReadByte(0x8012));
            Assert.Equal(0x21, machine.Memory.ReadByte(0x8014));
        });
    }

    [Fact]
    public void AQuotedStringIsWrittenAsItsCharacters()
    {
        (EmulatorSession session, DebuggerViewModel model) = Open();

        model.MemoryEditAddressText = "9000";
        model.MemoryEditValueText = "\"OK\"";
        model.WriteMemoryCommand.Execute(null);

        session.WithMachine(machine =>
        {
            Assert.Equal((byte)'O', machine.Memory.ReadByte(0x9000));
            Assert.Equal((byte)'K', machine.Memory.ReadByte(0x9001));
        });
    }

    [Fact]
    public void SearchFindsThePatternAndMovesTheView()
    {
        (EmulatorSession session, DebuggerViewModel model) = Open();

        session.WithMachine(machine =>
        {
            machine.Memory.WriteByte(0x12340, 0xDE);
            machine.Memory.WriteByte(0x12341, 0xAD);
            machine.Memory.WriteByte(0x12342, 0xBE);
        });

        model.MemoryAddressText = "10000";
        model.MemorySearchText = "DE AD BE";
        model.SearchMemoryCommand.Execute(null);

        Assert.Equal("012340", model.MemoryAddressText);
        Assert.Contains("Found", model.StatusText);
    }

    [Fact]
    public void ASearchThatFindsNothingLeavesTheViewAlone()
    {
        (_, DebuggerViewModel model) = Open();

        model.MemoryAddressText = "F0000";
        model.MemorySearchText = "\"a string no BIOS would contain: qzqzqz\"";
        model.SearchMemoryCommand.Execute(null);

        Assert.Equal("F0000", model.MemoryAddressText);
        Assert.Equal("Not found", model.StatusText);
    }

    [Fact]
    public void WithPagingOnTheViewFollowsTheGuestsTables()
    {
        (EmulatorSession session, DebuggerViewModel model) = Open();

        // One page mapped: linear 0x00400000 lands on physical 0x00330000.
        session.WithMachine(machine =>
        {
            machine.Memory.WriteDword(0x20000 + (0x00400000u >> 22) * 4, 0x21000 | 7);
            machine.Memory.WriteDword(0x21000 + ((0x00400000u >> 12) & 0x3FF) * 4, 0x00330000 | 7);
            machine.Cpu.State.Cr3 = 0x20000;
            machine.Cpu.State.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
            machine.Memory.WriteByte(0x00330042, 0x5A);
        });

        model.MemoryFollowsPaging = true;
        model.MemoryEditAddressText = "400043";
        model.MemoryEditValueText = "A5";
        model.WriteMemoryCommand.Execute(null);

        session.WithMachine(machine =>
        {
            // The write went through the tables, into the physical frame.
            Assert.Equal(0xA5, machine.Memory.ReadByte(0x00330043));
            // And the byte already there reads back through the same path.
            Assert.Equal(0x5A, machine.Memory.ReadByte(0x00330042));
        });
    }

    [Fact]
    public void NonsenseIsRefusedRatherThanWrittenSomewhereRandom()
    {
        (EmulatorSession session, DebuggerViewModel model) = Open();

        model.MemoryEditAddressText = "not an address";
        model.MemoryEditValueText = "B8";
        model.WriteMemoryCommand.Execute(null);
        Assert.Contains("Address", model.StatusText);

        model.MemoryEditAddressText = "9000";
        model.MemoryEditValueText = "B8 0";      // odd number of digits
        model.WriteMemoryCommand.Execute(null);
        Assert.Contains("Bytes", model.StatusText);

        session.WithMachine(machine => Assert.Equal(0x00, machine.Memory.ReadByte(0x9000)));
    }
}

/// <summary>
/// The debugger's disk editor, driven through the view model the window binds
/// to: navigating sectors, seeing what each one is, and patching one.
/// </summary>
public class DebuggerDiskTests
{
    private static (EmulatorSession Session, DebuggerViewModel Model, string Path) Open()
    {
        string path = Path.Combine(System.IO.Path.GetTempPath(), $"aurora-dbg-{Guid.NewGuid():N}.img");
        Aurora386.Core.Devices.Storage.HardDiskImage.Create(path, 8 * 1024 * 1024);

        var session = new EmulatorSession();
        session.WithMachine(machine =>
        {
            machine.RunInstructions(2000);
            machine.AttachDisk(Aurora386.Core.Devices.Storage.DiskImage.OpenHardDisk(path));
        });

        return (session, new DebuggerViewModel(session), path);
    }

    [Fact]
    public void TheFirstSectorIsRecognisedAsThePartitionTable()
    {
        (_, DebuggerViewModel model, _) = Open();

        model.DiskSectorText = "0";
        model.GoToSectorCommand.Execute(null);

        Assert.Contains(model.DiskStructure, line => line.Contains("master boot record"));
        Assert.Contains(model.DiskStructure, line => line.Contains("partition 1"));
        Assert.Equal(32, model.DiskLines.Count);                 // 512 bytes, 16 to a row
        Assert.Contains("55 AA", model.DiskLines[^1]);           // the signature that ends it
    }

    [Fact]
    public void MovingASectorForwardAndBackLandsWhereItStarted()
    {
        (_, DebuggerViewModel model, _) = Open();

        model.DiskSectorText = "63";
        model.GoToSectorCommand.Execute(null);
        string atSixtyThree = string.Join("", model.DiskLines);

        model.NextSectorCommand.Execute(null);
        Assert.Equal("64", model.DiskSectorText);

        model.PreviousSectorCommand.Execute(null);
        Assert.Equal("63", model.DiskSectorText);
        Assert.Equal(atSixtyThree, string.Join("", model.DiskLines));
    }

    [Fact]
    public void APatchedByteIsOnTheDiskAndBackOnTheScreen()
    {
        (EmulatorSession session, DebuggerViewModel model, _) = Open();

        model.DiskSectorText = "200";
        model.GoToSectorCommand.Execute(null);
        model.DiskEditOffsetText = "10";
        model.DiskEditValueText = "DE AD BE EF";
        model.WriteDiskCommand.Execute(null);

        Assert.Contains("Wrote 4", model.DiskStatusText);
        Assert.Contains("DE AD BE EF", model.DiskLines[1]);      // row 010

        // And it is really on the medium, not only in the view.
        session.WithMachine(machine =>
        {
            var inspector = new Aurora386.Core.Diagnostics.DiskInspector(
                machine.DiskServices.AttachedDrives.First());
            byte[] sector = inspector.ReadSector(200);
            Assert.Equal(0xDE, sector[0x10]);
            Assert.Equal(0xEF, sector[0x13]);
        });
    }

    [Fact]
    public void AnEditThatWouldRunPastTheSectorIsRefused()
    {
        (_, DebuggerViewModel model, _) = Open();

        model.DiskSectorText = "200";
        model.GoToSectorCommand.Execute(null);
        model.DiskEditOffsetText = "1FE";
        model.DiskEditValueText = "11 22 33 44";     // four bytes from 0x1FE overruns
        model.WriteDiskCommand.Execute(null);

        Assert.Contains("past the end", model.DiskStatusText);
    }

    [Fact]
    public void WithNoDiskTheViewSaysSoInsteadOfShowingNothing()
    {
        var session = new EmulatorSession();
        session.WithMachine(machine => machine.RunInstructions(2000));
        var model = new DebuggerViewModel(session);

        model.GoToSectorCommand.Execute(null);

        Assert.Contains(model.DiskStructure, line => line.Contains("no disk"));
        Assert.Empty(model.DiskLines);
    }
}
