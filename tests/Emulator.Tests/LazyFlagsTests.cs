using Emulator.Core.Cpu;

namespace Emulator.Tests;

/// <summary>
/// I flag sono il componente da cui dipende ogni salto condizionato: un errore
/// qui non da' un crash, da' un programma che prende il ramo sbagliato molto
/// piu' tardi. Per questo ADD/SUB/ADC/SBB sono verificati in modo esaustivo su
/// tutte le 65536 combinazioni a 8 bit, contro flag calcolati in modo
/// indipendente dall'implementazione sotto test.
/// </summary>
public class LazyFlagsTests
{
    private static bool ExpectedParity(uint result)
    {
        int bits = 0;
        for (int i = 0; i < 8; i++) bits += (int)((result >> i) & 1);
        return (bits & 1) == 0;      // PF e' 1 quando i bit a 1 sono pari
    }

    [Fact]
    public void AddEsaustivoSu8Bit()
    {
        var f = new LazyFlags();
        for (uint a = 0; a <= 0xFF; a++)
        for (uint b = 0; b <= 0xFF; b++)
        {
            uint result = (a + b) & 0xFF;
            f.Set(FlagOp.Add, a, b, result, 8);

            Assert.Equal(a + b > 0xFF, f.Carry);
            Assert.Equal(result == 0, f.Zero);
            Assert.Equal((result & 0x80) != 0, f.Sign);
            Assert.Equal(ExpectedParity(result), f.Parity);
            Assert.Equal(((a & 0xF) + (b & 0xF)) > 0xF, f.Adjust);

            // Overflow con segno: i due addendi hanno lo stesso segno e il
            // risultato ha segno opposto.
            bool overflow = ((sbyte)a > 0 && (sbyte)b > 0 && (sbyte)result < 0)
                         || ((sbyte)a < 0 && (sbyte)b < 0 && (sbyte)result >= 0);
            Assert.Equal(overflow, f.Overflow);
        }
    }

    [Fact]
    public void SubEsaustivoSu8Bit()
    {
        var f = new LazyFlags();
        for (uint a = 0; a <= 0xFF; a++)
        for (uint b = 0; b <= 0xFF; b++)
        {
            uint result = (a - b) & 0xFF;
            f.Set(FlagOp.Sub, a, b, result, 8);

            Assert.Equal(a < b, f.Carry);            // prestito
            Assert.Equal(result == 0, f.Zero);
            Assert.Equal((result & 0x80) != 0, f.Sign);
            Assert.Equal(ExpectedParity(result), f.Parity);
            Assert.Equal((a & 0xF) < (b & 0xF), f.Adjust);

            bool overflow = ((sbyte)a >= 0 && (sbyte)b < 0 && (sbyte)result < 0)
                         || ((sbyte)a < 0 && (sbyte)b > 0 && (sbyte)result >= 0);
            Assert.Equal(overflow, f.Overflow);
        }
    }

    [Fact]
    public void AdcEsaustivoSu8BitConEntrambiICarryEntranti()
    {
        var f = new LazyFlags();
        for (uint carryIn = 0; carryIn <= 1; carryIn++)
        for (uint a = 0; a <= 0xFF; a++)
        for (uint b = 0; b <= 0xFF; b++)
        {
            uint full = a + b + carryIn;
            uint result = full & 0xFF;
            f.Set(FlagOp.Adc, a, b, result, 8, carryIn != 0);

            Assert.Equal(full > 0xFF, f.Carry);
            Assert.Equal(result == 0, f.Zero);
            Assert.Equal(((a & 0xF) + (b & 0xF) + carryIn) > 0xF, f.Adjust);
        }
    }

    [Fact]
    public void SbbEsaustivoSu8BitConEntrambiIPrestitiEntranti()
    {
        var f = new LazyFlags();
        for (uint borrowIn = 0; borrowIn <= 1; borrowIn++)
        for (uint a = 0; a <= 0xFF; a++)
        for (uint b = 0; b <= 0xFF; b++)
        {
            uint result = (a - b - borrowIn) & 0xFF;
            f.Set(FlagOp.Sbb, a, b, result, 8, borrowIn != 0);

            Assert.Equal(a < b + borrowIn, f.Carry);
            Assert.Equal(result == 0, f.Zero);
        }
    }

    [Fact]
    public void IncEDecPreservanoIlCarry()
    {
        // E' cio' che permette di usarli dentro cicli di somma multiprecisione
        // senza distruggere il riporto in corso.
        var f = new LazyFlags();
        f.Carry = true;
        f.Set(FlagOp.Inc, 5, 1, 6, 8);
        Assert.True(f.Carry);
        Assert.False(f.Zero);

        f.Carry = false;
        f.Set(FlagOp.Dec, 1, 1, 0, 8);
        Assert.False(f.Carry);
        Assert.True(f.Zero);
    }

    [Fact]
    public void IncSegnalaOverflowSoloAlLimitePositivo()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Inc, 0x7F, 1, 0x80, 8);
        Assert.True(f.Overflow);        // 127 -> -128
        Assert.True(f.Sign);

        f.Set(FlagOp.Inc, 0x10, 1, 0x11, 8);
        Assert.False(f.Overflow);
    }

    [Fact]
    public void DecSegnalaOverflowSoloAlLimiteNegativo()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Dec, 0x80, 1, 0x7F, 8);
        Assert.True(f.Overflow);        // -128 -> 127
        Assert.False(f.Sign);
    }

    [Fact]
    public void LogicAzzeraCarryEOverflow()
    {
        var f = new LazyFlags();
        f.Carry = true;
        f.Overflow = true;
        f.Set(FlagOp.Logic, 0xF0, 0x0F, 0x00, 8);

        Assert.False(f.Carry);
        Assert.False(f.Overflow);
        Assert.True(f.Zero);
        Assert.True(f.Parity);
    }

    [Fact]
    public void ParitaGuardaSoloGliOttoBitBassi()
    {
        // Vale anche per operandi a 16 e 32 bit: PF e' definito sul byte basso.
        var f = new LazyFlags();
        f.Set(FlagOp.Logic, 0, 0, 0xFF00, 16);
        Assert.True(f.Parity);          // byte basso 0x00: zero bit a 1, pari

        f.Set(FlagOp.Logic, 0, 0, 0xFF01, 16);
        Assert.False(f.Parity);         // byte basso 0x01: un bit a 1, dispari
    }

    [Fact]
    public void FlagDiControlloSonoIndipendentiDaQuelliAritmetici()
    {
        var f = new LazyFlags();
        f.Interrupt = true;
        f.Direction = true;

        // Un'operazione aritmetica non deve toccare IF/DF.
        f.Set(FlagOp.Add, 0xFF, 0xFF, 0xFE, 8);
        Assert.True(f.Carry);
        Assert.True(f.Interrupt);
        Assert.True(f.Direction);
    }

    [Fact]
    public void SetValueScartaLOperazionePendente()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Add, 0xFF, 0xFF, 0xFE, 8);   // produrrebbe carry
        f.SetValue((uint)Flags.Zero);              // POPF

        Assert.False(f.Carry);
        Assert.True(f.Zero);
    }

    [Fact]
    public void OperazioniA32BitCalcolanoCarryEOverflow()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Add, 0xFFFFFFFF, 1, 0, 32);
        Assert.True(f.Carry);
        Assert.True(f.Zero);
        Assert.False(f.Overflow);       // -1 + 1 = 0, nessun overflow con segno

        f.Set(FlagOp.Add, 0x7FFFFFFF, 1, 0x80000000, 32);
        Assert.False(f.Carry);
        Assert.True(f.Overflow);        // massimo positivo + 1
        Assert.True(f.Sign);
    }

    [Fact]
    public void ShlCalcolaCarryDallUltimoBitUscito()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Shl, 0x80, 1, 0x00, 8);
        Assert.True(f.Carry);           // il bit 7 e' uscito
        Assert.True(f.Zero);

        f.Set(FlagOp.Shl, 0x40, 1, 0x80, 8);
        Assert.False(f.Carry);
        Assert.True(f.Overflow);        // il segno e' cambiato
    }

    [Fact]
    public void ShrEdSarDifferisconoSulSegno()
    {
        var f = new LazyFlags();
        f.Set(FlagOp.Shr, 0x81, 1, 0x40, 8);
        Assert.True(f.Carry);           // bit 0 uscito
        Assert.True(f.Overflow);        // SHR di 1: OF = bit di segno originale

        f.Set(FlagOp.Sar, 0x81, 1, 0xC0, 8);
        Assert.True(f.Carry);
        Assert.False(f.Overflow);       // SAR non produce mai overflow
    }
}
