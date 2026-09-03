using System.Text;
using Aurora386.Core;
using Aurora386.Core.Dos;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Tests for the INT 2Fh function 11h protocol itself.
///
/// This interface was never documented by Microsoft, and everything about it
/// is a convention rather than a contract: arguments arrive in a scratch area
/// at fixed offsets, the drive is identified by pointer arithmetic on an
/// array, and failure is an error code in AX with carry set. Every one of
/// those is a place to get a number wrong in a way nothing detects -- DOS
/// simply behaves oddly. So each test here pins down one of those numbers.
///
/// The tests stand up the DOS structures by hand rather than booting DOS,
/// which is the only way to exercise the protocol deterministically: what a
/// real DOS puts in the SDA depends on what the guest was doing at the time.
/// </summary>
public class RedirectorProtocolTests : IDisposable
{
    /// <summary>
    /// Where the fake DOS structures are planted. Well above the interrupt
    /// vectors and the BIOS data area, and below where anything else in these
    /// tests reaches.
    /// </summary>
    private const uint SdaAddress = 0x20000;
    private const uint CdsAddress = 0x30000;
    private const uint SftAddress = 0x4000;      // ES:DI, with ES = 0
    private const uint DtaAddress = 0x40000;

    private readonly Machine _machine = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "aurora-redir-" + Guid.NewGuid().ToString("N"));

    /// <summary>Drive E:, the first letter past a hard disk and a CD.</summary>
    private const byte ShareDrive = 4;

    public RedirectorProtocolTests()
    {
        Directory.CreateDirectory(_root);

                // Twenty-six entries, the most DOS can have: these tests are about the
        // protocol, not about LASTDRIVE, and a short array would make every
        // one of them fail for the wrong reason.
        _machine.Redirector.AttachToDos(SdaAddress, CdsAddress, driveCount: 26);

        // The SDA's pointer to the CDS of the drive being worked on. This is
        // the only thing that says which drive a call is about, and the
        // redirector recovers the drive number by measuring the distance from
        // the start of the array.
        SetCurrentDrive(ShareDrive);
        SetDiskTransferAddress(DtaAddress);
    }

    public void Dispose()
    {
        _machine.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // --- harness ------------------------------------------------------------------

    private HostDirectoryFileSystem MountShare()
    {
        var share = new HostDirectoryFileSystem(_root);
        _machine.Redirector.Mount(ShareDrive, share);
        return share;
    }

    private void WriteHostFile(string relativePath, string contents)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    private void SetCurrentDrive(byte drive)
    {
        uint entry = CdsAddress + (uint)(drive * DosStructures.CdsEntrySize);

        // The pointer is a real-mode segment:offset pair, so the address has
        // to be expressible as one. A segment of address>>4 with a zero
        // offset always is, for any address in the first megabyte.
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaCurrentCdsPointer, 0);
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaCurrentCdsPointer + 2,
                                  (ushort)(entry >> 4));
    }

    private void SetDiskTransferAddress(uint address)
    {
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaDiskTransferAddress, 0);
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaDiskTransferAddress + 2,
                                  (ushort)(address >> 4));
    }

    /// <summary>
    /// Puts the fully qualified name where DOS would have put it: the SDA,
    /// not a register. Almost every subfunction reads its argument here.
    /// </summary>
    private void SetFilename(string qualified)
        => WriteAsciiz(SdaAddress + DosStructures.SdaFirstFilename, qualified);

    private void SetSecondFilename(string qualified)
        => WriteAsciiz(SdaAddress + DosStructures.SdaSecondFilename, qualified);

    private void WriteAsciiz(uint address, string text)
    {
        for (int i = 0; i < text.Length; i++)
            _machine.Memory.WriteByte(address + (uint)i, (byte)text[i]);
        _machine.Memory.WriteByte(address + (uint)text.Length, 0);
    }

    private void SetSearchAttributes(RedirectedFileAttributes attributes)
        => _machine.Memory.WriteByte(SdaAddress + DosStructures.SdaSearchAttributes,
                                     (byte)attributes);

    /// <summary>
    /// Makes the call, with a stack laid out the way an INT would have left
    /// it: the redirector reports failure by patching the flags image on the
    /// stack, because the IRET is about to load EFLAGS from there.
    /// </summary>
    private (bool handled, bool carry, CpuNs.CpuState state) Call(
        byte subfunction, Action<CpuNs.CpuState>? setup = null)
    {
        CpuNs.Cpu cpu = _machine.Cpu;

        cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        cpu.State.Esp = 0x7000;
        cpu.SetSegment(CpuNs.SegReg.Es, 0);

        // The three words an INT pushes: IP, CS, then FLAGS.
        _machine.Memory.WriteWord(0x7000, 0x1234);
        _machine.Memory.WriteWord(0x7002, 0x0000);
        _machine.Memory.WriteWord(0x7004, 0x0000);

        cpu.State.Ax = (ushort)(0x1100 | subfunction);
        cpu.State.Di = (ushort)SftAddress;
        setup?.Invoke(cpu.State);

        bool handled = _machine.Redirector.Handle(cpu);
        bool carry = (_machine.Memory.ReadWord(0x7004) & 1) != 0;

        return (handled, carry, cpu.State);
    }

    private byte ReadDta(uint offset) => _machine.Memory.ReadByte(DtaAddress + offset);

    private string ReadFoundName()
    {
        var builder = new StringBuilder(11);
        for (uint i = 0; i < 11; i++)
            builder.Append((char)_machine.Memory.ReadByte(
                SdaAddress + DosStructures.SdaFoundDirectoryEntry + i));
        return builder.ToString();
    }

    private uint ReadSft(uint offset)
        => (uint)(_machine.Memory.ReadWord(SftAddress + offset)
                | (_machine.Memory.ReadWord(SftAddress + offset + 2) << 16));

    // --- installation ---------------------------------------------------------------

    [Fact]
    public void TheInstallationCheckAnswersEvenWithNothingMounted()
    {
        // AL=FFh says a redirector exists. A program that asks and gets no
        // answer concludes the interface is absent altogether -- and it is
        // asked before any drive has been mapped, so refusing until something
        // is mounted would make the redirector undiscoverable.
        var machine = new Machine();
        using (machine)
        {
            var redirector = new RedirectorServices(machine);
            machine.Cpu.State.Ax = 0x1100;

            Assert.True(redirector.Handle(machine.Cpu));
            Assert.Equal(0xFF, machine.Cpu.State.GetReg8(0));
        }
    }

    [Fact]
    public void MountingADriveSetsBitFifteenOfItsCurrentDirectoryStructureFlags()
    {
        // This single bit is the entire act of installing a redirector on a
        // letter: it is what makes DOS stop handling the drive with its FAT
        // code and issue INT 2Fh instead. Without it every call below is
        // never made at all, and the drive reports "invalid drive".
        MountShare();

        uint entry = CdsAddress + (uint)(ShareDrive * DosStructures.CdsEntrySize);
        ushort flags = _machine.Memory.ReadWord(entry + DosStructures.CdsFlags);

        Assert.True((flags & DosStructures.CdsRedirectedFlag) != 0);
    }

    [Fact]
    public void MountingADriveAlsoSetsTheBitThatMakesTheLetterSelectable()
    {
        // Bit 14, called "physical drive" everywhere it is described, is also
        // set. That looks wrong -- there is no physical drive -- but MS-DOS
        // tests it when deciding whether a letter may be selected at all,
        // before it has looked at bit 15 and long before it would consider
        // calling a redirector. With bit 15 alone, typing the letter gets
        // "Invalid drive specification" and not one INT 2Fh call is ever
        // made, so the redirector looks completely broken while in fact it is
        // simply never asked.
        MountShare();

        uint entry = CdsAddress + (uint)(ShareDrive * DosStructures.CdsEntrySize);
        ushort flags = _machine.Memory.ReadWord(entry + DosStructures.CdsFlags);

        Assert.True((flags & DosStructures.CdsValidFlag) != 0);
        Assert.True((flags & DosStructures.CdsRedirectedFlag) != 0);
    }

    [Fact]
    public void UnmountingClearsTheRedirectedFlagAgain()
    {
        // Leaving it set with nothing behind it is worse than either state:
        // DOS keeps routing calls to a redirector that answers "no" to
        // everything, so the letter exists but is permanently broken.
        MountShare();
        _machine.Redirector.Unmount(ShareDrive);

        uint entry = CdsAddress + (uint)(ShareDrive * DosStructures.CdsEntrySize);
        ushort flags = _machine.Memory.ReadWord(entry + DosStructures.CdsFlags);

        Assert.True((flags & DosStructures.CdsRedirectedFlag) == 0);
        Assert.True((flags & DosStructures.CdsValidFlag) == 0);
    }

    [Fact]
    public void ALetterPastLastDriveIsRefusedInsteadOfWrittenOver()
    {
        // The CDS array is exactly LASTDRIVE entries long and is followed
        // immediately by other DOS structures. Writing a flags word one entry
        // past the end corrupts them, and the damage surfaces much later and
        // somewhere else entirely -- which is the worst kind of bug this
        // interface can produce, because nothing connects the symptom to the
        // mount that caused it.
        //
        // MS-DOS defaults LASTDRIVE to five, so E: is commonly the last
        // letter available and anything beyond it needs a CONFIG.SYS line.
        var machine = new Machine();
        using (machine)
        {
            const uint sda = SdaAddress;
            const uint cds = CdsAddress;
            machine.Redirector.AttachToDos(sda, cds, driveCount: 5);

            // The bytes just past the last entry, which must survive.
            uint pastTheEnd = cds + (uint)(5 * DosStructures.CdsEntrySize);
            machine.Memory.WriteWord(pastTheEnd + DosStructures.CdsFlags, 0x1234);

            bool accepted = machine.Redirector.Mount(5, new HostDirectoryFileSystem(_root));

            Assert.False(accepted);
            Assert.Equal(0x1234, machine.Memory.ReadWord(pastTheEnd + DosStructures.CdsFlags));
        }
    }

    [Fact]
    public void ALetterWithinLastDriveIsAccepted()
    {
        var machine = new Machine();
        using (machine)
        {
            machine.Redirector.AttachToDos(SdaAddress, CdsAddress, driveCount: 5);
            Assert.True(machine.Redirector.Mount(4, new HostDirectoryFileSystem(_root)));
        }
    }

    [Fact]
    public void ACallAboutADriveWeDoNotServeIsDeclined()
    {
        // Declining leaves AX untouched, which is how INT 2Fh says nobody
        // answered. Failing instead would claim the drive and then break it.
        MountShare();
        SetCurrentDrive(2);          // C:, the hard disk
        SetFilename("C:\\AUTOEXEC.BAT");

        Assert.False(Call(0x0F).handled);
    }

    // --- searching ------------------------------------------------------------------

    [Fact]
    public void FindFirstReportsAMatchingFileInTheDirectoryEntryFormat()
    {
        // The result goes into the SDA's found-entry field as a 32-byte FAT
        // directory entry: eleven bytes of blank-padded name with no dot.
        // Writing "README.TXT" verbatim, dot included, gives DOS a name it
        // parses as "README.T" with extension "XT".
        MountShare();
        WriteHostFile("README.TXT", "hello");

        SetFilename($"{(char)('A' + ShareDrive)}:\\*.*");
        SetSearchAttributes(RedirectedFileAttributes.None);

        (bool handled, bool carry, _) = Call(0x1B);

        Assert.True(handled);
        Assert.False(carry);
        Assert.Equal("README  TXT", ReadFoundName());
    }

    [Fact]
    public void FindFirstOnAnEmptyDirectoryFailsWithNoMoreFiles()
    {
        // Error 12h, not 02h. DIR prints "File not found" for either, but a
        // program looping over a wildcard tells them apart, and DOS's own
        // local search returns 12h here.
        MountShare();
        SetFilename($"{(char)('A' + ShareDrive)}:\\*.*");
        SetSearchAttributes(RedirectedFileAttributes.None);

        (bool handled, bool carry, CpuNs.CpuState state) = Call(0x1B);

        Assert.True(handled);
        Assert.True(carry);
        Assert.Equal(DosStructures.ErrorNoMoreFiles, state.Ax);
    }

    [Fact]
    public void FindNextWalksThroughTheRemainingMatches()
    {
        // The search identity lives in the 21-byte block in the SDA, because
        // findnext is given nothing else. Lose it between calls and findnext
        // either returns the first match forever or fails immediately.
        MountShare();
        WriteHostFile("A.TXT", "1");
        WriteHostFile("B.TXT", "2");
        WriteHostFile("C.TXT", "3");

        SetFilename($"{(char)('A' + ShareDrive)}:\\*.*");
        SetSearchAttributes(RedirectedFileAttributes.None);

        Assert.False(Call(0x1B).carry);
        var found = new List<string> { ReadFoundName() };

        for (int i = 0; i < 2; i++)
        {
            Assert.False(Call(0x1C).carry);
            found.Add(ReadFoundName());
        }

        Assert.Equal(["A       TXT", "B       TXT", "C       TXT"], found.Order());

        // And then it must stop, rather than cycling round again.
        Assert.True(Call(0x1C).carry);
    }

    [Fact]
    public void TheSearchBlockIsMarkedAsBelongingToARedirector()
    {
        // Byte 0 of the block is the drive number with bit 7 set. Without the
        // bit DOS handles findnext with its own FAT code, on a drive that has
        // no FAT -- so the second entry of every listing is garbage.
        MountShare();
        WriteHostFile("FILE.TXT", "x");

        SetFilename($"{(char)('A' + ShareDrive)}:\\*.*");
        SetSearchAttributes(RedirectedFileAttributes.None);
        Call(0x1B);

        byte marker = _machine.Memory.ReadByte(SdaAddress + DosStructures.SdaFindFirstBlock);
        Assert.True((marker & 0x80) != 0);
    }

    [Fact]
    public void AWildcardPatternIsMatchedFieldByFieldTheWayDosDid()
    {
        // "*.TXT" reaches us as a pattern that expands to eight question
        // marks and "TXT". A glob-style matcher gets subtly different answers
        // -- most visibly on a file with no extension, which "*.*" matches
        // because '?' matches the blank padding too.
        MountShare();
        WriteHostFile("NOTES.TXT", "a");
        WriteHostFile("IMAGE.BMP", "b");
        WriteHostFile("MAKEFILE", "c");

        SetFilename($"{(char)('A' + ShareDrive)}:\\*.TXT");
        SetSearchAttributes(RedirectedFileAttributes.None);

        Assert.False(Call(0x1B).carry);
        Assert.Equal("NOTES   TXT", ReadFoundName());
        Assert.True(Call(0x1C).carry);   // and nothing else matches
    }

    [Fact]
    public void AStarMatchesAFileWithNoExtension()
    {
        // The surprising DOS behaviour, and a direct consequence of expanding
        // wildcards at parse time: "*.*" fills both fields with question
        // marks, and a blank extension is matched by '?' like anything else.
        MountShare();
        WriteHostFile("MAKEFILE", "x");

        SetFilename($"{(char)('A' + ShareDrive)}:\\*.*");
        SetSearchAttributes(RedirectedFileAttributes.None);

        Assert.False(Call(0x1B).carry);
        Assert.Equal("MAKEFILE   ", ReadFoundName());
    }

    [Fact]
    public void AskingForTheVolumeLabelReturnsItInsteadOfTheFiles()
    {
        // DIR gets the label by searching the root for an entry with the
        // volume attribute -- there is no other call that returns it. A
        // redirector that ignores the request produces a listing with no
        // label line, and CHKDSK reports the volume as unlabelled.
        _machine.Redirector.Mount(ShareDrive, new HostDirectoryFileSystem(_root, "MYSHARE"));
        WriteHostFile("FILE.TXT", "x");

        SetFilename($"{(char)('A' + ShareDrive)}:\\*.*");
        SetSearchAttributes(RedirectedFileAttributes.VolumeLabel);

        Assert.False(Call(0x1B).carry);
        Assert.Equal("MYSHARE    ", ReadFoundName());
        Assert.Equal((byte)RedirectedFileAttributes.VolumeLabel,
                     _machine.Memory.ReadByte(SdaAddress + DosStructures.SdaFoundDirectoryEntry + 11));
    }

    [Fact]
    public void ADirectoryIsOnlyListedWhenTheAttributeMaskAsksForIt()
    {
        // The mask means "as well as ordinary files, show me these", not
        // "show me only these". A redirector that treats it as an exact match
        // hides every ordinary file the moment DIR asks for directories too.
        MountShare();
        WriteHostFile("FILE.TXT", "x");
        Directory.CreateDirectory(Path.Combine(_root, "SUBDIR"));

        SetFilename($"{(char)('A' + ShareDrive)}:\\*.*");

        SetSearchAttributes(RedirectedFileAttributes.None);
        Assert.False(Call(0x1B).carry);
        Assert.Equal("FILE    TXT", ReadFoundName());
        Assert.True(Call(0x1C).carry);

        SetSearchAttributes(RedirectedFileAttributes.Directory);
        var seen = new List<string>();
        Assert.False(Call(0x1B).carry);
        seen.Add(ReadFoundName());
        Assert.False(Call(0x1C).carry);
        seen.Add(ReadFoundName());

        Assert.Equal(["FILE    TXT", "SUBDIR     "], seen.Order());
    }

    // --- opening and reading ----------------------------------------------------------

    [Fact]
    public void OpeningAFileFillsInTheSystemFileTableEntry()
    {
        // DOS reads the SFT back on every later operation. The size and the
        // starting position are what a program sees from INT 21h AH=42h, and
        // the network bit in the open mode is what routes the next read to us
        // rather than to the FAT code.
        MountShare();
        WriteHostFile("DATA.BIN", "0123456789");

        SetFilename($"{(char)('A' + ShareDrive)}:\\DATA.BIN");

        (bool handled, bool carry, _) = Call(0x16);

        Assert.True(handled);
        Assert.False(carry);
        Assert.Equal(10u, ReadSft(DosStructures.SftFileSize));
        Assert.Equal(0u, ReadSft(DosStructures.SftFilePosition));

        ushort mode = _machine.Memory.ReadWord(SftAddress + DosStructures.SftOpenMode);
        Assert.True((mode & 0x8000) != 0);
    }

    [Fact]
    public void TheDriveNumberGoesInTheLowBitsOfTheDeviceInformationWord()
    {
        // And bit 15 must stay clear there: a set bit 15 means a character
        // device, and DOS then treats the file as if it were CON.
        MountShare();
        WriteHostFile("DATA.BIN", "x");
        SetFilename($"{(char)('A' + ShareDrive)}:\\DATA.BIN");
        Call(0x16);

        ushort info = _machine.Memory.ReadWord(SftAddress + DosStructures.SftDeviceInfo);

        Assert.Equal(ShareDrive, info & 0x3F);
        Assert.True((info & 0x8000) == 0);
    }

    [Fact]
    public void OpeningAFileThatIsNotThereFailsWithFileNotFound()
    {
        // Error 02h specifically. A program probing for an optional file
        // treats "file not found" as a normal answer and carries on, but
        // takes "access denied" as a real problem and gives up.
        MountShare();
        SetFilename($"{(char)('A' + ShareDrive)}:\\MISSING.TXT");

        (_, bool carry, CpuNs.CpuState state) = Call(0x16);

        Assert.True(carry);
        Assert.Equal(DosStructures.ErrorFileNotFound, state.Ax);
    }

    [Fact]
    public void ReadingCopiesBytesToTheDiskTransferAddressAndAdvancesThePosition()
    {
        // DOS does not advance the SFT position for a redirected file: the
        // redirector must. Forgetting is the classic bug that makes every
        // read return the beginning of the file, so a program copying a file
        // loops forever on its first block.
        MountShare();
        WriteHostFile("DATA.BIN", "ABCDEFGHIJ");
        SetFilename($"{(char)('A' + ShareDrive)}:\\DATA.BIN");
        Call(0x16);

        (_, bool carry, CpuNs.CpuState state) = Call(0x08, s => s.Cx = 4);

        Assert.False(carry);
        Assert.Equal(4, state.Cx);
        Assert.Equal("ABCD", ReadDtaString(4));
        Assert.Equal(4u, ReadSft(DosStructures.SftFilePosition));

        (_, _, CpuNs.CpuState second) = Call(0x08, s => s.Cx = 4);
        Assert.Equal("EFGH", ReadDtaString(4));
        Assert.Equal(8u, ReadSft(DosStructures.SftFilePosition));
        Assert.Equal(4, second.Cx);
    }

    [Fact]
    public void AReadThatRunsOffTheEndReturnsAShortCountRatherThanFailing()
    {
        // A short read is how DOS signals end of file. Returning an error
        // instead makes the last block of every file look like a disk fault.
        MountShare();
        WriteHostFile("DATA.BIN", "ABC");
        SetFilename($"{(char)('A' + ShareDrive)}:\\DATA.BIN");
        Call(0x16);

        (_, bool carry, CpuNs.CpuState state) = Call(0x08, s => s.Cx = 512);

        Assert.False(carry);
        Assert.Equal(3, state.Cx);
    }

    [Fact]
    public void ClosingReleasesTheFileAndFurtherReadsFail()
    {
        // The identifier in the SFT's four spare bytes is the only link back
        // to our own table; a close that does not drop the entry leaks one
        // per open file for the life of the machine.
        MountShare();
        WriteHostFile("DATA.BIN", "x");
        SetFilename($"{(char)('A' + ShareDrive)}:\\DATA.BIN");
        Call(0x16);

        Assert.False(Call(0x06).carry);

        (_, bool carry, CpuNs.CpuState state) = Call(0x08, s => s.Cx = 1);
        Assert.True(carry);
        Assert.Equal(DosStructures.ErrorInvalidHandle, state.Ax);
    }

    [Fact]
    public void SeekingFromTheEndPositionsRelativeToTheCurrentLength()
    {
        // The only seek a redirector is asked about, because it is the only
        // one that needs to know how long the file is on the far side. The
        // displacement in CX:DX is subtracted from the length.
        MountShare();
        WriteHostFile("DATA.BIN", "0123456789");
        SetFilename($"{(char)('A' + ShareDrive)}:\\DATA.BIN");
        Call(0x16);

        (_, bool carry, CpuNs.CpuState state) = Call(0x21, s => { s.Cx = 0; s.Dx = 4; });

        Assert.False(carry);
        Assert.Equal(6u, ReadSft(DosStructures.SftFilePosition));
        Assert.Equal(6, state.Ax);
    }

    // --- writing ------------------------------------------------------------------------

    [Fact]
    public void WritingStoresTheBytesAndGrowsTheRecordedSize()
    {
        MountShare();
        SetFilename($"{(char)('A' + ShareDrive)}:\\NEW.TXT");
        Assert.False(Call(0x17, s => s.Cx = 0).carry);

        for (int i = 0; i < 5; i++)
            _machine.Memory.WriteByte(DtaAddress + (uint)i, (byte)"HELLO"[i]);

        (_, bool carry, CpuNs.CpuState state) = Call(0x09, s => s.Cx = 5);

        Assert.False(carry);
        Assert.Equal(5, state.Cx);
        Assert.Equal(5u, ReadSft(DosStructures.SftFileSize));
        Assert.Equal("HELLO", File.ReadAllText(Path.Combine(_root, "NEW.TXT")));
    }

    [Fact]
    public void AWriteOfZeroBytesTruncatesTheFileAtTheCurrentPosition()
    {
        // Not a no-op: it is the only way a DOS program can shorten a file,
        // and a redirector that treats it as nothing to do leaves the tail of
        // an overwritten file in place.
        MountShare();
        WriteHostFile("DATA.TXT", "0123456789");
        SetFilename($"{(char)('A' + ShareDrive)}:\\DATA.TXT");
        Call(0x16);
        Call(0x21, s => { s.Cx = 0; s.Dx = 6; });   // position 4

        Assert.False(Call(0x09, s => s.Cx = 0).carry);
        Assert.Equal("0123", File.ReadAllText(Path.Combine(_root, "DATA.TXT")));
    }

    [Fact]
    public void WritingToAReadOnlyFilesystemIsRefusedAsWriteProtected()
    {
        // Error 13h, the code DOS uses for a write-protected disk. A CD
        // reports it, and a program that gets it knows to stop rather than
        // retry.
        var disc = new ReadOnlyStub();
        _machine.Redirector.Mount(ShareDrive, disc);

        SetFilename($"{(char)('A' + ShareDrive)}:\\FILE.TXT");
        Call(0x16);

        (_, bool carry, CpuNs.CpuState state) = Call(0x09, s => s.Cx = 4);

        Assert.True(carry);
        Assert.Equal(DosStructures.ErrorWriteProtected, state.Ax);
    }

    [Fact]
    public void CreatingADirectoryOnAReadOnlyFilesystemIsRefused()
    {
        _machine.Redirector.Mount(ShareDrive, new ReadOnlyStub());
        SetFilename($"{(char)('A' + ShareDrive)}:\\NEWDIR");

        (_, bool carry, CpuNs.CpuState state) = Call(0x03);

        Assert.True(carry);
        Assert.Equal(DosStructures.ErrorAccessDenied, state.Ax);
    }

    [Fact]
    public void DeletingWithAWildcardRemovesEveryMatch()
    {
        // "DEL *.TMP" reaches the redirector as one call carrying a pattern,
        // not as one call per file: DOS does not expand it first. A
        // redirector that treats the pattern as a literal name deletes
        // nothing and reports the file missing.
        MountShare();
        WriteHostFile("A.TMP", "1");
        WriteHostFile("B.TMP", "2");
        WriteHostFile("KEEP.TXT", "3");

        SetFilename($"{(char)('A' + ShareDrive)}:\\*.TMP");

        Assert.False(Call(0x11).carry);
        Assert.False(File.Exists(Path.Combine(_root, "A.TMP")));
        Assert.False(File.Exists(Path.Combine(_root, "B.TMP")));
        Assert.True(File.Exists(Path.Combine(_root, "KEEP.TXT")));
    }

    [Fact]
    public void RenamingTakesItsTargetFromTheSecondFilenameField()
    {
        // The only subfunction with two names, and the second is in a
        // different SDA field. Reading it from the first gives a rename onto
        // itself, which quietly does nothing.
        MountShare();
        WriteHostFile("OLD.TXT", "x");

        SetFilename($"{(char)('A' + ShareDrive)}:\\OLD.TXT");
        SetSecondFilename($"{(char)('A' + ShareDrive)}:\\NEW.TXT");

        Assert.False(Call(0x13).carry);
        Assert.True(File.Exists(Path.Combine(_root, "NEW.TXT")));
        Assert.False(File.Exists(Path.Combine(_root, "OLD.TXT")));
    }

    // --- directories and drive information ----------------------------------------------

    [Fact]
    public void ChangingIntoADirectoryThatExistsSucceeds()
    {
        MountShare();
        Directory.CreateDirectory(Path.Combine(_root, "GAMES"));

        SetFilename($"{(char)('A' + ShareDrive)}:\\GAMES");
        Assert.False(Call(0x05).carry);
    }

    [Fact]
    public void ChangingIntoAFileFailsWithPathNotFound()
    {
        // A file is not a directory, and DOS must be told so with 03h rather
        // than 02h: CD reports "Invalid directory" for the first and would
        // report a missing file for the second.
        MountShare();
        WriteHostFile("README.TXT", "x");

        SetFilename($"{(char)('A' + ShareDrive)}:\\README.TXT");

        (_, bool carry, CpuNs.CpuState state) = Call(0x05);
        Assert.True(carry);
        Assert.Equal(DosStructures.ErrorPathNotFound, state.Ax);
    }

    [Fact]
    public void MakingAndRemovingADirectoryWorksThroughTheProtocol()
    {
        MountShare();

        SetFilename($"{(char)('A' + ShareDrive)}:\\NEWDIR");
        Assert.False(Call(0x03).carry);
        Assert.True(Directory.Exists(Path.Combine(_root, "NEWDIR")));

        Assert.False(Call(0x01).carry);
        Assert.False(Directory.Exists(Path.Combine(_root, "NEWDIR")));
    }

    [Fact]
    public void GetDiskSpaceReportsClustersThatMultiplyOutToTheRealSize()
    {
        // DOS holds the cluster count in sixteen bits, so a large volume has
        // to be described with big clusters rather than many of them. A
        // redirector that returns the raw byte count truncated to sixteen
        // bits reports a few kilobytes free on a gigabyte share, and every
        // installer refuses to run.
        _machine.Redirector.Mount(ShareDrive, new FixedSizeStub(free: 100L * 1024 * 1024,
                                                                total: 1024L * 1024 * 1024));

        (_, bool carry, CpuNs.CpuState state) = Call(0x0C);

        Assert.False(carry);
        Assert.Equal(512, state.Cx);                 // bytes per sector
        Assert.True(state.Bx <= 0xFFFF);

        long reportedTotal = (long)state.Ax * state.Cx * state.Bx;
        Assert.InRange(reportedTotal, 900L * 1024 * 1024, 1100L * 1024 * 1024);
    }

    [Fact]
    public void GetFileAttributesReturnsTheSizeSplitAcrossBxAndDi()
    {
        // The 32-bit size is returned as two 16-bit halves in registers that
        // are nowhere near each other: the high word in BX, the low word in
        // DI. Swapping them gives a size out by a factor of 65536.
        MountShare();
        WriteHostFile("DATA.BIN", new string('x', 0x12345));

        SetFilename($"{(char)('A' + ShareDrive)}:\\DATA.BIN");

        (_, bool carry, CpuNs.CpuState state) = Call(0x0F);

        Assert.False(carry);
        Assert.Equal(0x0001, state.Bx);
        Assert.Equal(0x2345, state.Di);
    }

    [Fact]
    public void SpecialOpenCreatesTheFileWhenAskedToAndReportsThatItDid()
    {
        // Subfunction 1Eh is the combined open-or-create that DOS 4 onwards
        // uses for everything. CX comes back saying which happened: 1 opened,
        // 2 created, 3 replaced.
        MountShare();
        SetFilename($"{(char)('A' + ShareDrive)}:\\FRESH.TXT");

        // Action 0x10: create if it does not exist, fail if it does.
        (_, bool carry, CpuNs.CpuState state) = Call(0x1E, s => s.Dx = 0x10);

        Assert.False(carry);
        Assert.Equal(2, state.Cx);
        Assert.True(File.Exists(Path.Combine(_root, "FRESH.TXT")));
    }

    [Fact]
    public void SpecialOpenOpensAnExistingFileWithoutTruncatingIt()
    {
        MountShare();
        WriteHostFile("EXISTING.TXT", "keep me");
        SetFilename($"{(char)('A' + ShareDrive)}:\\EXISTING.TXT");

        // Action 0x11: open if it exists, create if it does not.
        (_, bool carry, CpuNs.CpuState state) = Call(0x1E, s => s.Dx = 0x11);

        Assert.False(carry);
        Assert.Equal(1, state.Cx);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_root, "EXISTING.TXT")));
    }

    [Fact]
    public void ASubfunctionWeDoNotImplementIsDeclinedRatherThanFailed()
    {
        // Declining leaves AX untouched, which DOS reads as "this operation
        // is not available on this drive" and reports sensibly. An invented
        // error code would reach the program as a real failure.
        MountShare();
        Assert.False(Call(0x0A).handled);   // lock region, which needs SHARE
    }

    [Fact]
    public void TheDriveIsWorkedOutFromWhereTheCdsPointerAims()
    {
        // There is no drive number in a register: the redirector recovers it
        // by measuring how far the SDA's current-CDS pointer is from the start
        // of the CDS array and dividing by the entry size. Get the entry size
        // wrong -- DOS 3 used 81 bytes, DOS 4 and later 88 -- and every call
        // is attributed to a drive several letters away.
        MountShare();
        WriteHostFile("MARKER.TXT", "x");

        // Aimed at E:, the drive we mounted: the call is ours.
        SetCurrentDrive(ShareDrive);
        SetFilename($"{(char)('A' + ShareDrive)}:\\MARKER.TXT");
        Assert.True(Call(0x0F).handled);

        // Aimed one entry further along, at F:, which nothing serves.
        SetCurrentDrive(ShareDrive + 1);
        Assert.False(Call(0x0F).handled);
    }

    [Fact]
    public void APointerThatDoesNotLandOnAnEntryBoundaryIsRejected()
    {
        // A pointer into the middle of an entry means the SDA holds something
        // that is not a CDS pointer at all -- the structures have moved, or
        // we are looking at the wrong DOS. Dividing anyway produces a drive
        // number that is merely plausible, and the call is then answered on
        // behalf of a drive nobody asked about.
        MountShare();

        uint middleOfEntry = CdsAddress + (uint)(ShareDrive * DosStructures.CdsEntrySize) + 16;
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaCurrentCdsPointer,
                                  (ushort)(middleOfEntry & 0x0F));
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaCurrentCdsPointer + 2,
                                  (ushort)(middleOfEntry >> 4));

        SetFilename($"{(char)('A' + ShareDrive)}:\\FILE.TXT");
        Assert.False(Call(0x0F).handled);
    }

    [Fact]
    public void MountingWritesTheDriveLetterIntoTheCurrentDirectoryPath()
    {
        // DOS shows this string to the user and parses it back when resolving
        // a relative name. Leaving the previous drive's letter there sends
        // every relative lookup to the wrong drive.
        MountShare();

        uint entry = CdsAddress + (uint)(ShareDrive * DosStructures.CdsEntrySize);
        var path = new StringBuilder();
        for (uint i = 0; i < 3; i++)
            path.Append((char)_machine.Memory.ReadByte(entry + DosStructures.CdsPath + i));

        Assert.Equal("E:\\", path.ToString());
        Assert.Equal(2, _machine.Memory.ReadWord(entry + DosStructures.CdsRootLength));
    }

    [Fact]
    public void AnErrorIsAlsoRecordedInTheSwappableDataAreasErrorField()
    {
        // INT 21h AH=59h fetches the extended error from there, long after
        // the call that failed has returned and AX has been overwritten.
        MountShare();
        SetFilename($"{(char)('A' + ShareDrive)}:\\MISSING.TXT");

        Call(0x16);

        Assert.Equal(DosStructures.ErrorFileNotFound,
                     _machine.Memory.ReadWord(SdaAddress + DosStructures.SdaErrorCode));
    }

    private string ReadDtaString(int length)
    {
        var builder = new StringBuilder(length);
        for (uint i = 0; i < length; i++) builder.Append((char)ReadDta(i));
        return builder.ToString();
    }

    /// <summary>A filesystem that refuses every write, standing in for a disc.</summary>
    private sealed class ReadOnlyStub : IRedirectedFileSystem
    {
        public string VolumeLabel => "DISC";
        public bool IsReadOnly => true;

        public RedirectedFileInfo? GetInfo(string path) => new()
        {
            Name = "FILE.TXT",
            Attributes = RedirectedFileAttributes.ReadOnly,
            Size = 4,
        };

        public IEnumerable<RedirectedFileInfo> Enumerate(string directoryPath) => [];
        public int Read(string path, long offset, Span<byte> destination) => 0;
        public (long free, long total) GetDiskSpace() => (0, 1024);

        public void Create(string path, RedirectedFileAttributes attributes) => throw new NotSupportedException();
        public void Write(string path, long offset, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void SetLength(string path, long length) => throw new NotSupportedException();
        public void Delete(string path) => throw new NotSupportedException();
        public void Rename(string fromPath, string toPath) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public void RemoveDirectory(string path) => throw new NotSupportedException();
    }

    /// <summary>A filesystem that reports a size of our choosing and nothing else.</summary>
    private sealed class FixedSizeStub(long free, long total) : IRedirectedFileSystem
    {
        public string VolumeLabel => "SIZED";
        public bool IsReadOnly => false;

        public RedirectedFileInfo? GetInfo(string path) => null;
        public IEnumerable<RedirectedFileInfo> Enumerate(string directoryPath) => [];
        public int Read(string path, long offset, Span<byte> destination) => 0;
        public (long free, long total) GetDiskSpace() => (free, total);

        public void Create(string path, RedirectedFileAttributes attributes) { }
        public void Write(string path, long offset, ReadOnlySpan<byte> data) { }
        public void SetLength(string path, long length) { }
        public void Delete(string path) { }
        public void Rename(string fromPath, string toPath) { }
        public void CreateDirectory(string path) { }
        public void RemoveDirectory(string path) { }
    }
}
