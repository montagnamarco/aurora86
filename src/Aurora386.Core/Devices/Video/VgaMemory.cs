using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// Video memory, mapped between 0xA0000 and 0xBFFFF.
///
/// The region is split into three windows with quite different uses:
///   0xA0000-0xAFFFF  graphics modes
///   0xB0000-0xB7FFF  monochrome text (MDA/Hercules)
///   0xB8000-0xBFFFF  colour text (CGA/EGA/VGA)
///
/// Every write marks the affected character row as dirty. Reconverting the
/// whole screen each frame would be almost entirely wasted work: a DOS prompt
/// typically changes a handful of cells between one frame and the next.
/// </summary>
public sealed class VgaMemory : IMemoryDevice
{
    public const uint Base = 0xA0000;
    public const uint Size = 0x20000;    // 128 KB visible through the window

    /// <summary>Offset of the colour text window within this device.</summary>
    public const uint TextWindowOffset = 0x18000;   // 0xB8000 - 0xA0000

    private readonly byte[] _vram = new byte[256 * 1024];
    private readonly bool[] _dirtyRows = new bool[64];
    private bool _anyDirty = true;

    public VgaRegisters Registers { get; }

    public VgaMemory(VgaRegisters registers) => Registers = registers;

    public byte ReadByte(uint offset)
    {
        if (offset >= Size) return 0xFF;
        return _vram[offset];
    }

    public void WriteByte(uint offset, byte value)
    {
        if (offset >= Size) return;
        if (_vram[offset] == value) return;    // no visible change, so no dirty row

        _vram[offset] = value;
        MarkDirty(offset);
    }

    private void MarkDirty(uint offset)
    {
        _anyDirty = true;

        if (offset >= TextWindowOffset)
        {
            uint textOffset = offset - TextWindowOffset;
            int columns = Registers.TextColumns > 0 ? Registers.TextColumns : 80;
            uint row = textOffset / (uint)(columns * 2);
            if (row < _dirtyRows.Length) _dirtyRows[row] = true;
        }
        else
        {
            // Outside the text window the mapping from offset to screen row
            // depends on the current graphics mode, so play safe and mark
            // everything dirty.
            Array.Fill(_dirtyRows, true);
        }
    }

    public bool IsRowDirty(int row) => (uint)row < _dirtyRows.Length && _dirtyRows[row];
    public bool HasChanges => _anyDirty;

    public void ClearDirty()
    {
        Array.Clear(_dirtyRows);
        _anyDirty = false;
    }

    public void MarkAllDirty()
    {
        Array.Fill(_dirtyRows, true);
        _anyDirty = true;
    }

    /// <summary>Direct access, used by the BIOS handlers and by the renderer.</summary>
    public Span<byte> GetSpan(uint offset, int length) => _vram.AsSpan((int)offset, length);

    public byte this[uint offset]
    {
        get => _vram[offset];
        set { _vram[offset] = value; MarkDirty(offset); }
    }
}
