using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Video;

/// <summary>
/// Memoria video mappata fra 0xA0000 e 0xBFFFF.
///
/// L'area e' divisa in tre finestre con usi diversi:
///   0xA0000-0xAFFFF  modalita' grafiche
///   0xB0000-0xB7FFF  testo monocromatico (MDA/Hercules)
///   0xB8000-0xBFFFF  testo a colori (CGA/EGA/VGA)
///
/// Ogni scrittura marca la riga corrispondente come da ridisegnare: convertire
/// l'intero schermo a ogni fotogramma sarebbe lavoro sprecato, dato che un
/// prompt DOS cambia poche celle per volta.
/// </summary>
public sealed class VgaMemory : IMemoryDevice
{
    public const uint Base = 0xA0000;
    public const uint Size = 0x20000;    // 128 KB di finestra visibile

    /// <summary>Offset della finestra di testo a colori dentro questo dispositivo.</summary>
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
        if (_vram[offset] == value) return;    // scrittura senza effetto visibile

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
            // Fuori dalla finestra di testo la corrispondenza riga-offset
            // dipende dalla modalita' grafica: si marca tutto.
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

    /// <summary>Accesso diretto, per il BIOS e per il rendering.</summary>
    public Span<byte> GetSpan(uint offset, int length) => _vram.AsSpan((int)offset, length);

    public byte this[uint offset]
    {
        get => _vram[offset];
        set { _vram[offset] = value; MarkDirty(offset); }
    }
}
