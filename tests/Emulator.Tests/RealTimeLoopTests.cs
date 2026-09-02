using Emulator.Core;
using Emulator.Core.Bios;

namespace Emulator.Tests;

/// <summary>
/// Il ciclo di esecuzione a tempo reale, quello che il frontend usa a ogni
/// fotogramma. Verifica che il tempo del sistema emulato scorra alla
/// velocita' giusta e che il rendering non lavori inutilmente.
/// </summary>
public class RealTimeLoopTests
{
    private static byte[] IdleBootSector()
    {
        // Un settore che abilita le interruzioni e attende: cosi' la macchina
        // resta viva mentre il timer scorre.
        var sector = new byte[512];
        new byte[] { 0xFB, 0xF4, 0xEB, 0xFD }.CopyTo(sector, 0);   // STI; HLT; JMP -3
        sector[510] = 0x55; sector[511] = 0xAA;
        return sector;
    }

    [Fact]
    public void IlTimerScorreAllaFrequenzaDelPc()
    {
        using var machine = new Machine();
        DiskImage disk = DiskImage.CreateInMemory(0x00, 80, 2, 18);
        disk.WriteSectors(0, 0, 1, 1, IdleBootSector());
        machine.AttachDisk(disk);

        machine.RunInstructions(2000);      // avvio
        uint before = machine.Bda.TimerTicks;

        // Due secondi simulati, un fotogramma alla volta come fa il frontend.
        for (int i = 0; i < 120; i++)
            machine.RunFor(TimeSpan.FromSeconds(1.0 / 60.0));

        uint ticks = machine.Bda.TimerTicks - before;

        // A 18,2 Hz due secondi danno circa 36 tick. Il margine tiene conto
        // dell'arrotondamento fra la frequenza della CPU e quella del timer.
        Assert.InRange(ticks, 30u, 42u);
    }

    [Fact]
    public void IlRenderNonRidisegnaSenzaCambiamenti()
    {
        // Ridisegnare 80x25 celle a ogni fotogramma quando nulla e' cambiato
        // sarebbe lavoro quasi tutto sprecato: il tracciamento delle righe
        // modificate esiste proprio per evitarlo.
        using var machine = new Machine();
        machine.RunInstructions(500);

        machine.Video.Render(force: true);
        long baseline = machine.Video.FramesRendered;

        for (int i = 0; i < 60; i++)
        {
            machine.RunFor(TimeSpan.FromSeconds(1.0 / 60.0));
            machine.Video.Render();
        }

        // Senza scritture in memoria video il conteggio non deve crescere.
        Assert.Equal(baseline, machine.Video.FramesRendered);
    }

    [Fact]
    public void IlRenderRidisegnaQuandoLoSchermoCambia()
    {
        using var machine = new Machine();
        machine.RunInstructions(500);
        machine.Video.Render(force: true);
        long baseline = machine.Video.FramesRendered;

        machine.VideoServices.TeletypeOutput((byte)'X', 0);

        Assert.True(machine.Video.Render());
        Assert.Equal(baseline + 1, machine.Video.FramesRendered);
    }

    [Fact]
    public void IlFramebufferHaLaDimensioneDelModoTesto()
    {
        using var machine = new Machine();
        machine.RunInstructions(500);
        machine.Video.Render(force: true);

        Assert.Equal(640, machine.Video.Width);
        Assert.Equal(400, machine.Video.Height);
        Assert.Equal(640 * 400, machine.Video.Framebuffer.Length);
    }

    [Fact]
    public void LEsecuzioneAvanzaDiUnNumeroCoerenteDiIstruzioni()
    {
        using var machine = new Machine();
        DiskImage disk = DiskImage.CreateInMemory(0x00, 80, 2, 18);

        // Un ciclo che gira senza fermarsi, per contare le istruzioni.
        var sector = new byte[512];
        new byte[] { 0x90, 0xEB, 0xFD }.CopyTo(sector, 0);   // NOP; JMP -3
        sector[510] = 0x55; sector[511] = 0xAA;
        disk.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(disk);

        machine.RunInstructions(2000);
        long before = machine.Cpu.Cycles;

        machine.RunFor(TimeSpan.FromSeconds(0.1));

        long executed = machine.Cpu.Cycles - before;
        long expected = (long)(machine.CpuFrequencyHz * 0.1);

        // Il conteggio deve corrispondere alla frequenza dichiarata, entro
        // la granularita' dei blocchi di esecuzione.
        Assert.InRange(executed, expected - 5000, expected + 5000);
    }

    [Fact]
    public void IlCursoreLampeggiaSenzaToccareLaMemoriaVideo()
    {
        using var machine = new Machine();
        machine.RunInstructions(500);
        machine.Video.Render(force: true);

        // Il lampeggio e' uno stato del renderer: cambiarlo deve produrre un
        // ridisegno della riga del cursore senza che la memoria video cambi.
        byte cellBefore = machine.VideoMemory[
            Emulator.Core.Devices.Video.VgaMemory.TextWindowOffset];

        machine.Video.CursorBlinkOn = false;
        machine.Video.Render(force: true);
        machine.Video.CursorBlinkOn = true;
        machine.Video.Render(force: true);

        Assert.Equal(cellBefore, machine.VideoMemory[
            Emulator.Core.Devices.Video.VgaMemory.TextWindowOffset]);
    }
}
