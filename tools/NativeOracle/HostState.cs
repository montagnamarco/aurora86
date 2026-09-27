using System.Runtime.InteropServices;

namespace NativeOracle;

/// <summary>
/// The block of memory the host stub reads its inputs from and writes its
/// outputs to, laid out at fixed byte offsets because the stub is raw
/// machine code addressing it as `[r15+offset]`, not a JIT-compiled access
/// through this type. <see cref="HostStub"/> and this struct must always
/// agree on the numbers.
///
/// Offsets 0x20-0x21F are a reserved, 16-byte-aligned FXSAVE image: a later
/// work package (MMX, SSE) turns on <see cref="HostStub.BuildInvocation"/>'s
/// <c>useExtendedState</c> flag and starts reading and writing it. No family
/// in this first oracle touches the FPU, MMX or XMM state, so the stub never
/// exercises that path yet, but the layout is reserved now so the offsets
/// this struct and the machine code agree on do not have to move later.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct HostState
{
    public const int EaxOffset = 0x00;
    public const int EcxOffset = 0x04;
    public const int EdxOffset = 0x08;
    public const int EbxOffset = 0x0C;
    public const int EbpOffset = 0x10;
    public const int EsiOffset = 0x14;
    public const int EdiOffset = 0x18;
    public const int EflagsOffset = 0x1C;
    public const int FxsaveOffset = 0x20;
    public const int FxsaveSize = 0x200;
    public const int Size = FxsaveOffset + FxsaveSize;

    // Sub-offsets inside the FXSAVE image itself (32-bit legacy layout, SDM
    // Vol.1 table 13-1): the fields work package P2.5's MMX families actually
    // read and write. FCW/FSW/FTW/MXCSR are set to sane, "everything valid"
    // defaults before every MMX case so the host CPU's own architectural rule
    // -- any MMX instruction but EMMS sets TOP<-0, every tag valid -- is what
    // produces the tag word and TOP the comparer reads back, not leftover
    // state from a previous case.
    public const int FxsaveFcwOffset = 0x00;
    public const int FxsaveFswOffset = 0x02;
    public const int FxsaveFtwOffset = 0x04;          // one byte, the abridged tag word
    public const int FxsaveMxcsrOffset = 0x18;
    public const int FxsaveMxcsrMaskOffset = 0x1C;
    public const int FxsaveMmOffset = 0x20;           // + 16*i for MM0..MM7

    /// <summary>
    /// XMM0-7, 160 bytes into the 512-byte image (SDM Vol.2A's FXSAVE
    /// layout table -- the same offset Aurora's own Cpu.P6.cs writes and
    /// reads at). Work package P4.1 (SSE) is what actually reads and writes
    /// this sub-offset; it was already reserved as part of the image
    /// before that, like every other FXSAVE field this struct does not yet
    /// name.
    /// </summary>
    public const int FxsaveXmmOffset = 0xA0;

    [FieldOffset(EaxOffset)] public uint Eax;
    [FieldOffset(EcxOffset)] public uint Ecx;
    [FieldOffset(EdxOffset)] public uint Edx;
    [FieldOffset(EbxOffset)] public uint Ebx;
    [FieldOffset(EbpOffset)] public uint Ebp;
    [FieldOffset(EsiOffset)] public uint Esi;
    [FieldOffset(EdiOffset)] public uint Edi;

    /// <summary>The full 32-bit EFLAGS value; only CF,PF,AF,ZF,SF,DF,OF are ever asked for.</summary>
    [FieldOffset(EflagsOffset)] public uint Eflags;
}
