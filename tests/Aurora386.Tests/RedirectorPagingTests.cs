using Aurora386.Core;
using Aurora386.Core.Cpu;
using Aurora386.Core.Dos;

namespace Aurora386.Tests;

/// <summary>
/// The redirector under a monitor that pages the first megabyte.
///
/// Every address the redirector is handed -- the SDA, the SFT, the disk
/// transfer address -- is a far pointer, and a far pointer names a linear
/// address. With DOS alone linear and physical are the same; under Windows
/// in Enhanced mode a DOS call from a Windows program reads into a
/// translation buffer that the monitor mapped wherever it pleased. The
/// redirector wrote through physical addresses, the data landed in some
/// other page, and Notepad opened a file on the shared drive and showed its
/// own name: the buffer still held the path from the open.
/// </summary>
public sealed class RedirectorPagingTests : IDisposable
{
    private const uint SdaAddress = 0x20000;
    private const uint CdsAddress = 0x30000;
    private const uint SftAddress = 0x4000;
    private const uint DtaLinear = 0x40000;      // where DOS says the buffer is
    private const uint DtaPhysical = 0x70000;    // where the page tables put it
    private const uint PageDirectory = 0x60000;
    private const uint Table = 0x61000;
    private const byte ShareDrive = 4;

    private readonly Machine _machine = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "aurora-redir-paged-" + Guid.NewGuid().ToString("N"));

    public RedirectorPagingTests()
    {
        Directory.CreateDirectory(_root);
        _machine.RunInstructions(200);
        _machine.Redirector.AttachToDos(SdaAddress, CdsAddress, driveCount: 26);
        _machine.Redirector.Mount(ShareDrive, new HostDirectoryFileSystem(_root));

        // The first four megabytes mapped onto themselves, except the page
        // the transfer address points at, which lives somewhere else.
        const uint Flags = 0x07;    // present, writable, user
        _machine.Memory.WriteDword(PageDirectory, Table | Flags);
        for (uint page = 0; page < 1024; page++)
            _machine.Memory.WriteDword(Table + page * 4, (page << 12) | Flags);
        _machine.Memory.WriteDword(Table + (DtaLinear >> 12) * 4, DtaPhysical | Flags);

        CpuState state = _machine.Cpu.State;
        state.Cr3 = PageDirectory;
        state.Cr0 |= CpuState.Cr0ProtectionEnable | CpuState.Cr0Paging;
        for (int i = 0; i < state.Segments.Length; i++)
        {
            state.Segments[i].Base = 0;
            state.Segments[i].Limit = 0xFFFF;
        }
        state.Esp = 0x7000;

        // The SDA fields the call reads: the CDS of the drive, the DTA.
        uint entry = CdsAddress + (uint)(ShareDrive * DosStructures.CdsEntrySize);
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaCurrentCdsPointer, 0);
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaCurrentCdsPointer + 2, (ushort)(entry >> 4));
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaDiskTransferAddress, 0);
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaDiskTransferAddress + 2, (ushort)(DtaLinear >> 4));
    }

    public void Dispose()
    {
        _machine.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Call(byte subfunction, ushort cx = 0)
    {
        CpuState state = _machine.Cpu.State;
        state.Ax = (ushort)(0x1100 | subfunction);
        state.Cx = cx;
        state.Di = (ushort)SftAddress;
        _machine.Redirector.Handle(_machine.Cpu);
    }

    [Fact]
    public void AReadLandsWhereThePageTablesSayTheBufferIs()
    {
        File.WriteAllText(Path.Combine(_root, "NOTE.TXT"), "secondo file");
        string name = $"{(char)('A' + ShareDrive)}:\\NOTE.TXT";
        for (int i = 0; i < name.Length; i++)
            _machine.Memory.WriteByte(SdaAddress + DosStructures.SdaFirstFilename + (uint)i, (byte)name[i]);
        _machine.Memory.WriteByte(SdaAddress + DosStructures.SdaFirstFilename + (uint)name.Length, 0);

        Call(0x16);                 // open
        Call(0x08, cx: 7);          // read

        Assert.Equal(7, _machine.Cpu.State.Cx);
        var got = new char[7];
        for (uint i = 0; i < 7; i++) got[i] = (char)_machine.Memory.ReadByte(DtaPhysical + i);
        Assert.Equal("secondo", new string(got));
        Assert.Equal(0, _machine.Memory.ReadByte(DtaLinear));      // nothing at the physical twin
    }

    [Fact]
    public void AWriteTakesItsBytesFromWhereThePageTablesSayTheBufferIs()
    {
        string name = $"{(char)('A' + ShareDrive)}:\\OUT.TXT";
        for (int i = 0; i < name.Length; i++)
            _machine.Memory.WriteByte(SdaAddress + DosStructures.SdaFirstFilename + (uint)i, (byte)name[i]);
        _machine.Memory.WriteByte(SdaAddress + DosStructures.SdaFirstFilename + (uint)name.Length, 0);
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaExtendedOpenAction, 0x0012);
        _machine.Memory.WriteWord(SdaAddress + DosStructures.SdaExtendedOpenMode, 0x0002);

        for (int i = 0; i < 4; i++) _machine.Memory.WriteByte(DtaPhysical + (uint)i, (byte)"ciao"[i]);
        for (int i = 0; i < 4; i++) _machine.Memory.WriteByte(DtaLinear + (uint)i, (byte)'X');

        Call(0x2E);                 // extended open: create
        Call(0x09, cx: 4);          // write

        Assert.Equal("ciao", File.ReadAllText(Path.Combine(_root, "OUT.TXT")));
    }
}
