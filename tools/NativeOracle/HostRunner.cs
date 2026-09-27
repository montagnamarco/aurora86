using System.Runtime.InteropServices;
using Aurora386.Core.Cpu.Sse;

namespace NativeOracle;

/// <summary>
/// Runs one test instruction on the real CPU: VirtualAlloc's an executable
/// page, assembles <see cref="HostStub"/>'s call around the instruction's
/// bytes into it, and invokes it through a native function pointer with a
/// pointer to a <see cref="HostState"/> as its one argument.
///
/// It also owns the one shared data buffer memory operands address: a page
/// allocated at a fixed, low address so a 32-bit register can hold it both
/// here (64-bit addressing, upper bits zero because the value never grew
/// past 4 KB above the base) and in the emulator's 16 MB bus.
/// </summary>
public sealed unsafe class HostRunner : IDisposable
{
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint PageExecuteReadWrite = 0x40;

    /// <summary>
    /// Candidate fixed addresses for the shared data buffer, all comfortably
    /// below 16 MB so the same address is reachable from the emulator's bus
    /// and fits in any 32-bit register with no sign or truncation surprises.
    /// </summary>
    private static readonly nint[] BufferAddressCandidates =
    [
        0x00200000, 0x00300000, 0x00400000, 0x00500000,
        0x00600000, 0x00700000, 0x00800000, 0x00900000,
        0x00A00000, 0x00B00000, 0x00C00000, 0x00D00000,
        0x00E00000, 0x00F00000,
    ];

    public const int BufferSize = 4096;

    private readonly nint _codePage;
    private readonly int _codePageSize;
    private readonly HostState* _state;

    /// <summary>The address every test instruction's memory operand is based on.</summary>
    public uint BufferAddress { get; }

    public HostRunner()
    {
        _codePageSize = 4096;
        _codePage = VirtualAlloc(0, (nuint)_codePageSize, MemCommit | MemReserve, PageExecuteReadWrite);
        if (_codePage == 0)
        {
            throw new InvalidOperationException($"VirtualAlloc for the code page failed, error {Marshal.GetLastWin32Error()}.");
        }

        BufferAddress = (uint)AllocateFixedBuffer();

        _state = (HostState*)NativeMemory.AlignedAlloc((nuint)HostState.Size, 16);
    }

    private nint AllocateFixedBuffer()
    {
        foreach (nint candidate in BufferAddressCandidates)
        {
            nint address = VirtualAlloc(candidate, BufferSize, MemCommit | MemReserve, PageReadWrite);
            if (address != 0) return address;
        }

        throw new InvalidOperationException("Every candidate address for the shared data buffer was already in use.");
    }

    /// <summary>Writes the buffer's contents directly, for the emulator run to mirror.</summary>
    public void WriteBuffer(ReadOnlySpan<byte> contents)
    {
        var destination = new Span<byte>((void*)BufferAddress, BufferSize);
        contents[..Math.Min(contents.Length, BufferSize)].CopyTo(destination);
    }

    public Span<byte> ReadBuffer() => new((void*)BufferAddress, BufferSize);

    /// <summary>
    /// Runs one instruction with the given initial general registers and
    /// EFLAGS, and returns the resulting registers and EFLAGS. Esp is never
    /// read or written: the harness has no register for it, by design --
    /// no template ever names ESP as an operand.
    ///
    /// <paramref name="useExtendedState"/> asks the stub to FXRSTOR the
    /// reserved image (see <see cref="HostState.FxsaveOffset"/>) before the
    /// test instruction and FXSAVE it back afterwards -- MMX families set up
    /// that image themselves, through <see cref="PrepareFxsaveDefaults"/> and
    /// <see cref="WriteMmxRegisters"/>, before calling this with the flag set.
    /// </summary>
    public (uint[] Gpr, uint Eflags) Run(ReadOnlySpan<byte> instruction, uint[] initialGpr, uint initialEflags, bool useExtendedState = false)
    {
        _state->Eax = initialGpr[(int)Aurora386.Core.Cpu.Reg.Eax];
        _state->Ecx = initialGpr[(int)Aurora386.Core.Cpu.Reg.Ecx];
        _state->Edx = initialGpr[(int)Aurora386.Core.Cpu.Reg.Edx];
        _state->Ebx = initialGpr[(int)Aurora386.Core.Cpu.Reg.Ebx];
        _state->Ebp = initialGpr[(int)Aurora386.Core.Cpu.Reg.Ebp];
        _state->Esi = initialGpr[(int)Aurora386.Core.Cpu.Reg.Esi];
        _state->Edi = initialGpr[(int)Aurora386.Core.Cpu.Reg.Edi];
        _state->Eflags = initialEflags;

        byte[] call = HostStub.BuildInvocation(instruction, useExtendedState);
        if (call.Length > _codePageSize)
        {
            throw new InvalidOperationException($"The assembled call ({call.Length} bytes) no longer fits the code page.");
        }

        Marshal.Copy(call, 0, _codePage, call.Length);
        FlushInstructionCache(GetCurrentProcess(), _codePage, (nuint)call.Length);

        var stub = (delegate* unmanaged<nint, void>)_codePage;
        stub((nint)_state);

        var resultGpr = new uint[8];
        resultGpr[(int)Aurora386.Core.Cpu.Reg.Eax] = _state->Eax;
        resultGpr[(int)Aurora386.Core.Cpu.Reg.Ecx] = _state->Ecx;
        resultGpr[(int)Aurora386.Core.Cpu.Reg.Edx] = _state->Edx;
        resultGpr[(int)Aurora386.Core.Cpu.Reg.Ebx] = _state->Ebx;
        resultGpr[(int)Aurora386.Core.Cpu.Reg.Ebp] = _state->Ebp;
        resultGpr[(int)Aurora386.Core.Cpu.Reg.Esi] = _state->Esi;
        resultGpr[(int)Aurora386.Core.Cpu.Reg.Edi] = _state->Edi;

        return (resultGpr, _state->Eflags);
    }

    /// <summary>
    /// The reserved FXSAVE image as a byte span, so an MMX family can write
    /// its inputs and read its outputs directly at the offsets
    /// <see cref="HostState"/> names.
    /// </summary>
    private Span<byte> FxsaveImage => new((void*)((byte*)_state + HostState.FxsaveOffset), HostState.FxsaveSize);

    /// <summary>
    /// A clean FXSAVE image before an MMX case: FCW at its power-on value,
    /// FSW zero (TOP = 0, no condition or exception bits), every FTW bit set
    /// (every register "in use", as any MMX instruction but EMMS leaves
    /// them), and MXCSR/MXCSR_MASK at their power-on defaults -- SSE state
    /// no MMX family touches, but FXRSTOR still reads it, so it has to be
    /// something valid rather than whatever the previous case left behind.
    /// </summary>
    public void PrepareFxsaveDefaults()
    {
        Span<byte> image = FxsaveImage;
        image.Clear();
        BitConverter.GetBytes((ushort)0x037F).CopyTo(image[HostState.FxsaveFcwOffset..]);
        image[HostState.FxsaveFtwOffset] = 0xFF;
        BitConverter.GetBytes((uint)0x1F80).CopyTo(image[HostState.FxsaveMxcsrOffset..]);
        BitConverter.GetBytes((uint)0xFFFFFFFF).CopyTo(image[HostState.FxsaveMxcsrMaskOffset..]);
    }

    /// <summary>
    /// Seeds MM0-7: the low 64 bits of each ST/MM slot, with the high 16 bits
    /// (the x87 sign and exponent) forced to all ones -- the same "aliased
    /// onto a NaN" arrangement <c>Fpu.WriteMmx</c> gives the emulator's own
    /// register file, so a case that reads a register neither engine wrote
    /// still agrees on what was there.
    /// </summary>
    public void WriteMmxRegisters(ulong[] mmx)
    {
        Span<byte> image = FxsaveImage;
        for (int i = 0; i < 8; i++)
        {
            int offset = HostState.FxsaveMmOffset + i * 16;
            BitConverter.GetBytes(mmx[i]).CopyTo(image[offset..]);
            image[offset + 8] = 0xFF;
            image[offset + 9] = 0xFF;
        }
    }

    /// <summary>MM0-7 after the case ran, read back from the FXSAVE image.</summary>
    public ulong[] ReadMmxRegisters()
    {
        Span<byte> image = FxsaveImage;
        var mmx = new ulong[8];
        for (int i = 0; i < 8; i++)
        {
            mmx[i] = BitConverter.ToUInt64(image.Slice(HostState.FxsaveMmOffset + i * 16, 8));
        }
        return mmx;
    }

    /// <summary>FSW after the case ran: bits 11-13 are TOP, the value the comparer actually checks.</summary>
    public ushort ReadFsw() => BitConverter.ToUInt16(FxsaveImage.Slice(HostState.FxsaveFswOffset, 2));

    /// <summary>The abridged tag word (one bit per register, 1 = not empty) after the case ran.</summary>
    public byte ReadFtw() => FxsaveImage[HostState.FxsaveFtwOffset];

    /// <summary>Writes XMM0-7 into the reserved image before an SSE case, at the same offset (160) Cpu.P6.cs's own FXSAVE/FXRSTOR use.</summary>
    public void WriteXmmRegisters(XmmValue[] xmm)
    {
        Span<byte> image = FxsaveImage;
        for (int i = 0; i < 8; i++)
        {
            int offset = HostState.FxsaveXmmOffset + i * 16;
            BitConverter.GetBytes(xmm[i].D0).CopyTo(image[offset..]);
            BitConverter.GetBytes(xmm[i].D1).CopyTo(image[(offset + 4)..]);
            BitConverter.GetBytes(xmm[i].D2).CopyTo(image[(offset + 8)..]);
            BitConverter.GetBytes(xmm[i].D3).CopyTo(image[(offset + 12)..]);
        }
    }

    /// <summary>XMM0-7 after the case ran.</summary>
    public XmmValue[] ReadXmmRegisters()
    {
        Span<byte> image = FxsaveImage;
        var xmm = new XmmValue[8];
        for (int i = 0; i < 8; i++)
        {
            int offset = HostState.FxsaveXmmOffset + i * 16;
            xmm[i] = new XmmValue
            {
                D0 = BitConverter.ToUInt32(image.Slice(offset, 4)),
                D1 = BitConverter.ToUInt32(image.Slice(offset + 4, 4)),
                D2 = BitConverter.ToUInt32(image.Slice(offset + 8, 4)),
                D3 = BitConverter.ToUInt32(image.Slice(offset + 12, 4)),
            };
        }
        return xmm;
    }

    /// <summary>Writes MXCSR into the reserved image before an SSE case -- the random rounding mode and FZ bit a family picked, always held inside <see cref="Aurora386.Core.Cpu.Sse.SseUnit.MxcsrMask"/> so the host's own FXRSTOR never sees a reserved bit and faults the harness process.</summary>
    public void WriteMxcsr(uint value) => BitConverter.GetBytes(value).CopyTo(FxsaveImage[HostState.FxsaveMxcsrOffset..]);

    /// <summary>MXCSR (flags included) after the case ran.</summary>
    public uint ReadMxcsr() => BitConverter.ToUInt32(FxsaveImage.Slice(HostState.FxsaveMxcsrOffset, 4));

    public void Dispose()
    {
        NativeMemory.AlignedFree(_state);
        VirtualFree(_codePage, 0, MemRelease);
        VirtualFree((nint)BufferAddress, 0, MemRelease);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(nint process, nint baseAddress, nuint size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
