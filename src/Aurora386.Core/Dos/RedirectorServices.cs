using System.Text;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Dos;

/// <summary>
/// The DOS network redirector: INT 2Fh function 11h.
///
/// This is the interface that makes something which is not a FAT disk appear
/// as a drive letter. Microsoft built it for LAN Manager, Novell wrote a
/// redirector against it for NetWare, and MSCDEX used it to present a CD --
/// which is why a CD-ROM has always been drive D: rather than a device you
/// open by name. The same machinery serves both purposes here: a folder on
/// the host and an ISO image differ only in which
/// <see cref="IRedirectedFileSystem"/> is behind the letter.
///
/// <para><b>How DOS decides to call us.</b> Every file operation begins in
/// INT 21h, which resolves the name to a drive letter and looks that letter
/// up in the Current Directory Structure. If bit 15 of the CDS flags is set,
/// DOS stops there and issues INT 2Fh with AH=11h and the subfunction in AL.
/// Nothing else marks a drive as ours: setting that bit is the installation.
/// </para>
///
/// <para><b>How arguments arrive.</b> Almost never in registers. DOS puts the
/// fully qualified filename in the Swappable Data Area, points the SDA's
/// current-CDS field at the drive being used, and expects the redirector to
/// read them from there. The exceptions are the file operations -- read,
/// write, close -- which get a pointer to the System File Table entry in
/// ES:DI, and findnext, which gets the search block in the disk transfer
/// address.</para>
///
/// <para><b>How answers are returned.</b> Carry clear means success. Carry
/// set means failure, with the DOS error code in AX -- and DOS passes that
/// code straight back to the program, so returning the wrong one produces
/// bizarre symptoms: report "access denied" where "file not found" was meant
/// and a program that was probing for an optional file gives up with an
/// error instead of carrying on.</para>
///
/// <para><b>What is implemented here.</b> Everything needed to list, read,
/// change into and execute from a redirected drive, plus writing for a
/// backend that permits it. What is not implemented is documented at the
/// switch below.</para>
/// </summary>
public sealed class RedirectorServices(Machine machine)
{
    private readonly Machine _machine = machine;

    /// <summary>
    /// The drives we answer for, indexed by number (0 = A:). A letter with no
    /// entry is not ours and every call about it is declined, which lets DOS
    /// fall through to whatever else might be listening.
    /// </summary>
    private readonly Dictionary<byte, IRedirectedFileSystem> _drives = [];

    /// <summary>
    /// Open files, keyed by the value we stashed in the SFT's four spare
    /// bytes. DOS gives us those bytes precisely so we have somewhere to put
    /// a handle of our own, and this is the table it indexes.
    /// </summary>
    private readonly Dictionary<uint, OpenFile> _openFiles = [];
    private uint _nextFileId = 1;

    /// <summary>
    /// In-progress directory searches, keyed by the identifier written into
    /// the search block that DOS hands back and forth. A search has to hold
    /// state across calls -- findfirst produces the list, findnext walks it --
    /// and the search block is only 21 bytes, far too small to hold a
    /// directory listing.
    /// </summary>
    private readonly Dictionary<ushort, ActiveSearch> _searches = [];
    private ushort _nextSearchId = 1;

    /// <summary>
    /// Diagnostics hook, called for every subfunction with the drive it was
    /// resolved to. It exists because the single hardest thing to see about
    /// this interface is whether DOS is routing to it at all: a drive that
    /// reports "invalid drive" looks identical whether the redirector
    /// answered badly or was never asked.
    /// </summary>
    public Action<byte, byte>? Trace { get; set; }

    /// <summary>
    /// The other half of <see cref="Trace"/>: the subfunction, whether it
    /// was answered, and AX afterwards -- the error code when it failed.
    /// Which call declined or failed, and with what, is the whole of what a
    /// DOS error message hides.
    /// </summary>
    public Action<byte, bool, ushort>? TraceResult { get; set; }

    /// <summary>Address of the Swappable Data Area, learned at installation.</summary>
    public uint SwappableDataArea { get; private set; }

    /// <summary>Address of the first Current Directory Structure entry.</summary>
    public uint CurrentDirectoryStructure { get; private set; }

    /// <summary>
    /// How many entries the CDS array has, which is the LASTDRIVE setting.
    ///
    /// This is a hard limit on which letters can be redirected, and not a
    /// soft one: the array is followed straight away by other DOS structures,
    /// so writing a flags word one entry past the end corrupts them. DOS
    /// defaults LASTDRIVE to five, so E: is usually the last letter available
    /// and a user who wants more has to say so in CONFIG.SYS.
    /// </summary>
    public byte DriveCount { get; private set; }

    /// <summary>
    /// Records where DOS keeps the two structures the protocol works through,
    /// and applies the redirected flag to anything already mounted.
    ///
    /// Called by <see cref="RedirectorInstaller"/> once it has asked DOS, and
    /// by tests that stand the structures up by hand rather than booting a
    /// real DOS -- which is the only way to exercise the protocol
    /// deterministically, since what a live DOS has in its SDA depends on
    /// what the guest happened to be doing.
    /// </summary>
    public void AttachToDos(uint swappableDataArea, uint currentDirectoryStructure,
                            byte driveCount)
    {
        SwappableDataArea = swappableDataArea;
        CurrentDirectoryStructure = currentDirectoryStructure;
        DriveCount = driveCount;
        ReapplyMounts();
    }

    /// <summary>True once we know where DOS keeps its structures.</summary>
    public bool IsInstalled => SwappableDataArea != 0 && CurrentDirectoryStructure != 0;

    private sealed class OpenFile
    {
        public required IRedirectedFileSystem FileSystem { get; init; }
        public required string Path { get; init; }
        public required byte Drive { get; init; }
    }

    private sealed class ActiveSearch
    {
        public required IRedirectedFileSystem FileSystem { get; init; }
        public required List<RedirectedFileInfo> Matches { get; init; }
        public int Position { get; set; }
    }

    // --- mounting --------------------------------------------------------------

    /// <summary>
    /// Puts a filesystem behind a drive letter. The letter is a number with
    /// 0 meaning A:, the same convention DOS uses everywhere below the
    /// surface.
    ///
    /// Returns false when DOS is up but has no CDS entry for that letter,
    /// which means LASTDRIVE in CONFIG.SYS is too low. The mount is still
    /// recorded, because DOS may not be running yet and the limit is only
    /// knowable once it is -- but the caller is told, because there is
    /// nothing else that would reveal the problem: the letter simply reports
    /// "invalid drive" from inside DOS, with no hint as to why.
    /// </summary>
    public bool Mount(byte drive, IRedirectedFileSystem fileSystem)
    {
        _drives[drive] = fileSystem;
        MarkDriveRedirected(drive);

        return !IsInstalled || drive < DriveCount;
    }

    public void Unmount(byte drive)
    {
        if (!_drives.Remove(drive)) return;

        // Clearing the flag hands the letter back to DOS. Leaving it set with
        // nothing behind it would be worse than either state: DOS would keep
        // routing calls to a redirector that answers "no such drive" to
        // everything, and the letter would appear to exist while being
        // permanently broken.
        ClearDriveRedirected(drive);

        foreach (uint id in _openFiles.Where(f => f.Value.Drive == drive)
                                      .Select(f => f.Key).ToList())
            _openFiles.Remove(id);
    }

    public IRedirectedFileSystem? GetMounted(byte drive)
        => _drives.GetValueOrDefault(drive);

    /// <summary>The drive letters currently served, lowest first.</summary>
    public IEnumerable<byte> MountedDrives => _drives.Keys.Order();

    /// <summary>
    /// Marks the drive's CDS entry as one we serve, which is the entire act
    /// of installing a redirector on a letter.
    ///
    /// Both of the top two bits go in, and the second one is not obvious.
    /// Bit 15 is what diverts file operations to INT 2Fh function 11h, and
    /// looks like the whole answer. But MS-DOS decides separately whether a
    /// letter may be selected at all, and that test wants bit 14 -- nominally
    /// "there is a physical drive here" -- set as well: with bit 15 alone,
    /// typing the letter gets "Invalid drive specification" and the
    /// redirector is never consulted about anything, because DOS refuses the
    /// drive before it gets that far.
    ///
    /// Reading bit 14 as literally meaning "a real disk" is what makes this
    /// surprising, and it is the wrong reading. What DOS actually takes from
    /// it is "this entry describes a usable drive", and a redirected drive is
    /// one. MSCDEX set both bits too, which is why a CD could be selected.
    /// </summary>
    private void MarkDriveRedirected(byte drive)
    {
        if (!IsInstalled) return;

        // Past the end of the array there is no entry to mark, only whatever
        // DOS put next in memory.
        if (drive >= DriveCount) return;

        uint entry = CurrentDirectoryStructure + (uint)(drive * DosStructures.CdsEntrySize);
        ushort flags = ReadWordAt(entry + DosStructures.CdsFlags);

        flags |= DosStructures.CdsRedirectedFlag | DosStructures.CdsValidFlag;
        WriteWordAt(entry + DosStructures.CdsFlags, flags);

        // The path has to name the drive we are pretending to be, because DOS
        // shows it to the user and parses it back when resolving relative
        // names. Anything else and a CHDIR onto the drive lands somewhere
        // unrelated.
        WriteAsciiz(entry + DosStructures.CdsPath, $"{(char)('A' + drive)}:\\");
        WriteWordAt(entry + DosStructures.CdsRootLength, 2);
    }

    private void ClearDriveRedirected(byte drive)
    {
        if (!IsInstalled) return;
        if (drive >= DriveCount) return;

        uint entry = CurrentDirectoryStructure + (uint)(drive * DosStructures.CdsEntrySize);
        ushort flags = ReadWordAt(entry + DosStructures.CdsFlags);
        WriteWordAt(entry + DosStructures.CdsFlags,
            (ushort)(flags & ~(DosStructures.CdsRedirectedFlag | DosStructures.CdsValidFlag)));
    }

    /// <summary>
    /// Re-applies the redirected flag to every mounted drive. Called once the
    /// SDA and CDS addresses are known, since a drive may well have been
    /// mounted before DOS was even loaded.
    /// </summary>
    private void ReapplyMounts()
    {
        foreach (byte drive in _drives.Keys) MarkDriveRedirected(drive);
    }

    // --- the protocol ----------------------------------------------------------

    /// <summary>
    /// Handles an INT 2Fh AH=11h call. Returns false when the subfunction is
    /// one we do not implement or the drive is not ours, so the caller leaves
    /// AX untouched -- which is how INT 2Fh says nobody answered.
    /// </summary>
    public bool Handle(CpuNs.Cpu cpu)
    {
        byte subfunction = cpu.State.GetReg8(0);   // AL
        Trace?.Invoke(subfunction, CurrentDrive());

        // The installation check is answered whether or not anything is
        // mounted: it says a redirector exists, not that a particular drive
        // does. A program that asks and gets no answer concludes the whole
        // interface is absent.
        if (subfunction == 0x00) return ReportInstalled(cpu);

        if (!IsInstalled) return false;

        bool handled = Dispatch(cpu, subfunction);
        TraceResult?.Invoke(subfunction, handled, cpu.State.Ax);
        return handled;
    }

    private bool Dispatch(CpuNs.Cpu cpu, byte subfunction)
    {
        return subfunction switch
        {
            0x01 => RemoveDirectory(cpu),
            0x03 => MakeDirectory(cpu),
            0x06 => CloseFile(cpu),
            0x07 => CommitFile(cpu),
            0x08 => ReadFile(cpu),
            0x09 => WriteFile(cpu),
            0x0C => GetDiskSpace(cpu),
            0x0E => SetFileAttributes(cpu),
            0x0F => GetFileAttributes(cpu),
            // 11h renames and 13h deletes -- the reverse of what one would
            // guess from DOS's own numbering, where 41h deletes and 56h
            // renames. Having them swapped made DEL rename a file to the
            // garbage in the second name buffer, and report "access denied".
            0x11 => RenameFile(cpu),
            0x13 => DeleteFile(cpu),
            0x16 => OpenFileCall(cpu),
            0x17 => CreateFileCall(cpu),
            0x05 => ChangeDirectory(cpu),

            // 18h and 19h are create and findfirst "without CDS": the same
            // operations, reached when DOS has no CDS entry for the name --
            // a UNC name, or one a redirector chose to canonicalise itself
            // through 23h. The drive then comes from the name, which
            // TryResolve falls back to.
            0x18 => CreateFileCall(cpu),
            0x19 => FindFirst(cpu),
            0x1B => FindFirst(cpu),
            0x1C => FindNext(cpu),
            0x21 => SeekFromEnd(cpu),
            0x23 => QualifyFilename(cpu),

            // 2Eh, not 1Eh, is the extended open of INT 21h AH=6Ch, which
            // COMMAND.COM uses for TYPE and COPY. 1Eh is "do redirection",
            // the INT 21h 5Fxx family, which is declined below. Having them
            // the other way round answered every TYPE with "not supported".
            0x2E => SpecialOpen(cpu),

            // Deliberately not implemented, and why it is safe not to:
            //
            //   02h  remove directory, second half of a two-step protocol
            //        that only DOS 3 used.
            //   04h  make directory, likewise.
            //   0Ah  lock region, 0Bh unlock region -- record locking, which
            //        needs SHARE loaded and which nothing does on a personal
            //        machine with one program running.
            //   1Dh  close all files for a process, used at abnormal
            //        termination; leaving a handle open costs nothing here
            //        because we hold no host resources between calls.
            //   1Eh  do redirection (INT 21h 5Fxx), 1Fh printer setup, 20h
            //        flush all buffers, 22h process termination, 24h-27h the
            //        printer family; 25h arrives at the end of every command
            //        and is exactly as unanswered without a redirector loaded.
            //
            // Declining is what DOS's own INT 2Fh handler does for every
            // function 11h it is asked: carry set and AX=1. The multiplex
            // hook produces that answer, so that a declined call looks the
            // same as it did before the vector was taken over. What it must
            // not do is leave the registers as they were: the caller passed
            // carry clear, and DOS reads carry clear back as success.
            _ => false,
        };
    }

    /// <summary>
    /// Subfunction 00: the installation check. AL=FFh means a redirector is
    /// present.
    /// </summary>
    private static bool ReportInstalled(CpuNs.Cpu cpu)
    {
        cpu.State.SetReg8(0, 0xFF);
        return true;
    }

    /// <summary>
    /// Subfunction 2Eh: "do redirection", the call that adds and removes
    /// network mappings -- what NET USE did.
    ///
    /// We answer it only to say the operation is not supported, because the
    /// mapping here is made from the host side rather than from inside DOS.
    /// Answering matters all the same: a program that gets silence concludes
    /// the redirector is broken, whereas a clean "not supported" is a normal
    /// answer that every network utility knows how to print.
    /// </summary>
    /// <summary>
    /// Subfunction 23h: qualify a filename. A redirector may rewrite a name
    /// before DOS parses it -- Novell used it to turn "SERVER/VOL:" into
    /// something DOS could hold.
    ///
    /// We have nothing to rewrite: our names are ordinary DOS names. But the
    /// way to say so is to answer successfully and leave the buffer alone,
    /// NOT to decline.
    ///
    /// Declining looks right -- "we did not handle it, let DOS parse it
    /// itself" -- and it is what this used to do. It is wrong, and DIR was
    /// the proof: DOS does not fall back to its own parsing here. It takes
    /// the unanswered call as a failed one, reports "invalid function" and
    /// abandons the operation, so a directory listing died before FindFirst
    /// was ever reached. The drive existed, answered its label, and then
    /// refused to list anything.
    /// </summary>
    private bool QualifyFilename(CpuNs.Cpu cpu)
    {
        // DS:SI is the name to canonicalise, ES:DI a buffer for the result.
        // Answering with a canonical name is what a redirector for names DOS
        // cannot parse does -- Novell's "SERVER/VOL:" -- and it has a cost:
        // DOS then treats the name as one with no CDS entry, and reaches for
        // the "without CDS" variants of every call, current directory
        // included. Our names are ordinary DOS names on an ordinary drive
        // letter with a CDS of its own, so the right answer is the one DOS's
        // default handler gives: not resolved, carry set, and DOS
        // canonicalises the name itself against that drive's CDS.
        //
        // Two wrong answers preceded this one, both measured. Reading the
        // name from the SDA found garbage there (it is in DS:SI), and
        // declining by leaving the registers alone was read as success,
        // because the carry DOS passed in was clear.
        return false;
    }

    // --- directory operations ---------------------------------------------------

    /// <summary>
    /// Subfunction 05: change directory. DOS is asking whether the path
    /// exists and is a directory; it updates the CDS itself once we say yes.
    ///
    /// Note what is NOT our job here: DOS has already resolved "..", already
    /// merged the path with the current directory, and already checked the
    /// length. All that reaches us is an absolute path to verify.
    /// </summary>
    private bool ChangeDirectory(CpuNs.Cpu cpu)
    {
        if (!TryResolve(cpu, out IRedirectedFileSystem? fs, out string path)) return false;

        RedirectedFileInfo? info = fs.GetInfo(path);
        if (info is null || !info.IsDirectory)
        {
            Fail(cpu, DosStructures.ErrorPathNotFound);
            return true;
        }

        Succeed(cpu);
        return true;
    }

    private bool MakeDirectory(CpuNs.Cpu cpu)
    {
        if (!TryResolve(cpu, out IRedirectedFileSystem? fs, out string path)) return false;

        if (fs.IsReadOnly) { Fail(cpu, DosStructures.ErrorAccessDenied); return true; }
        if (fs.GetInfo(path) is not null) { Fail(cpu, DosStructures.ErrorAccessDenied); return true; }

        return TryWrite(cpu, () => fs.CreateDirectory(path));
    }

    private bool RemoveDirectory(CpuNs.Cpu cpu)
    {
        if (!TryResolve(cpu, out IRedirectedFileSystem? fs, out string path)) return false;

        if (fs.IsReadOnly) { Fail(cpu, DosStructures.ErrorAccessDenied); return true; }

        RedirectedFileInfo? info = fs.GetInfo(path);
        if (info is null || !info.IsDirectory)
        {
            Fail(cpu, DosStructures.ErrorPathNotFound);
            return true;
        }

        return TryWrite(cpu, () => fs.RemoveDirectory(path));
    }

    // --- searching ---------------------------------------------------------------

    /// <summary>
    /// Subfunction 1Bh: find the first file matching a pattern.
    ///
    /// The pattern arrives in the SDA already chewed into an eleven-byte
    /// directory-entry form, with wildcards turned into question marks: "*.*"
    /// reaches us as eleven question marks, and "REPORT*.TX" as
    /// "REPORT??TX?". That expansion is why DOS's wildcards behave as they do
    /// -- "*" is not a general glob, it is shorthand for "fill the rest of
    /// this field with ?" -- and matching against the expanded form rather
    /// than against the original text is what reproduces the behaviour
    /// exactly.
    ///
    /// The 21-byte search block DOS hands back is where the search identity
    /// must live: findnext gets nothing else to go on. We put a drive number
    /// and a search number there, and keep the actual listing on our side.
    /// </summary>
    private bool FindFirst(CpuNs.Cpu cpu)
    {
        if (!TryResolve(cpu, out IRedirectedFileSystem? fs, out string path)) return false;

        // The path is a directory plus a pattern. Splitting them is our job:
        // DOS resolved the directory part but left the pattern attached.
        int lastSeparator = path.LastIndexOf('\\');
        string directory = lastSeparator <= 0 ? "\\" : path[..lastSeparator];
        string pattern = path[(lastSeparator + 1)..];

        byte attributeMask = ReadByteAt(
            SwappableDataArea + DosStructures.SdaSearchAttributes);

        var matches = new List<RedirectedFileInfo>();

        // The volume label is a search result, not a property. DIR gets the
        // label by asking for a file with the volume-label attribute in the
        // root, and there is nothing else it could ask -- which is why a
        // redirector that ignores this returns a listing with no label line.
        // Not only in the root: DIR asks for the label from whatever
        // directory is current, through an FCB search on that directory,
        // and prints whatever comes back first. A real disk answers with
        // the root's label; so does this.
        if ((attributeMask & (byte)RedirectedFileAttributes.VolumeLabel) != 0
            && !string.IsNullOrEmpty(fs.VolumeLabel))
        {
            matches.Add(new RedirectedFileInfo
            {
                Name = fs.VolumeLabel,
                Attributes = RedirectedFileAttributes.VolumeLabel,
            });
        }

        if (fs.GetInfo(directory) is { } directoryInfo && directoryInfo.IsDirectory)
        {
            matches.AddRange(fs.Enumerate(directory)
                               .Where(e => MatchesPattern(e.Name, pattern))
                               .Where(e => PassesAttributeMask(e, attributeMask)));
        }
        else if (directory != "\\")
        {
            Fail(cpu, DosStructures.ErrorPathNotFound);
            return true;
        }

        if (matches.Count == 0)
        {
            // "No more files" rather than "file not found": DIR prints "File
            // not found" for either, but a program looping over a wildcard
            // distinguishes them, and DOS's own local search returns 12h here.
            Fail(cpu, DosStructures.ErrorNoMoreFiles);
            return true;
        }

        ushort searchId = _nextSearchId++;
        if (_nextSearchId == 0) _nextSearchId = 1;

        _searches[searchId] = new ActiveSearch
        {
            FileSystem = fs,
            Matches = matches,
            Position = 0,
        };

        // Old searches would otherwise accumulate for the life of the
        // machine: DOS has no obligation to tell us a search is finished,
        // because a program is free to simply stop calling findnext.
        PruneSearches(searchId);

        WriteSearchBlock(searchId, DriveNumberOf(cpu, fs), pattern, attributeMask);
        return EmitNextMatch(cpu, searchId);
    }

    /// <summary>The number of the drive a call is about: from the CDS pointer, or from the name when there is none.</summary>
    private byte DriveNumberOf(CpuNs.Cpu cpu, IRedirectedFileSystem fs)
    {
        byte drive = CurrentDrive();
        if (drive != 0xFF) return drive;
        foreach ((byte number, IRedirectedFileSystem mounted) in _drives)
            if (ReferenceEquals(mounted, fs)) return number;
        return 0;
    }

    /// <summary>
    /// Subfunction 1Ch: the next match. The search block in the SDA is the
    /// only thing identifying which search this is.
    /// </summary>
    private bool FindNext(CpuNs.Cpu cpu)
    {
        ushort searchId = ReadWordAt(
            SwappableDataArea + DosStructures.SdaFindFirstBlock + SearchIdOffset);

        if (!_searches.ContainsKey(searchId))
        {
            Fail(cpu, DosStructures.ErrorNoMoreFiles);
            return true;
        }

        return EmitNextMatch(cpu, searchId);
    }

    /// <summary>
    /// Where inside the 21-byte search block we keep our search number.
    ///
    /// Offset 13 is the start of the region DOS treats as belonging to
    /// whoever performed the search; the first thirteen bytes hold the search
    /// template and attribute, which DOS itself reads. Writing over those
    /// would break DOS's own bookkeeping.
    /// </summary>
    private const int SearchIdOffset = 13;

    private void WriteSearchBlock(ushort searchId, byte drive, string pattern, byte attributeMask)
    {
        uint block = SwappableDataArea + DosStructures.SdaFindFirstBlock;

        // Byte 0 is the drive number (A: is 1) with bit 7 set, which is how
        // DOS knows a search belongs to a redirector rather than to a local
        // disk. Get it wrong and findnext is never routed here at all: DOS
        // handles it with the FAT code, on a drive that has no FAT.
        WriteByteAt(block, (byte)(0x80 | (drive + 1)));

        // Bytes 1-11 are the search template in FCB form and byte 12 the
        // attribute mask, and they are not decoration. A search started
        // through an FCB -- which is how COMMAND.COM's DIR lists files --
        // ends with DOS copying these twelve bytes back over the FCB's own
        // drive, name and extension. Leave them zero and the FCB's
        // extension becomes three NULs; DIR then builds its next search with
        // "leave the extension as it is", hands DOS an FCB no name could
        // match, and reports "path not found" for a directory it had just
        // listed the label of. It only ever worked after a search on a local
        // drive had left a real template behind in the same block.
        string template = SearchTemplate(pattern);
        for (int i = 0; i < 11; i++)
            WriteByteAt(block + 1 + (uint)i, (byte)template[i]);
        WriteByteAt(block + 12, attributeMask);

        WriteWordAt(block + SearchIdOffset, searchId);
    }

    /// <summary>
    /// A pattern as an eleven-character FCB template: name padded to eight,
    /// extension to three, and "*" spelt out as question marks to the end
    /// of its field, which is how DOS itself writes wildcards.
    /// </summary>
    internal static string SearchTemplate(string pattern)
    {
        int dot = pattern.IndexOf('.');
        string name = dot < 0 ? pattern : pattern[..dot];
        string extension = dot < 0 ? "" : pattern[(dot + 1)..];
        return Expand(name, 8) + Expand(extension, 3);

        static string Expand(string part, int width)
        {
            var builder = new StringBuilder(width);
            foreach (char c in part)
            {
                if (builder.Length == width) break;
                if (c == '*')
                {
                    while (builder.Length < width) builder.Append('?');
                    break;
                }
                builder.Append(char.ToUpperInvariant(c));
            }
            while (builder.Length < width) builder.Append(' ');
            return builder.ToString();
        }
    }

    private bool EmitNextMatch(CpuNs.Cpu cpu, ushort searchId)
    {
        ActiveSearch search = _searches[searchId];

        if (search.Position >= search.Matches.Count)
        {
            _searches.Remove(searchId);
            Fail(cpu, DosStructures.ErrorNoMoreFiles);
            return true;
        }

        RedirectedFileInfo match = search.Matches[search.Position++];
        WriteDirectoryEntry(SwappableDataArea + DosStructures.SdaFoundDirectoryEntry, match);

        Succeed(cpu);
        return true;
    }

    /// <summary>
    /// Keeps the table of live searches from growing without bound. Anything
    /// but a handful of recent searches is abandoned state.
    /// </summary>
    private void PruneSearches(ushort keep)
    {
        const int limit = 32;
        if (_searches.Count <= limit) return;

        foreach (ushort id in _searches.Keys.Where(id => id != keep)
                                            .Take(_searches.Count - limit).ToList())
            _searches.Remove(id);
    }

    /// <summary>
    /// Writes a 32-byte directory entry in the FAT layout, which is the form
    /// DOS hands back from a search and expects from us.
    ///
    /// The layout has survived unchanged since 1980 and every DOS program
    /// parses it directly, so the offsets are not negotiable: eleven bytes of
    /// blank-padded name, attribute, ten reserved, time, date, starting
    /// cluster, size.
    /// </summary>
    private void WriteDirectoryEntry(uint address, RedirectedFileInfo info)
    {
        string padded = PadToDirectoryEntry(info.Name);
        for (int i = 0; i < 11; i++)
            WriteByteAt(address + (uint)i, (byte)padded[i]);

        WriteByteAt(address + 11, (byte)info.Attributes);

        for (uint i = 12; i < 22; i++)
            WriteByteAt(address + i, 0);

        WriteWordAt(address + 22, info.PackedTime);
        WriteWordAt(address + 24, info.PackedDate);

        // The starting cluster. There is no cluster: nothing behind this
        // drive has a FAT. Zero is what MSCDEX wrote too, and no program
        // looks at it for a file it did not open by cluster.
        WriteWordAt(address + 26, 0);

        uint size = (uint)Math.Min(info.Size, uint.MaxValue);
        WriteWordAt(address + 28, (ushort)size);
        WriteWordAt(address + 30, (ushort)(size >> 16));
    }

    /// <summary>
    /// Turns "COMMAND.COM" into the eleven-byte "COMMAND COM" of a directory
    /// entry: name blank-padded to eight, extension blank-padded to three,
    /// and no dot between them. The dot in a DOS filename never existed on
    /// disk -- it is punctuation the command interpreter adds when printing.
    /// </summary>
    internal static string PadToDirectoryEntry(string name)
    {
        // A volume label is stored whole across all eleven bytes: it is one
        // field, not a name and an extension, so a label with a dot in it
        // must not be split.
        int dot = name.IndexOf('.');
        string stem = dot >= 0 ? name[..dot] : name;
        string extension = dot >= 0 ? name[(dot + 1)..] : "";

        if (stem.Length > 8) stem = stem[..8];
        if (extension.Length > 3) extension = extension[..3];

        return (stem.PadRight(8) + extension.PadRight(3)).ToUpperInvariant();
    }

    /// <summary>
    /// Matches a name against a DOS search pattern in the eleven-byte form.
    ///
    /// Both sides are padded to the directory-entry layout first, and then it
    /// is a straight character-by-character comparison with '?' matching
    /// anything. That is exactly how DOS did it, and it explains the
    /// behaviour that surprises people: "*.*" matches a file with no
    /// extension, because the extension field is blanks and '?' matches a
    /// blank too.
    /// </summary>
    internal static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return true;

        string paddedName = PadToDirectoryEntry(name);
        string paddedPattern = ExpandWildcards(pattern);

        for (int i = 0; i < 11; i++)
        {
            if (paddedPattern[i] == '?') continue;
            if (paddedPattern[i] != paddedName[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// Expands a pattern into the eleven-byte form, turning '*' into question
    /// marks that fill the rest of the field it appears in.
    ///
    /// This is why "A*B.TXT" matches every file starting with A: the star
    /// swallows the remainder of the name field and the B is never compared.
    /// DOS did not consider that a bug; it is a direct consequence of doing
    /// the expansion at parse time rather than matching a glob at compare
    /// time.
    /// </summary>
    internal static string ExpandWildcards(string pattern)
    {
        int dot = pattern.IndexOf('.');
        string stem = dot >= 0 ? pattern[..dot] : pattern;
        string extension = dot >= 0 ? pattern[(dot + 1)..] : "";

        return (ExpandField(stem, 8) + ExpandField(extension, 3)).ToUpperInvariant();
    }

    private static string ExpandField(string field, int width)
    {
        var builder = new StringBuilder(width);

        foreach (char c in field)
        {
            if (builder.Length == width) break;

            if (c == '*')
            {
                while (builder.Length < width) builder.Append('?');
                break;
            }

            builder.Append(c);
        }

        return builder.ToString().PadRight(width);
    }

    /// <summary>
    /// Applies the DOS attribute mask, whose logic is not "show me these
    /// attributes" but "as well as ordinary files, show me these".
    ///
    /// Hidden, system and directory are the three that can be asked for;
    /// read-only and archive are never filtered on, because every file has
    /// them or not for reasons that have nothing to do with whether the user
    /// wants to see it. And a volume label is different again: asking for it
    /// means you want it INSTEAD of everything else, which is how DIR gets
    /// the label on a line of its own.
    /// </summary>
    private static bool PassesAttributeMask(RedirectedFileInfo info, byte mask)
    {
        var attributes = info.Attributes;

        if ((attributes & RedirectedFileAttributes.VolumeLabel) != 0)
            return (mask & (byte)RedirectedFileAttributes.VolumeLabel) != 0;

        // The mask is inclusive -- hidden, system and directory entries
        // appear only if asked for, ordinary files always -- with one
        // exception DOS has kept since 2.0: a mask of exactly the volume
        // label bit means "labels only". DIR relies on it: it asks with
        // that mask and prints the first name it gets as the label.
        if (mask == (byte)RedirectedFileAttributes.VolumeLabel) return false;

        const byte selective = (byte)(RedirectedFileAttributes.Hidden
                                    | RedirectedFileAttributes.System
                                    | RedirectedFileAttributes.Directory);

        byte required = (byte)((byte)attributes & selective);
        return (required & ~mask) == 0;
    }

    // --- opening and reading ------------------------------------------------------

    /// <summary>
    /// Subfunction 16h: open an existing file. The SFT entry to fill in is at
    /// ES:DI.
    /// </summary>
    private bool OpenFileCall(CpuNs.Cpu cpu)
    {
        if (!TryResolve(cpu, out IRedirectedFileSystem? fs, out string path)) return false;

        RedirectedFileInfo? info = fs.GetInfo(path);
        if (info is null || info.IsDirectory)
        {
            Fail(cpu, DosStructures.ErrorFileNotFound);
            return true;
        }

        FillSftForOpenFile(cpu, fs, path, info, ClassicOpenMode());
        Succeed(cpu);
        return true;
    }

    /// <summary>
    /// Subfunction 17h: create or truncate a file.
    /// </summary>
    private bool CreateFileCall(CpuNs.Cpu cpu)
    {
        if (!TryResolve(cpu, out IRedirectedFileSystem? fs, out string path)) return false;

        if (fs.IsReadOnly)
        {
            Fail(cpu, DosStructures.ErrorAccessDenied);
            return true;
        }

        var attributes = (RedirectedFileAttributes)cpu.State.GetReg8(1);   // CL

        try
        {
            fs.Create(path, attributes);
        }
        catch (Exception e) when (IsHostFileError(e))
        {
            Fail(cpu, DosStructures.ErrorAccessDenied);
            return true;
        }

        RedirectedFileInfo info = fs.GetInfo(path) ?? new RedirectedFileInfo
        {
            Name = NameOf(path),
            Attributes = attributes,
            Size = 0,
        };

        FillSftForOpenFile(cpu, fs, path, info, ReadWriteMode);
        Succeed(cpu);
        return true;
    }

    /// <summary>
    /// Subfunction 1Eh: the "special" open, which is the one that actually
    /// gets used from DOS 4 onwards.
    ///
    /// It is a single entry point for open, create and truncate, with the
    /// choice encoded in the action code in the SDA's open mode: what to do
    /// if the file exists, and what to do if it does not. That combined form
    /// is what INT 21h AX=6C00h exposes to programs, and it exists because
    /// the older calls could not express "open it if it is there, create it
    /// if it is not" without a race.
    ///
    /// The result code goes in CX: 1 for opened, 2 for created, 3 for
    /// replaced. A caller that asked to be told which happened reads it.
    /// </summary>
    private bool SpecialOpen(CpuNs.Cpu cpu)
    {
        if (!TryResolve(cpu, out IRedirectedFileSystem? fs, out string path)) return false;

        // The action code: bits 0-3 say what to do when the file exists,
        // bits 4-7 when it does not. DOS keeps it in the SDA, not in DX;
        // DOS 6.22 also sets a bit above those for the ordinary opens it
        // routes through here, which changes nothing.
        ushort action = ReadWordAt(SwappableDataArea + DosStructures.SdaExtendedOpenAction);
        bool openIfExists = (action & 0x01) != 0;
        bool replaceIfExists = (action & 0x02) != 0;
        bool createIfMissing = (action & 0x10) != 0;

        RedirectedFileInfo? info = fs.GetInfo(path);

        if (info is not null && info.IsDirectory)
        {
            Fail(cpu, DosStructures.ErrorAccessDenied);
            return true;
        }

        if (info is null)
        {
            if (!createIfMissing || fs.IsReadOnly)
            {
                Fail(cpu, createIfMissing ? DosStructures.ErrorAccessDenied
                                          : DosStructures.ErrorFileNotFound);
                return true;
            }

            try { fs.Create(path, RedirectedFileAttributes.Archive); }
            catch (Exception e) when (IsHostFileError(e))
            {
                Fail(cpu, DosStructures.ErrorAccessDenied);
                return true;
            }

            info = fs.GetInfo(path) ?? new RedirectedFileInfo { Name = NameOf(path) };
            cpu.State.Cx = 2;
        }
        else if (replaceIfExists)
        {
            if (fs.IsReadOnly) { Fail(cpu, DosStructures.ErrorAccessDenied); return true; }

            try { fs.SetLength(path, 0); }
            catch (Exception e) when (IsHostFileError(e))
            {
                Fail(cpu, DosStructures.ErrorAccessDenied);
                return true;
            }

            info = fs.GetInfo(path) ?? info;
            cpu.State.Cx = 3;
        }
        else if (openIfExists)
        {
            cpu.State.Cx = 1;
        }
        else
        {
            Fail(cpu, DosStructures.ErrorFileExists);
            return true;
        }

        FillSftForOpenFile(cpu, fs, path, info, ExtendedOpenMode());
        Succeed(cpu);
        return true;
    }

    /// <summary>
    /// Writes the System File Table entry for a file we have just opened.
    ///
    /// The four spare bytes at offset 7 are where our own handle goes. They
    /// exist for exactly this: a network redirector needed somewhere to keep
    /// a file identifier, and DOS reserved the space rather than forcing every
    /// redirector to maintain a side table keyed by SFT address -- which
    /// would break the moment DOS moved an entry.
    /// </summary>
    /// <summary>The open mode of an ordinary open (16h), as DOS left it in the SDA.</summary>
    private ushort ClassicOpenMode() =>
        ReadByteAt(SwappableDataArea + DosStructures.SdaOpenMode);

    /// <summary>The open mode of an extended open (2Eh), from its own field in the SDA.</summary>
    private ushort ExtendedOpenMode() =>
        ReadWordAt(SwappableDataArea + DosStructures.SdaExtendedOpenMode);

    /// <summary>A file just created is open for reading and writing: that is what create means.</summary>
    private const ushort ReadWriteMode = 0x02;

    /// <param name="mode">
    /// The open mode to record in the SFT. It matters more than it looks:
    /// DOS checks the access bits itself on every write, before calling
    /// anybody, and a file recorded as read-only is refused with "access
    /// denied" without the redirector hearing of it. Reading the mode from
    /// the classic open's field for every kind of open left every created
    /// file at zero bytes.
    /// </param>
    private void FillSftForOpenFile(CpuNs.Cpu cpu, IRedirectedFileSystem fs,
                                    string path, RedirectedFileInfo info, ushort mode)
    {
        uint sft = cpu.State.Di;
        byte drive = DriveNumberOf(cpu, fs);

        uint fileId = _nextFileId++;
        _openFiles[fileId] = new OpenFile { FileSystem = fs, Path = path, Drive = drive };

        // Bit 15 of the open mode means "opened through an FCB", which
        // this was not; the rest is the mode as given.
        cpu.WriteMemoryWord(CpuNs.SegReg.Es, sft + DosStructures.SftOpenMode, (ushort)(mode & 0x7F));

        cpu.WriteMemoryWord(CpuNs.SegReg.Es, sft + DosStructures.SftHandleCount, 1);
        cpu.WriteMemoryByte(CpuNs.SegReg.Es, sft + DosStructures.SftAttributes,
                            (byte)info.Attributes);

        // The device information word is where "remote" lives: bit 15. DOS
        // reads it on every read, write, seek and close to decide whether
        // to come to us or to walk the FAT, and the FAT it walks otherwise
        // is the real disk's, with the cluster number it finds in an SFT we
        // never filled -- which is how a TYPE on a shared folder came to
        // read sectors of C: over the top of DOS's own data and leave the
        // machine with no current drive. Bit 6 says the file has not been
        // written; bit 7 clear says it is a file and not a character device;
        // the low six bits are the drive.
        cpu.WriteMemoryWord(CpuNs.SegReg.Es, sft + DosStructures.SftDeviceInfo,
                            (ushort)(0x8040 | (drive & 0x3F)));

        WriteDword(cpu, sft + DosStructures.SftDeviceDriverPointer, fileId);

        WriteDword(cpu, sft + DosStructures.SftFileSize, (uint)Math.Min(info.Size, uint.MaxValue));
        WriteDword(cpu, sft + DosStructures.SftFilePosition, 0);

        cpu.WriteMemoryWord(CpuNs.SegReg.Es, sft + DosStructures.SftTime, info.PackedTime);
        cpu.WriteMemoryWord(CpuNs.SegReg.Es, sft + DosStructures.SftDate, info.PackedDate);

        string padded = PadToDirectoryEntry(info.Name);
        for (int i = 0; i < 11; i++)
            cpu.WriteMemoryByte(CpuNs.SegReg.Es, sft + DosStructures.SftFileName + (uint)i,
                                (byte)padded[i]);
    }

    /// <summary>
    /// Subfunction 08: read from an open file.
    ///
    /// CX says how many bytes, the disk transfer address says where they go,
    /// and the file position comes out of the SFT. On return CX must hold how
    /// many bytes were actually read, and the SFT position must have been
    /// advanced by that much -- DOS does not do it for us, and a redirector
    /// that forgets makes every read return the beginning of the file.
    /// </summary>
    private bool ReadFile(CpuNs.Cpu cpu)
    {
        OpenFile? file = FindOpenFile(cpu, out uint sft);
        if (file is null)
        {
            Fail(cpu, DosStructures.ErrorInvalidHandle);
            return true;
        }

        int requested = cpu.State.Cx;
        uint position = ReadDword(cpu, sft + DosStructures.SftFilePosition);

        var buffer = new byte[requested];
        int read;

        try
        {
            read = requested == 0 ? 0 : file.FileSystem.Read(file.Path, position, buffer);
        }
        catch (Exception e) when (IsHostFileError(e))
        {
            Fail(cpu, DosStructures.ErrorAccessDenied);
            return true;
        }

        uint target = DiskTransferAddress();
        for (int i = 0; i < read; i++)
            WriteByteAt(target + (uint)i, buffer[i]);

        WriteDword(cpu, sft + DosStructures.SftFilePosition, position + (uint)read);
        cpu.State.Cx = (ushort)read;

        Succeed(cpu);
        return true;
    }

    /// <summary>
    /// Subfunction 09: write to an open file.
    ///
    /// A write of zero bytes is not a no-op: it is how DOS truncates a file
    /// at the current position, and a program that wants to shorten a file
    /// has no other way to say so.
    /// </summary>
    private bool WriteFile(CpuNs.Cpu cpu)
    {
        OpenFile? file = FindOpenFile(cpu, out uint sft);
        if (file is null)
        {
            Fail(cpu, DosStructures.ErrorInvalidHandle);
            return true;
        }

        if (file.FileSystem.IsReadOnly)
        {
            Fail(cpu, DosStructures.ErrorWriteProtected);
            return true;
        }

        int requested = cpu.State.Cx;
        uint position = ReadDword(cpu, sft + DosStructures.SftFilePosition);

        try
        {
            if (requested == 0)
            {
                file.FileSystem.SetLength(file.Path, position);
            }
            else
            {
                uint source = DiskTransferAddress();
                var buffer = new byte[requested];
                for (int i = 0; i < requested; i++)
                    buffer[i] = ReadByteAt(source + (uint)i);

                file.FileSystem.Write(file.Path, position, buffer);
            }
        }
        catch (Exception e) when (IsHostFileError(e))
        {
            Fail(cpu, DosStructures.ErrorAccessDenied);
            return true;
        }

        uint newPosition = position + (uint)requested;
        WriteDword(cpu, sft + DosStructures.SftFilePosition, newPosition);

        // The size only grows: writing in the middle of a file leaves it as
        // long as it was.
        uint size = ReadDword(cpu, sft + DosStructures.SftFileSize);
        if (newPosition > size || requested == 0)
            WriteDword(cpu, sft + DosStructures.SftFileSize, newPosition);

        cpu.State.Cx = (ushort)requested;
        Succeed(cpu);
        return true;
    }

    /// <summary>
    /// Subfunction 06: close. DOS has already decremented the handle count;
    /// we are only called when it has reached zero.
    /// </summary>
    private bool CloseFile(CpuNs.Cpu cpu)
    {
        OpenFile? file = FindOpenFile(cpu, out uint sft);
        if (file is null)
        {
            Fail(cpu, DosStructures.ErrorInvalidHandle);
            return true;
        }

        _openFiles.Remove(ReadDword(cpu, sft + DosStructures.SftDeviceDriverPointer));
        Succeed(cpu);
        return true;
    }

    /// <summary>
    /// Subfunction 07: commit, i.e. flush this file to the medium.
    ///
    /// Nothing to do: every write here goes straight through to the host,
    /// because we keep no buffers of our own. Answering success rather than
    /// declining matters, since a database program that gets a failure from
    /// commit assumes its data is at risk and refuses to continue.
    /// </summary>
    private bool CommitFile(CpuNs.Cpu cpu)
    {
        if (FindOpenFile(cpu, out _) is null)
        {
            Fail(cpu, DosStructures.ErrorInvalidHandle);
            return true;
        }

        Succeed(cpu);
        return true;
    }

    /// <summary>
    /// Subfunction 21h: seek from the end of the file.
    ///
    /// Only this one direction is a redirector's business. Seeking from the
    /// start or from the current position needs nothing but arithmetic on the
    /// SFT position, which DOS does itself; seeking from the end needs to
    /// know how long the file is on the far side, and only the redirector can
    /// say. On a network drive the answer could have changed since the file
    /// was opened, which is the whole reason for the call.
    /// </summary>
    private bool SeekFromEnd(CpuNs.Cpu cpu)
    {
        OpenFile? file = FindOpenFile(cpu, out uint sft);
        if (file is null)
        {
            Fail(cpu, DosStructures.ErrorInvalidHandle);
            return true;
        }

        long size = file.FileSystem.GetInfo(file.Path)?.Size
                  ?? ReadDword(cpu, sft + DosStructures.SftFileSize);

        // The offset arrives in CX:DX and is a negative displacement from the
        // end, expressed as a positive number: seeking "4 from the end" of a
        // ten-byte file lands at 6.
        uint offset = (uint)((cpu.State.Cx << 16) | cpu.State.Dx);
        long position = Math.Max(0, size - offset);

        WriteDword(cpu, sft + DosStructures.SftFilePosition, (uint)position);

        cpu.State.Dx = (ushort)(position >> 16);
        cpu.State.Ax = (ushort)position;

        Succeed(cpu);
        return true;
    }

    // --- file management ----------------------------------------------------------

    private bool DeleteFile(CpuNs.Cpu cpu)
    {
        if (!TryResolve(cpu, out IRedirectedFileSystem? fs, out string path)) return false;

        if (fs.IsReadOnly) { Fail(cpu, DosStructures.ErrorAccessDenied); return true; }

        // DELETE takes wildcards -- "DEL *.TMP" reaches us as one call with a
        // pattern, not as one call per file, so expanding it is our job.
        int lastSeparator = path.LastIndexOf('\\');
        string directory = lastSeparator <= 0 ? "\\" : path[..lastSeparator];
        string pattern = path[(lastSeparator + 1)..];

        List<RedirectedFileInfo> victims = pattern.Contains('*') || pattern.Contains('?')
            ? fs.Enumerate(directory).Where(e => !e.IsDirectory)
                                     .Where(e => MatchesPattern(e.Name, pattern)).ToList()
            : [];

        if (victims.Count == 0)
        {
            if (fs.GetInfo(path) is null || fs.GetInfo(path)!.IsDirectory)
            {
                Fail(cpu, DosStructures.ErrorFileNotFound);
                return true;
            }

            return TryWrite(cpu, () => fs.Delete(path));
        }

        return TryWrite(cpu, () =>
        {
            foreach (RedirectedFileInfo victim in victims)
                fs.Delete(JoinPath(directory, victim.Name));
        });
    }

    private bool RenameFile(CpuNs.Cpu cpu)
    {
        if (!TryResolve(cpu, out IRedirectedFileSystem? fs, out string fromPath)) return false;

        if (fs.IsReadOnly) { Fail(cpu, DosStructures.ErrorAccessDenied); return true; }

        string qualifiedTarget = ReadAsciiz(SwappableDataArea + DosStructures.SdaSecondFilename);
        string toPath = StripDriveLetter(qualifiedTarget);

        if (fs.GetInfo(fromPath) is null)
        {
            Fail(cpu, DosStructures.ErrorFileNotFound);
            return true;
        }

        if (fs.GetInfo(toPath) is not null)
        {
            Fail(cpu, DosStructures.ErrorAccessDenied);
            return true;
        }

        return TryWrite(cpu, () => fs.Rename(fromPath, toPath));
    }

    /// <summary>
    /// Subfunction 0Fh: get a file's attributes, size and timestamp without
    /// opening it. This is what a program calls to test whether a file exists.
    /// </summary>
    private bool GetFileAttributes(CpuNs.Cpu cpu)
    {
        if (!TryResolve(cpu, out IRedirectedFileSystem? fs, out string path)) return false;

        RedirectedFileInfo? info = fs.GetInfo(path);
        if (info is null)
        {
            Fail(cpu, DosStructures.ErrorFileNotFound);
            return true;
        }

        cpu.State.Ax = (ushort)info.Attributes;
        cpu.State.Bx = (ushort)(info.Size >> 16);
        cpu.State.Di = (ushort)info.Size;
        cpu.State.Cx = info.PackedTime;
        cpu.State.Dx = info.PackedDate;

        Succeed(cpu);
        return true;
    }

    /// <summary>
    /// Subfunction 0Eh: set a file's attributes. Accepted but not acted on
    /// for a writable share, because the DOS attribute bits have no faithful
    /// equivalent on the host and inventing one -- chmod'ing a file because
    /// DOS marked it read-only -- would surprise the user of the host far
    /// more than it would help the program inside DOS.
    /// </summary>
    private bool SetFileAttributes(CpuNs.Cpu cpu)
    {
        if (!TryResolve(cpu, out IRedirectedFileSystem? fs, out string path)) return false;

        if (fs.IsReadOnly) { Fail(cpu, DosStructures.ErrorAccessDenied); return true; }

        if (fs.GetInfo(path) is null)
        {
            Fail(cpu, DosStructures.ErrorFileNotFound);
            return true;
        }

        Succeed(cpu);
        return true;
    }

    /// <summary>
    /// Subfunction 0Ch: how much room is on the drive.
    ///
    /// The answer is returned as clusters, sectors per cluster and bytes per
    /// sector, because in 1981 that was the only vocabulary DOS had. There
    /// are no clusters here, so the numbers are chosen to multiply out to the
    /// right total; the constraint is that DOS holds the cluster count in
    /// sixteen bits, so a large volume has to be described with big clusters
    /// rather than many of them. That is exactly the same arithmetic that
    /// forced FAT16 into 32 KB clusters on a 2 GB disk.
    /// </summary>
    private bool GetDiskSpace(CpuNs.Cpu cpu)
    {
        IRedirectedFileSystem? fs = DriveFromCds();
        if (fs is null) return false;

        (long free, long total) = fs.GetDiskSpace();

        const int bytesPerSector = 512;
        int sectorsPerCluster = 1;
        long totalClusters = total / (bytesPerSector * sectorsPerCluster);

        // The three numbers multiply to the size, in 32-bit arithmetic
        // inside DIR and CHKDSK: 65535 clusters of 64 sectors of 512 bytes
        // is just under 2 GB, and one doubling more takes the product past
        // what a signed 32-bit number holds, which DIR shows as "0 bytes
        // free". A host disk of a terabyte is reported as a full 2 GB one.
        while (totalClusters > 0xFFFF && sectorsPerCluster < 64)
        {
            sectorsPerCluster *= 2;
            totalClusters = total / (bytesPerSector * sectorsPerCluster);
        }

        long freeClusters = free / (bytesPerSector * sectorsPerCluster);

        cpu.State.Ax = (ushort)sectorsPerCluster;
        cpu.State.Bx = (ushort)Math.Min(totalClusters, 0xFFFF);
        cpu.State.Cx = bytesPerSector;
        cpu.State.Dx = (ushort)Math.Min(freeClusters, 0xFFFF);

        Succeed(cpu);
        return true;
    }

    // --- plumbing -------------------------------------------------------------------

    /// <summary>
    /// Works out which drive the current call is about and what path within
    /// it, or returns false if the drive is not ours.
    ///
    /// The drive is not in a register: it is whichever CDS the SDA's
    /// current-CDS pointer is aimed at. Working it back to a drive number
    /// means measuring the distance from the start of the CDS array, which is
    /// arithmetic no sane interface would require -- but it is what DOS
    /// gives us, and every redirector ever written did the same subtraction.
    /// </summary>
    private bool TryResolve(CpuNs.Cpu cpu, out IRedirectedFileSystem fileSystem, out string path)
    {
        fileSystem = null!;
        path = "";

        string qualified = ReadAsciiz(SwappableDataArea + DosStructures.SdaFirstFilename);

        // The drive is where the CDS pointer aims. When it aims nowhere at
        // all -- FFFF:FFFF, which is how the "without CDS" calls arrive --
        // the qualified name still carries the letter, and that is the
        // drive. A pointer that aims at some other drive, or into the middle
        // of an entry, is not the same case: that call is not ours, or the
        // structures have moved, and the name must not be used to guess.
        IRedirectedFileSystem? found = DriveFromCds();
        if (found is null && CdsPointerIsAbsent()) found = DriveFromName(qualified);
        if (found is null) return false;

        fileSystem = found;
        path = StripDriveLetter(qualified);
        return true;
    }

    private bool CdsPointerIsAbsent() =>
        ReadDwordAt(SwappableDataArea + DosStructures.SdaCurrentCdsPointer) == 0xFFFFFFFF;

    private IRedirectedFileSystem? DriveFromName(string qualified)
    {
        if (qualified.Length < 2 || qualified[1] != ':') return null;
        int drive = char.ToUpperInvariant(qualified[0]) - 'A';
        return drive is >= 0 and < 26 ? _drives.GetValueOrDefault((byte)drive) : null;
    }

    private IRedirectedFileSystem? DriveFromCds()
    {
        byte drive = CurrentDrive();
        return drive == 0xFF ? null : _drives.GetValueOrDefault(drive);
    }

    /// <summary>
    /// The drive this call concerns, or 0xFF when it cannot be worked out.
    /// </summary>
    public byte CurrentDriveForTrace => CurrentDrive();

    private byte CurrentDrive()
    {
        uint pointer = ReadDwordAt(SwappableDataArea + DosStructures.SdaCurrentCdsPointer);
        uint address = ((pointer >> 16) << 4) + (pointer & 0xFFFF);

        if (address < CurrentDirectoryStructure) return 0xFF;

        uint distance = address - CurrentDirectoryStructure;
        if (distance % DosStructures.CdsEntrySize != 0) return 0xFF;

        uint drive = distance / DosStructures.CdsEntrySize;
        return drive >= DriveCount ? (byte)0xFF : (byte)drive;
    }

    /// <summary>
    /// Finds the open file an SFT entry at ES:DI refers to, through the
    /// identifier we stored in it when the file was opened.
    /// </summary>
    private OpenFile? FindOpenFile(CpuNs.Cpu cpu, out uint sft)
    {
        sft = cpu.State.Di;
        uint fileId = ReadDword(cpu, sft + DosStructures.SftDeviceDriverPointer);
        return _openFiles.GetValueOrDefault(fileId);
    }

    private uint DiskTransferAddress()
    {
        uint pointer = ReadDwordAt(SwappableDataArea + DosStructures.SdaDiskTransferAddress);
        return ((pointer >> 16) << 4) + (pointer & 0xFFFF);
    }

    /// <summary>
    /// Takes "E:\GAMES\DOOM.EXE" down to "\GAMES\DOOM.EXE". The drive letter
    /// has already served its purpose by the time we get here -- it told DOS
    /// to call us -- and the backend has no idea what letter it is mounted on.
    /// </summary>
    internal static string StripDriveLetter(string qualified)
    {
        string path = qualified.Length >= 2 && qualified[1] == ':' ? qualified[2..] : qualified;
        if (path.Length == 0) return "\\";
        return path.StartsWith('\\') ? path : "\\" + path;
    }

    private static string JoinPath(string directory, string name)
        => directory == "\\" ? "\\" + name : directory + "\\" + name;

    private static string NameOf(string path)
    {
        int separator = path.LastIndexOf('\\');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    /// <summary>
    /// Reports success: carry clear, and nothing else disturbed.
    /// </summary>
    private static void Succeed(CpuNs.Cpu cpu) => cpu.SetCarryInStackedFlags(false);

    /// <summary>
    /// Reports failure with a DOS error code. The code goes both in AX, where
    /// DOS reads it, and into the SDA's error field, which is where INT 21h
    /// AH=59h (get extended error) later fetches it from.
    /// </summary>
    private void Fail(CpuNs.Cpu cpu, ushort error)
    {
        cpu.State.Ax = error;
        cpu.SetCarryInStackedFlags(true);

        if (SwappableDataArea != 0)
            WriteWordAt(SwappableDataArea + DosStructures.SdaErrorCode, error);
    }

    /// <summary>
    /// Runs a write operation, turning a host failure into the DOS error code
    /// that most nearly means the same thing. Returning "access denied" for
    /// everything is not laziness: DOS has no code for "the host filesystem
    /// is full in a way we cannot describe", and access denied is the one a
    /// program is prepared to see from any operation.
    /// </summary>
    private bool TryWrite(CpuNs.Cpu cpu, Action operation)
    {
        try
        {
            operation();
        }
        catch (Exception e) when (IsHostFileError(e))
        {
            Fail(cpu, DosStructures.ErrorAccessDenied);
            return true;
        }

        Succeed(cpu);
        return true;
    }

    private static bool IsHostFileError(Exception e)
        => e is IOException or UnauthorizedAccessException or NotSupportedException
             or ArgumentException;

    private string ReadAsciiz(uint address)
    {
        var builder = new StringBuilder(128);

        for (uint i = 0; i < 128; i++)
        {
            byte c = ReadByteAt(address + i);
            if (c == 0) break;
            builder.Append((char)c);
        }

        return builder.ToString();
    }

    private void WriteAsciiz(uint address, string text)
    {
        for (int i = 0; i < text.Length; i++)
            WriteByteAt(address + (uint)i, (byte)text[i]);
        WriteByteAt(address + (uint)text.Length, 0);
    }

    private uint ReadDwordAt(uint address)
        => (uint)(ReadWordAt(address)
                | (ReadWordAt(address + 2) << 16));

    // --- guest memory ----------------------------------------------------------
    //
    // Every address the redirector handles -- the SDA, the CDS, an SFT, the
    // disk transfer address -- comes from a far pointer, and a far pointer
    // names a linear address, not a physical one. With DOS alone the two
    // are the same. Under Windows in Enhanced mode they are not: the
    // buffer a DOS call from a Windows program reads into is a translation
    // buffer the monitor mapped where it pleased, and data written to the
    // physical address the linear one spells landed in some other page.
    // Notepad opened a file on the shared drive and showed its own name
    // instead of the text: the buffer still held the path from the open.

    private byte ReadByteAt(uint linear) => _machine.Cpu.ReadLinearByte(linear);
    private void WriteByteAt(uint linear, byte value) => _machine.Cpu.WriteLinearByte(linear, value);
    private ushort ReadWordAt(uint linear) => _machine.Cpu.ReadLinearWord(linear);
    private void WriteWordAt(uint linear, ushort value) => _machine.Cpu.WriteLinearWord(linear, value);

    private static uint ReadDword(CpuNs.Cpu cpu, uint offset)
        => (uint)(cpu.ReadMemoryWord(CpuNs.SegReg.Es, offset)
                | (cpu.ReadMemoryWord(CpuNs.SegReg.Es, offset + 2) << 16));

    private static void WriteDword(CpuNs.Cpu cpu, uint offset, uint value)
    {
        cpu.WriteMemoryWord(CpuNs.SegReg.Es, offset, (ushort)value);
        cpu.WriteMemoryWord(CpuNs.SegReg.Es, offset + 2, (ushort)(value >> 16));
    }
}
