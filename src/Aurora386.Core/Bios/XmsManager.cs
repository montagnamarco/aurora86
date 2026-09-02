using Aurora386.Core.Memory;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Core.Bios;

/// <summary>
/// Gestore della memoria estesa secondo la specifica XMS, cioe' il servizio
/// che su un PC reale fornisce HIMEM.SYS.
///
/// Serve a due cose che DOS 6.22 usa entrambe:
///  - la HMA, i 64 KB appena sopra il megabyte, dove DOS si sposta con
///    DOS=HIGH liberando una quarantina di KB di memoria convenzionale;
///  - i blocchi di memoria estesa (EMB), che i programmi allocano per i dati.
///
/// I programmi non chiamano queste funzioni con una INT: chiedono a INT 2Fh
/// l'indirizzo di un punto di ingresso e poi lo chiamano direttamente. Qui
/// quel punto di ingresso e' un aggancio nativo nella ROM.
/// </summary>
public sealed class XmsManager
{
    private readonly PhysicalMemoryBus _memory;

    /// <summary>Primo indirizzo della HMA: i 64 KB appena sopra il megabyte.</summary>
    private const uint HmaBase = 0x100000;

    /// <summary>
    /// Dimensione utile della HMA. Non sono 64 KB pieni: con un segmento a
    /// 0xFFFF l'offset massimo raggiungibile e' 0xFFFF, quindi l'ultimo
    /// indirizzo utile e' 0x10FFEF -- sedici byte in meno.
    /// </summary>
    private const uint HmaSize = 0xFFF0;

    /// <summary>Primo indirizzo della memoria gestita come estesa.</summary>
    private const uint ExtendedMemoryBase = 0x110000;   // subito sopra la HMA

    /// <summary>Un blocco di memoria estesa allocato da un programma.</summary>
    private sealed class Block
    {
        public uint Base;
        public uint SizeKb;
        public int LockCount;
        public bool InUse;
    }

    private readonly Dictionary<ushort, Block> _blocks = [];
    private ushort _nextHandle = 1;

    /// <summary>Prossimo indirizzo libero nella memoria estesa.</summary>
    private uint _allocationPointer = ExtendedMemoryBase;

    /// <summary>true se la HMA e' stata assegnata a un programma.</summary>
    public bool HmaAllocated { get; private set; }

    public XmsManager(PhysicalMemoryBus memory) => _memory = memory;

    /// <summary>Memoria estesa totale gestibile, in KB.</summary>
    private uint TotalExtendedKb =>
        _memory.RamSize > ExtendedMemoryBase ? (_memory.RamSize - ExtendedMemoryBase) / 1024 : 0;

    /// <summary>Memoria estesa ancora libera, in KB.</summary>
    private uint FreeExtendedKb =>
        _memory.RamSize > _allocationPointer ? (_memory.RamSize - _allocationPointer) / 1024 : 0;

    /// <summary>
    /// Punto di ingresso XMS. La funzione richiesta sta in AH; l'esito e' in
    /// AX (1 riuscito, 0 fallito) con il codice di errore in BL.
    /// </summary>
    public void Handle(CpuNs.Cpu cpu)
    {
        byte function = cpu.State.GetReg8(4);   // AH

        switch (function)
        {
            case 0x00: GetVersion(cpu); return;
            case 0x01: RequestHma(cpu); return;
            case 0x02: ReleaseHma(cpu); return;
            case 0x03: GlobalEnableA20(cpu); return;
            case 0x04: GlobalDisableA20(cpu); return;
            case 0x05: LocalEnableA20(cpu); return;
            case 0x06: LocalDisableA20(cpu); return;
            case 0x07: QueryA20(cpu); return;
            case 0x08: QueryFreeMemory(cpu); return;
            case 0x09: AllocateBlock(cpu); return;
            case 0x0A: FreeBlock(cpu); return;
            case 0x0B: MoveBlock(cpu); return;
            case 0x0C: LockBlock(cpu); return;
            case 0x0D: UnlockBlock(cpu); return;
            case 0x0E: GetHandleInformation(cpu); return;

            default:
                Fail(cpu, 0x80);   // funzione non implementata
                return;
        }
    }

    private void GetVersion(CpuNs.Cpu cpu)
    {
        cpu.State.Ax = 0x0300;    // XMS 3.0, in cifre BCD
        cpu.State.Bx = 0x0001;    // versione interna del gestore
        cpu.State.Dx = 0x0001;    // la HMA esiste
    }

    /// <summary>
    /// Funzione 0x01: assegna la HMA a chi la chiede. DOS la richiede all'avvio
    /// quando CONFIG.SYS contiene DOS=HIGH, e ci sposta il proprio nucleo.
    /// </summary>
    private void RequestHma(CpuNs.Cpu cpu)
    {
        if (HmaAllocated) { Fail(cpu, 0x91); return; }   // gia' assegnata

        // DX dice quanto spazio serve. La HMA e' lunga 0xFFF0 byte, ma il
        // valore 0xFFFF ha un significato speciale: e' quello che usa un
        // sistema operativo per dire "la prendo tutta", ed e' esattamente
        // cio' che fa DOS con DOS=HIGH. Rifiutarlo impedirebbe a DOS di
        // spostarsi in memoria alta.
        ushort requestedBytes = cpu.State.Dx;
        if (requestedBytes != 0xFFFF && requestedBytes > HmaSize) { Fail(cpu, 0xA1); return; }

        HmaAllocated = true;
        // Chi ottiene la HMA ha bisogno che A20 resti accesa per usarla.
        _memory.A20Enabled = true;
        Succeed(cpu);
    }

    private void ReleaseHma(CpuNs.Cpu cpu)
    {
        if (!HmaAllocated) { Fail(cpu, 0x93); return; }
        HmaAllocated = false;
        Succeed(cpu);
    }

    // Le versioni "globali" e "locali" del controllo A20 differiscono solo per
    // il conteggio dei riferimenti, che qui non serve: un solo gestore, nessun
    // conflitto fra chiamanti.

    private void GlobalEnableA20(CpuNs.Cpu cpu)
    {
        _memory.A20Enabled = true;
        Succeed(cpu);
    }

    private void GlobalDisableA20(CpuNs.Cpu cpu)
    {
        // Disabilitare A20 mentre la HMA e' in uso renderebbe irraggiungibile
        // il codice che sta girando: la richiesta va rifiutata.
        if (HmaAllocated) { Fail(cpu, 0x94); return; }
        _memory.A20Enabled = false;
        Succeed(cpu);
    }

    private void LocalEnableA20(CpuNs.Cpu cpu)
    {
        _memory.A20Enabled = true;
        Succeed(cpu);
    }

    private void LocalDisableA20(CpuNs.Cpu cpu)
    {
        if (HmaAllocated) { Fail(cpu, 0x94); return; }
        _memory.A20Enabled = false;
        Succeed(cpu);
    }

    private void QueryA20(CpuNs.Cpu cpu)
    {
        cpu.State.Ax = (ushort)(_memory.A20Enabled ? 1 : 0);
        cpu.State.SetReg8(3, 0);   // BL: nessun errore
    }

    private void QueryFreeMemory(CpuNs.Cpu cpu)
    {
        uint free = FreeExtendedKb;
        // I registri sono a 16 bit: oltre 64 MB il valore viene saturato,
        // che con 16 MB di RAM non e' un problema.
        cpu.State.Ax = (ushort)Math.Min(free, 0xFFFF);      // blocco piu' grande
        cpu.State.Dx = (ushort)Math.Min(TotalExtendedKb, 0xFFFF);
        cpu.State.SetReg8(3, 0);
    }

    /// <summary>
    /// Funzione 0x09: alloca un blocco di memoria estesa. L'allocatore e'
    /// lineare e non riusa lo spazio liberato: e' sufficiente per il modo in
    /// cui DOS e i programmi dell'epoca usano XMS, che allocano poco e
    /// all'avvio.
    /// </summary>
    private void AllocateBlock(CpuNs.Cpu cpu)
    {
        uint requestedKb = cpu.State.Dx;
        if (requestedKb == 0) requestedKb = 1;

        if (requestedKb > FreeExtendedKb) { Fail(cpu, 0xA0); return; }

        var block = new Block
        {
            Base = _allocationPointer,
            SizeKb = requestedKb,
            InUse = true,
        };

        _allocationPointer += requestedKb * 1024;

        ushort handle = _nextHandle++;
        _blocks[handle] = block;

        cpu.State.Ax = 1;
        cpu.State.Dx = handle;
    }

    private void FreeBlock(CpuNs.Cpu cpu)
    {
        ushort handle = cpu.State.Dx;
        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            Fail(cpu, 0xA2);   // handle non valido
            return;
        }
        if (block.LockCount > 0) { Fail(cpu, 0xAB); return; }   // ancora bloccato

        block.InUse = false;
        _blocks.Remove(handle);
        Succeed(cpu);
    }

    /// <summary>
    /// Funzione 0x0B: copia un blocco di memoria. E' il modo in cui i
    /// programmi spostano dati fra memoria convenzionale ed estesa, dato che
    /// in real mode non possono indirizzare quest'ultima direttamente.
    ///
    /// I parametri stanno in una struttura puntata da DS:SI: lunghezza,
    /// handle e offset di origine, handle e offset di destinazione.
    /// </summary>
    private void MoveBlock(CpuNs.Cpu cpu)
    {
        uint parameters = cpu.State.Si;

        uint length = ReadDwordFromDs(cpu, parameters);
        ushort sourceHandle = cpu.ReadMemoryWord(CpuNs.SegReg.Ds, parameters + 4);
        uint sourceOffset = ReadDwordFromDs(cpu, parameters + 6);
        ushort destHandle = cpu.ReadMemoryWord(CpuNs.SegReg.Ds, parameters + 10);
        uint destOffset = ReadDwordFromDs(cpu, parameters + 12);

        // Una lunghezza dispari non e' ammessa dalla specifica.
        if ((length & 1) != 0) { Fail(cpu, 0xA7); return; }
        if (length == 0) { Succeed(cpu); return; }

        if (!TryResolve(sourceHandle, sourceOffset, length, out uint sourceAddress))
        {
            Fail(cpu, (byte)(sourceHandle == 0 ? 0xA7 : 0xA3));
            return;
        }
        if (!TryResolve(destHandle, destOffset, length, out uint destAddress))
        {
            Fail(cpu, (byte)(destHandle == 0 ? 0xA7 : 0xA5));
            return;
        }

        // La copia avviene sul bus per rispettare mappature e sola lettura;
        // l'A20 va forzata accesa perche' gli indirizzi sopra il megabyte
        // altrimenti avvolgerebbero.
        bool previousA20 = _memory.A20Enabled;
        _memory.A20Enabled = true;

        // Copia all'indietro quando le aree si sovrappongono e la destinazione
        // sta piu' avanti, altrimenti si sovrascriverebbe la sorgente.
        if (destAddress > sourceAddress && destAddress < sourceAddress + length)
        {
            for (uint i = length; i > 0; i--)
                _memory.WriteByte(destAddress + i - 1, _memory.ReadByte(sourceAddress + i - 1));
        }
        else
        {
            for (uint i = 0; i < length; i++)
                _memory.WriteByte(destAddress + i, _memory.ReadByte(sourceAddress + i));
        }

        _memory.A20Enabled = previousA20;
        Succeed(cpu);
    }

    /// <summary>
    /// Converte handle e offset in indirizzo fisico. L'handle zero significa
    /// "memoria convenzionale", e in quel caso l'offset e' un puntatore
    /// segmento:offset impacchettato in 32 bit.
    /// </summary>
    private bool TryResolve(ushort handle, uint offset, uint length, out uint address)
    {
        if (handle == 0)
        {
            // Puntatore reale: segmento nella meta' alta, offset in quella bassa.
            ushort segment = (ushort)(offset >> 16);
            ushort within = (ushort)offset;
            address = (uint)segment * 16 + within;
            return address + length <= _memory.RamSize;
        }

        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            address = 0;
            return false;
        }

        if (offset + length > block.SizeKb * 1024)
        {
            address = 0;
            return false;
        }

        address = block.Base + offset;
        return true;
    }

    private void LockBlock(CpuNs.Cpu cpu)
    {
        ushort handle = cpu.State.Dx;
        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            Fail(cpu, 0xA2);
            return;
        }

        block.LockCount++;
        cpu.State.Ax = 1;
        // L'indirizzo fisico del blocco va restituito in DX:BX.
        cpu.State.Dx = (ushort)(block.Base >> 16);
        cpu.State.Bx = (ushort)block.Base;
    }

    private void UnlockBlock(CpuNs.Cpu cpu)
    {
        ushort handle = cpu.State.Dx;
        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            Fail(cpu, 0xA2);
            return;
        }
        if (block.LockCount == 0) { Fail(cpu, 0xAA); return; }

        block.LockCount--;
        Succeed(cpu);
    }

    private void GetHandleInformation(CpuNs.Cpu cpu)
    {
        ushort handle = cpu.State.Dx;
        if (!_blocks.TryGetValue(handle, out Block? block) || !block.InUse)
        {
            Fail(cpu, 0xA2);
            return;
        }

        cpu.State.Ax = 1;
        cpu.State.SetReg8(4, (byte)block.LockCount);            // BH: blocchi attivi
        cpu.State.SetReg8(3, (byte)(0xFF - _blocks.Count));     // BL: handle liberi
        cpu.State.Dx = (ushort)block.SizeKb;
    }

    private uint ReadDwordFromDs(CpuNs.Cpu cpu, uint offset)
        => (uint)(cpu.ReadMemoryWord(CpuNs.SegReg.Ds, offset)
                | (cpu.ReadMemoryWord(CpuNs.SegReg.Ds, offset + 2) << 16));

    private static void Succeed(CpuNs.Cpu cpu)
    {
        cpu.State.Ax = 1;
        cpu.State.SetReg8(3, 0);   // BL: nessun errore
    }

    private static void Fail(CpuNs.Cpu cpu, byte errorCode)
    {
        cpu.State.Ax = 0;
        cpu.State.SetReg8(3, errorCode);   // BL
    }
}
