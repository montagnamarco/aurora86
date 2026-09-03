namespace Aurora386.Core.Dos;

/// <summary>
/// The layout of the DOS data structures a redirector has to work through.
///
/// None of this was documented by Microsoft. DOS was supposed to be reached
/// only through INT 21h, and the redirector interface was published to a
/// handful of network vendors under agreement -- which is why every offset
/// here was recovered by other people disassembling DOS, and why the whole
/// area became the best-known example of an undocumented interface that
/// everybody nonetheless depended on. The numbers below are the MS-DOS 4.0
/// and later layout, which is what 5.0 and 6.22 use.
///
/// The three structures matter for different reasons.
///
/// The <b>SDA</b> (Swappable Data Area) is DOS's scratchpad: the current
/// directory being worked on, the search template for a wildcard match, the
/// attribute mask, the disk transfer address. It is called swappable because
/// a multitasker had to save and restore it to run two DOS programs at once,
/// and getting its address is the only sanctioned part of this whole
/// interface -- INT 21h AX=5D06 returns it.
///
/// The <b>CDS</b> (Current Directory Structure) is the array with one entry
/// per drive letter, holding the current directory of each. A drive is
/// redirected when bit 15 of its flags word is set: that single bit is what
/// diverts every file operation on that letter away from the FAT code and
/// into INT 2Fh function 11h. Setting it is how a redirector installs itself.
///
/// The <b>SFT</b> (System File Table) is where an open file lives. DOS hands
/// out a handle, the handle indexes a per-process table, and that table
/// indexes into the SFT. The redirector is given a pointer straight to the
/// SFT entry and is expected to maintain the file position and size fields
/// inside it -- DOS does not track them on the redirector's behalf.
/// </summary>
public static class DosStructures
{
    // --- Swappable Data Area ---------------------------------------------------

    /// <summary>
    /// Error code from the last operation. The redirector writes it before
    /// returning with carry set.
    /// </summary>
    public const int SdaErrorCode = 0x0E;

    /// <summary>
    /// The current disk transfer address, where a findfirst/findnext writes
    /// its result. Four bytes: offset then segment.
    /// </summary>
    public const int SdaDiskTransferAddress = 0x0C;

    /// <summary>
    /// The attribute mask for a search or a create -- which of hidden, system
    /// and directory the caller is willing to see.
    /// </summary>
    public const int SdaSearchAttributes = 0x4D;

    /// <summary>The open mode for function 111Eh: read, write or both.</summary>
    public const int SdaOpenMode = 0x4E;

    /// <summary>
    /// The fully qualified name of the file being operated on, as DOS
    /// resolved it: drive letter, colon, then an absolute path. Almost every
    /// subfunction takes its argument from here rather than from a register.
    /// </summary>
    public const int SdaFirstFilename = 0x9E;

    /// <summary>The second name, used by rename.</summary>
    public const int SdaSecondFilename = 0x11E;

    /// <summary>
    /// The 21-byte search block a findfirst hands back and a findnext hands
    /// in. Its first 21 bytes are private to whoever is doing the searching,
    /// which is precisely what lets a redirector keep its own state there.
    /// </summary>
    public const int SdaFindFirstBlock = 0x19E;

    /// <summary>The directory entry a search or an open produces, 32 bytes.</summary>
    public const int SdaFoundDirectoryEntry = 0x1B3;

    /// <summary>
    /// The name a search was asked for, already split into eleven bytes of
    /// name and extension with the wildcards expanded into question marks.
    /// </summary>
    public const int SdaSearchTemplate = 0x9E;

    /// <summary>Where the CDS of the drive being worked on is pointed to from.</summary>
    public const int SdaCurrentCdsPointer = 0x282;

    // --- Current Directory Structure -------------------------------------------

    /// <summary>
    /// Size of one CDS entry in DOS 4.0 and later. DOS 3 used 81 bytes; the
    /// extra seven hold the fields that support SUBST and JOIN.
    /// </summary>
    public const int CdsEntrySize = 88;

    /// <summary>
    /// The current directory as a full path, 67 bytes: "C:\" plus up to 64
    /// characters, ASCIIZ. The 67 is the famous limit on how deep a DOS path
    /// may be, and it is a consequence of this field's size and nothing else.
    /// </summary>
    public const int CdsPath = 0x00;

    /// <summary>The flags word. Bit 15 is what marks a drive as redirected.</summary>
    public const int CdsFlags = 0x43;

    /// <summary>
    /// Length of the part of the path that is the root -- 2 for "C:", more
    /// when SUBST has grafted a subdirectory onto a letter. DOS will not let
    /// a CHDIR .. climb above this point.
    /// </summary>
    public const int CdsRootLength = 0x4F;

    /// <summary>Bit 15 of the CDS flags: this drive is handled by a redirector.</summary>
    public const ushort CdsRedirectedFlag = 0x8000;

    /// <summary>Bit 14: the drive is physical, i.e. a real local disk.</summary>
    public const ushort CdsPhysicalFlag = 0x4000;

    // --- System File Table -----------------------------------------------------

    /// <summary>
    /// How many handles refer to this entry. DOS increments it when a file is
    /// duplicated and decrements it on close; the redirector is only asked to
    /// close the file when the count reaches zero.
    /// </summary>
    public const int SftHandleCount = 0x00;

    /// <summary>
    /// The open mode. Bit 15 set means the file belongs to a redirected
    /// device rather than a local disk -- the redirector sets it on open, and
    /// DOS reads it back to decide whom to route later operations to.
    /// </summary>
    public const int SftOpenMode = 0x02;

    /// <summary>The DOS attribute byte.</summary>
    public const int SftAttributes = 0x04;

    /// <summary>
    /// The device information word. For a redirected file the low six bits
    /// carry the drive number (0 = A:), and bit 15 must be clear -- a set bit
    /// 15 here means a character device, not a file.
    /// </summary>
    public const int SftDeviceInfo = 0x05;

    /// <summary>
    /// Four bytes of storage the redirector may use however it likes. It is
    /// meant for a network's own file identifier, and it is the only per-file
    /// state a redirector gets to keep inside DOS's own tables.
    /// </summary>
    public const int SftDeviceDriverPointer = 0x07;

    /// <summary>The file's size in bytes.</summary>
    public const int SftFileSize = 0x11;

    /// <summary>
    /// The current position within the file. DOS updates it after a read or
    /// a write on a local file, but for a redirected one the redirector is
    /// responsible for advancing it -- forgetting to is the classic bug that
    /// makes a program read the first sector of a file over and over.
    /// </summary>
    public const int SftFilePosition = 0x15;

    /// <summary>The packed time and date, in FAT format.</summary>
    public const int SftTime = 0x0D;
    public const int SftDate = 0x0F;

    /// <summary>The 11-byte name, in the padded directory-entry form.</summary>
    public const int SftFileName = 0x20;

    // --- error codes -----------------------------------------------------------
    // The values DOS puts in AX when a call fails. A redirector returns them
    // in AX with carry set, and DOS passes them straight through to the
    // program that made the original INT 21h call.

    public const ushort ErrorFileNotFound = 0x02;
    public const ushort ErrorPathNotFound = 0x03;
    public const ushort ErrorAccessDenied = 0x05;
    public const ushort ErrorInvalidHandle = 0x06;
    public const ushort ErrorNoMoreFiles = 0x12;
    public const ushort ErrorWriteProtected = 0x13;
    public const ushort ErrorDiskFull = 0x27;
    public const ushort ErrorNotSupported = 0x32;
    public const ushort ErrorFileExists = 0x50;
    public const ushort ErrorDirectoryNotEmpty = 0x10;
}
