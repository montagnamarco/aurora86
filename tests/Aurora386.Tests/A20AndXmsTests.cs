using Aurora386.Core;
using Aurora386.Core.Memory;

namespace Aurora386.Tests;

/// <summary>
/// The A20 line and address wrapping at 1 MB. The polarity is the
/// counter-intuitive part: A20 <b>disabled</b> is the compatibility state in
/// which addresses wrap the way they did on the 8086, and the High Memory
/// Area becomes unreachable.
/// </summary>
public class A20Tests
{
    [Fact]
    public void ConA20SpentaGliIndirizziAvvolgonoAUnMegabyte()
    {
        using var bus = new PhysicalMemoryBus();
        bus.A20Enabled = false;

        bus.WriteDword(0x000000, 0x12345678);

        // 0x100000 has bit 20 set, so with A20 off it resolves to 0x000000.
        Assert.Equal(0x12345678u, bus.ReadDword(0x100000));
    }

    [Fact]
    public void ConA20AccesaGliIndirizziSonoDistinti()
    {
        using var bus = new PhysicalMemoryBus();
        bus.A20Enabled = true;

        bus.WriteDword(0x000000, 0x11111111);
        bus.WriteDword(0x100000, 0x22222222);

        Assert.Equal(0x11111111u, bus.ReadDword(0x000000));
        Assert.Equal(0x22222222u, bus.ReadDword(0x100000));
    }

    [Fact]
    public void IlWrappingValeAncheInScrittura()
    {
        // A write above the megabyte with A20 off must land low as well;
        // otherwise reads and writes would disagree about where memory is.
        using var bus = new PhysicalMemoryBus();
        bus.A20Enabled = false;

        bus.WriteWord(0x100000, 0xBEEF);
        Assert.Equal(0xBEEF, bus.ReadWord(0x000000));

        bus.WriteByte(0x100010, 0x42);
        Assert.Equal(0x42, bus.ReadByte(0x000010));

        bus.WriteDword(0x100020, 0xCAFEBABE);
        Assert.Equal(0xCAFEBABEu, bus.ReadDword(0x000020));
    }

    [Fact]
    public void SoloIlBitVentiVieneEscluso()
    {
        // The wrap clears bit 20; it does not truncate to 20 bits. Memory
        // above 2 MB must stay reachable even with A20 disabled.
        using var bus = new PhysicalMemoryBus();
        bus.A20Enabled = true;
        bus.WriteDword(0x200000, 0xAAAAAAAA);   // 2 MB: bit 21, not bit 20

        bus.A20Enabled = false;
        Assert.Equal(0xAAAAAAAAu, bus.ReadDword(0x200000));
    }

    [Fact]
    public void LaProvaDiWrappingUsataDalSoftwareDaLEsitoAtteso()
    {
        // This is the canonical way a program probes A20: write low, then
        // read back one megabyte higher and see whether it aliased.
        using var bus = new PhysicalMemoryBus();

        bus.A20Enabled = false;
        bus.WriteDword(0x000000, 0xDEADBEEF);
        bus.WriteDword(0x100000, 0x00000000);
        Assert.Equal(0u, bus.ReadDword(0x000000));      // same address after all

        bus.A20Enabled = true;
        bus.WriteDword(0x000000, 0xDEADBEEF);
        bus.WriteDword(0x100000, 0x00000000);
        Assert.Equal(0xDEADBEEFu, bus.ReadDword(0x000000));   // genuinely distinct addresses
    }

    [Fact]
    public void AccessoACavalloDelMegabyteConA20Spenta()
    {
        // Edge case: a 32-bit access starting just below the boundary. Every
        // byte of it has to be masked consistently.
        using var bus = new PhysicalMemoryBus();
        bus.A20Enabled = true;
        bus.WriteDword(0x0FFFFE, 0x11223344);
        bus.A20Enabled = false;

        // With A20 off the address is still below the megabyte, so nothing
        // about it changes.
        Assert.Equal(0x11223344u, bus.ReadDword(0x0FFFFE));
    }
}

public class A20HardwareTests
{
    [Fact]
    public void LaPortaNoveDueControllaA20()
    {
        using var machine = new Machine();
        machine.RunInstructions(300);

        // Bit 1 clear: A20 disabled.
        machine.Io.WriteByte(0x92, 0x00);
        Assert.False(machine.Memory.A20Enabled);

        machine.Io.WriteByte(0x92, 0x02);
        Assert.True(machine.Memory.A20Enabled);
    }

    [Fact]
    public void LaPortaNoveDueRiportaLoStatoEffettivo()
    {
        using var machine = new Machine();
        machine.RunInstructions(300);

        // Even when the line was changed elsewhere, reading the port must
        // tell the truth: there is one A20 line, not a private copy per
        // controller.
        machine.Memory.A20Enabled = false;
        Assert.Equal(0x00, machine.Io.ReadByte(0x92) & 0x02);

        machine.Memory.A20Enabled = true;
        Assert.Equal(0x02, machine.Io.ReadByte(0x92) & 0x02);
    }

    [Fact]
    public void IlControllerDiTastieraControllaA20()
    {
        using var machine = new Machine();
        machine.RunInstructions(300);

        // Command 0xD1 (write output port), followed by the data byte.
        machine.Io.WriteByte(0x64, 0xD1);
        machine.Io.WriteByte(0x60, 0x00);      // bit 1 clear
        Assert.False(machine.Memory.A20Enabled);

        machine.Io.WriteByte(0x64, 0xD1);
        machine.Io.WriteByte(0x60, 0x02);
        Assert.True(machine.Memory.A20Enabled);
    }

    [Fact]
    public void LeDueVieDiControlloVedonoLaStessaLinea()
    {
        // The keyboard controller and port 0x92 drive the same physical wire.
        // Keeping two separate states would let them drift apart.
        using var machine = new Machine();
        machine.RunInstructions(300);

        machine.Io.WriteByte(0x92, 0x00);                  // disabled via the fast port
        Assert.False(machine.Keyboard.A20Enabled);         // the controller sees it too

        machine.Io.WriteByte(0x64, 0xD1);
        machine.Io.WriteByte(0x60, 0x02);                  // enabled via the controller
        Assert.Equal(0x02, machine.Io.ReadByte(0x92) & 0x02);
    }

    [Fact]
    public void Int15hFunzione24hGestisceA20()
    {
        //   B8 01 24   MOV AX,0x2401  (enable A20)
        //   CD 15      INT 15h
        //   F4         HLT
        var sector = new byte[512];
        new byte[] { 0xB8, 0x01, 0x24, 0xCD, 0x15, 0xF4 }.CopyTo(sector, 0);
        sector[510] = 0x55; sector[511] = 0xAA;

        using var machine = new Machine();
        var disk = Aurora386.Core.Bios.DiskImage.CreateInMemory(0x00, 80, 2, 18);
        disk.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(disk);

        machine.Memory.A20Enabled = false;
        machine.RunInstructions(2000);

        Assert.True(machine.Memory.A20Enabled);
        Assert.Equal(0, machine.Cpu.State.GetReg8(4));   // AH = 0: success
    }
}

/// <summary>
/// The XMS driver: the service HIMEM.SYS provides on a real PC.
/// </summary>
public class XmsTests
{
    private static Machine CreateBootedMachine()
    {
        var machine = new Machine();
        machine.RunInstructions(300);
        return machine;
    }

    private static void CallXms(Machine machine, byte function, Action<Aurora386.Core.Cpu.Cpu>? setup = null)
    {
        machine.Cpu.State.SetReg8(4, function);
        setup?.Invoke(machine.Cpu);
        machine.Xms.Handle(machine.Cpu);
    }

    [Fact]
    public void LaVersioneRiportaLaPresenzaDellaHma()
    {
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x00);

        Assert.Equal(0x0300, machine.Cpu.State.Ax);   // XMS 3.0
        Assert.Equal(1, machine.Cpu.State.Dx);        // the HMA exists
    }

    [Fact]
    public void LaRichiestaDellaHmaRiesceEAccendeA20()
    {
        using Machine machine = CreateBootedMachine();
        machine.Memory.A20Enabled = false;

        // DX=0xFFFF is what DOS passes with DOS=HIGH: "give me the whole HMA".
        CallXms(machine, 0x01, c => c.State.Dx = 0xFFFF);

        Assert.Equal(1, machine.Cpu.State.Ax);
        Assert.True(machine.Xms.HmaAllocated);
        Assert.True(machine.Memory.A20Enabled);
    }

    [Fact]
    public void LaHmaNonPuoEssereAssegnataDueVolte()
    {
        using Machine machine = CreateBootedMachine();

        CallXms(machine, 0x01, c => c.State.Dx = 0xFFFF);
        CallXms(machine, 0x01, c => c.State.Dx = 0xFFFF);

        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0x91, machine.Cpu.State.GetReg8(3));   // already allocated
    }

    [Fact]
    public void SpegnereA20ConLaHmaInUsoVieneRifiutato()
    {
        // Turning it off would make the code currently executing up there
        // unreachable. This is a safeguard, not an arbitrary restriction.
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x01, c => c.State.Dx = 0xFFFF);

        CallXms(machine, 0x04);   // global A20 disable

        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0x94, machine.Cpu.State.GetReg8(3));
        Assert.True(machine.Memory.A20Enabled);
    }

    [Fact]
    public void UnaRichiestaTroppoGrandeVieneRifiutata()
    {
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x01, c => c.State.Dx = 0xFFF1);   // larger than the HMA

        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0xA1, machine.Cpu.State.GetReg8(3));
    }

    [Fact]
    public void LaMemoriaEstesaDisponibileRispecchiaLaRam()
    {
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x08);

        // 16 MB, less the first megabyte and less the HMA.
        Assert.True(machine.Cpu.State.Dx > 15000);
        Assert.True(machine.Cpu.State.Dx < 16000);
    }

    [Fact]
    public void AllocazioneELiberazioneDiUnBlocco()
    {
        using Machine machine = CreateBootedMachine();

        CallXms(machine, 0x08);
        ushort freeBefore = machine.Cpu.State.Dx;

        CallXms(machine, 0x09, c => c.State.Dx = 64);
        Assert.Equal(1, machine.Cpu.State.Ax);
        ushort handle = machine.Cpu.State.Dx;
        Assert.NotEqual(0, handle);

        CallXms(machine, 0x0E, c => c.State.Dx = handle);
        Assert.Equal(64, machine.Cpu.State.Dx);      // the size that was requested

        CallXms(machine, 0x0A, c => c.State.Dx = handle);
        Assert.Equal(1, machine.Cpu.State.Ax);
    }

    [Fact]
    public void UnHandleNonValidoVieneRifiutato()
    {
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x0A, c => c.State.Dx = 999);

        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0xA2, machine.Cpu.State.GetReg8(3));
    }

    [Fact]
    public void UnBloccoBloccatoNonPuoEssereLiberato()
    {
        using Machine machine = CreateBootedMachine();

        CallXms(machine, 0x09, c => c.State.Dx = 32);
        ushort handle = machine.Cpu.State.Dx;

        CallXms(machine, 0x0C, c => c.State.Dx = handle);   // lock the block
        Assert.Equal(1, machine.Cpu.State.Ax);

        CallXms(machine, 0x0A, c => c.State.Dx = handle);   // try to free it
        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0xAB, machine.Cpu.State.GetReg8(3));

        CallXms(machine, 0x0D, c => c.State.Dx = handle);   // unlock
        CallXms(machine, 0x0A, c => c.State.Dx = handle);
        Assert.Equal(1, machine.Cpu.State.Ax);
    }

    [Fact]
    public void CopiaDallaMemoriaConvenzionaleAQuellaEstesa()
    {
        using Machine machine = CreateBootedMachine();

        CallXms(machine, 0x09, c => c.State.Dx = 64);
        ushort handle = machine.Cpu.State.Dx;

        const uint source = 0x2000;
        const uint parameters = 0x3000;
        byte[] payload = "DATI-VERSO-LA-MEMORIA-ESTESA"u8.ToArray();
        payload.CopyTo(machine.Memory.GetRamSpan(source, payload.Length));

        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Ds, 0);
        machine.Memory.WriteDword(parameters, (uint)payload.Length);
        machine.Memory.WriteWord(parameters + 4, 0);            // source: conventional memory
        machine.Memory.WriteDword(parameters + 6, source);
        machine.Memory.WriteWord(parameters + 10, handle);      // destination: the XMS block
        machine.Memory.WriteDword(parameters + 12, 0);

        CallXms(machine, 0x0B, c => c.State.Si = (ushort)parameters);
        Assert.Equal(1, machine.Cpu.State.Ax);

        // Read it back from the block's physical address.
        CallXms(machine, 0x0C, c => c.State.Dx = handle);
        uint physical = ((uint)machine.Cpu.State.Dx << 16) | machine.Cpu.State.Bx;

        machine.Memory.A20Enabled = true;
        string readBack = System.Text.Encoding.ASCII.GetString(
            machine.Memory.GetRamSpan(physical, payload.Length).ToArray());
        Assert.Equal("DATI-VERSO-LA-MEMORIA-ESTESA", readBack);
    }

    [Fact]
    public void UnaLunghezzaDispariVieneRifiutata()
    {
        // The XMS specification requires even transfer lengths.
        using Machine machine = CreateBootedMachine();
        CallXms(machine, 0x09, c => c.State.Dx = 16);
        ushort handle = machine.Cpu.State.Dx;

        const uint parameters = 0x3000;
        machine.Cpu.SetSegment(Aurora386.Core.Cpu.SegReg.Ds, 0);
        machine.Memory.WriteDword(parameters, 7);               // odd length
        machine.Memory.WriteWord(parameters + 4, 0);
        machine.Memory.WriteDword(parameters + 6, 0x2000);
        machine.Memory.WriteWord(parameters + 10, handle);
        machine.Memory.WriteDword(parameters + 12, 0);

        CallXms(machine, 0x0B, c => c.State.Si = (ushort)parameters);
        Assert.Equal(0, machine.Cpu.State.Ax);
        Assert.Equal(0xA7, machine.Cpu.State.GetReg8(3));
    }
}

public class MultiplexTests
{
    [Fact]
    public void Int2FhAnnunciaIlGestoreXms()
    {
        //   B8 00 43   MOV AX,0x4300
        //   CD 2F      INT 2Fh
        //   A2 00 60   MOV [0x6000],AL
        //   F4         HLT
        var sector = new byte[512];
        new byte[] { 0xB8, 0x00, 0x43, 0xCD, 0x2F, 0xA2, 0x00, 0x60, 0xF4 }.CopyTo(sector, 0);
        sector[510] = 0x55; sector[511] = 0xAA;

        using var machine = new Machine();
        var disk = Aurora386.Core.Bios.DiskImage.CreateInMemory(0x00, 80, 2, 18);
        disk.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(disk);
        machine.RunInstructions(2000);

        // 0x80 in AL is how an XMS driver declares itself present.
        Assert.Equal(0x80, machine.Memory.ReadByte(0x6000));
    }

    [Fact]
    public void Int2FhFornisceIlPuntoDiIngresso()
    {
        //   B8 10 43   MOV AX,0x4310
        //   CD 2F      INT 2Fh
        //   89 1E 00 60  MOV [0x6000],BX
        //   8C 06 02 60  MOV [0x6002],ES
        //   F4         HLT
        var sector = new byte[512];
        new byte[] {
            0xB8, 0x10, 0x43, 0xCD, 0x2F,
            0x89, 0x1E, 0x00, 0x60,
            0x8C, 0x06, 0x02, 0x60,
            0xF4,
        }.CopyTo(sector, 0);
        sector[510] = 0x55; sector[511] = 0xAA;

        using var machine = new Machine();
        var disk = Aurora386.Core.Bios.DiskImage.CreateInMemory(0x00, 80, 2, 18);
        disk.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(disk);
        machine.RunInstructions(2000);

        Assert.Equal(0xF000, machine.Memory.ReadWord(0x6002));   // the BIOS segment
        Assert.NotEqual(0, machine.Memory.ReadWord(0x6000));     // a plausible offset
    }
}
