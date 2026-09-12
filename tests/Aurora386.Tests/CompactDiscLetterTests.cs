using System.Text;
using Aurora386.Core;
using Aurora386.Core.Dos;
using Aurora386.Core.Devices.Storage;

namespace Aurora386.Tests;

/// <summary>
/// The CD-ROM's drive letter is the guest's to decide, and the emulator
/// learns it by asking through the MSCDEX installation check.
/// </summary>
public class CompactDiscLetterTests
{
    private static Machine Booted()
    {
        var machine = new Machine();
        machine.RunInstructions(3000);
        machine.Cpu.State.Halted = false;

        var bytes = new byte[100 * IsoImage.SectorSize];
        int pvd = IsoImage.PrimaryVolumeDescriptorSector * IsoImage.SectorSize;
        bytes[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(bytes, pvd + 1);
        machine.AttachCompactDisc(new IsoImage(new MemoryStream(bytes)));

        // Fake DOS structures, so the installer believes DOS is up and idle.
        machine.Redirector.AttachToDos(0x20000, 0x30000, 26);
        return machine;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoringNativeCdOwnershipPreservesTheGuestDriveFlags(bool throughFile)
    {
        using Machine source = Booted();
        source.UnmountRedirectedDrive(3);
        source.GuestHasItsOwnCompactDiscExtensions = true;
        uint driveFlags = 0x30000 + 3 * Aurora386.Core.Dos.DosStructures.CdsEntrySize
            + Aurora386.Core.Dos.DosStructures.CdsFlags;
        source.Memory.WriteWord(driveFlags, 0x4000);
        MachineSnapshot snapshot = MachineSnapshot.Capture(source);
        string snapshotPath = Path.GetTempFileName();
        try
        {
            if (throughFile)
            {
                snapshot.Save(snapshotPath);
                snapshot = MachineSnapshot.Load(snapshotPath);
            }

            using Machine restored = Booted();
            Assert.NotNull(restored.Redirector.GetMounted(3));
            snapshot.RestoreTo(restored);

            Assert.True(restored.GuestHasItsOwnCompactDiscExtensions);
            Assert.Null(restored.Redirector.GetMounted(3));
            Assert.Equal(0x4000, restored.Memory.ReadWord(driveFlags));
        }
        finally
        {
            File.Delete(snapshotPath);
        }
    }

    [Fact]
    public void RestoringTheStandInReattachesItsCdFilesystem()
    {
        using Machine machine = Booted();
        MachineSnapshot snapshot = MachineSnapshot.Capture(machine);
        machine.UnmountRedirectedDrive(3);
        machine.GuestHasItsOwnCompactDiscExtensions = true;

        snapshot.RestoreTo(machine);

        Assert.False(machine.GuestHasItsOwnCompactDiscExtensions);
        Assert.IsType<IsoFileSystem>(machine.Redirector.GetMounted(3));
        Assert.Equal((byte)3, machine.QueryCompactDiscLetter());
    }

    [Fact]
    public void APreviousSnapshotWithoutCdOwnershipRemainsReadable()
    {
        using Machine machine = Booted();
        string snapshotPath = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(snapshotPath);
            using (FileStream file = File.OpenWrite(snapshotPath))
            {
                file.SetLength(file.Length - 2);
            }
            MachineSnapshot.Load(snapshotPath).RestoreTo(machine);
            Assert.Equal((byte)3, machine.QueryCompactDiscLetter());
        }
        finally
        {
            File.Delete(snapshotPath);
        }
    }

    [Fact]
    public void WithoutAGuestDriverTheStandInAnswersWithItsOwnLetter()
    {
        using Machine machine = Booted();
        Assert.Equal((byte)3, machine.QueryCompactDiscLetter());   // D:
        Assert.Equal((byte)3, machine.CompactDiscLetter);
    }

    [Fact]
    public void AGuestDriverThatTookTheVectorIsTheOneWhoAnswers()
    {
        using Machine machine = Booted();

        // A handler at 0100:0000 answering the installation check with
        // "one drive, starting at F:", the way MSCDEX would after two disks.
        //   3D 00 15    CMP AX,1500
        //   75 07       JNE +7
        //   BB 01 00    MOV BX,1
        //   B9 05 00    MOV CX,5
        //   CF          IRET
        byte[] handler = [0x3D, 0x00, 0x15, 0x75, 0x07, 0xBB, 0x01, 0x00, 0xB9, 0x05, 0x00, 0xCF, 0xCF];
        for (int i = 0; i < handler.Length; i++) machine.Memory.WriteByte(0x1000 + (uint)i, handler[i]);
        machine.Memory.WriteWord(0x2F * 4, 0x0000);
        machine.Memory.WriteWord(0x2F * 4 + 2, 0x0100);

        byte? seen = null;
        machine.CompactDiscLetterChanged += letter => seen = letter;

        Assert.Equal((byte)5, machine.QueryCompactDiscLetter());
        Assert.Equal((byte)5, seen);

        // E: is occupied and MSCDEX still owns F: after the tray is emptied.
        machine.Memory.WriteWord(0x30000 + 4 * Aurora386.Core.Dos.DosStructures.CdsEntrySize
            + Aurora386.Core.Dos.DosStructures.CdsFlags, 0x4000);
        machine.EjectCompactDisc();
        Assert.Equal(6, machine.NextSharedFolderDrive());
    }

    [Fact]
    public void TheQuestionIsNotAskedInProtectedMode()
    {
        using Machine machine = Booted();
        machine.Cpu.State.Cr0 |= Aurora386.Core.Cpu.CpuState.Cr0ProtectionEnable;
        Assert.Null(machine.QueryCompactDiscLetter());
    }
}
