using Aurora86.Core.Devices.Parallel;
using CpuNs = Aurora86.Core.Cpu;

namespace Aurora86.Core.Firmware;

/// <summary>
/// INT 17h, the BIOS printer service LPT1 answers through
/// (docs/PARALLEL-PORT.md section 2).
///
/// DOS's COPY, PRINT and TYPE FILE &gt; PRN, and a program's own "print
/// report" menu entry, reach the printer through this call rather than the
/// parallel port directly. There are three functions, chosen by AH: print
/// the character in AL (00h), initialize the printer by pulsing /INIT
/// (01h), and read status without sending anything (02h). Every one of them
/// answers in AH with the same transformation of the STATUS register: bits
/// 2-0 cleared, and bits 6 (/ACK) and 3 (/ERROR) inverted -- so that a ready
/// printer, whose STATUS reads DFh, answers 90h: not busy, selected. Only
/// printer 0 is fitted; DX naming any other one is a bare time-out, AH =
/// 01h, without the port being touched at all (*choice*: the IBM BIOS
/// instead leaves AH untouched when the BIOS data area holds no port for
/// it, which this class does not reproduce -- see docs/PARALLEL-PORT.md
/// section 2).
///
/// The CONTROL writes below follow the IBM PC AT BIOS listing literally:
/// fixed values, not a read of whatever CONTROL already held. AH=00h writes
/// 0Dh (STROBE, /INIT and SELECT IN all set) and then 0Ch (STROBE cleared
/// again); AH=01h writes 08h (/INIT cleared, asserting reset) and then 0Ch.
/// Neither value ever sets the IRQ enable bit, on purpose: the BIOS prints
/// by polling STATUS, never by interrupt, so even a driver that left LPT1's
/// IRQ enable bit set before calling INT 17h finds it cleared by the call,
/// and no IRQ 7 reaches the PIC on the BIOS's own account.
/// </summary>
public sealed class PrinterServices
{
    // Functions of INT 17h, passed in AH.
    private const byte FunctionPrintCharacter = 0x00;
    private const byte FunctionInitialize = 0x01;
    private const byte FunctionReadStatus = 0x02;

    // The exact CONTROL values the IBM BIOS listing writes for each
    // function, composed from the named bits ParallelPort declares rather
    // than left as bare hex. Bit 4 (IRQ enable) is never one of them.
    private const byte ControlPrintStrobeAsserted =
        ParallelPort.ControlStrobe | ParallelPort.ControlInitialize | ParallelPort.ControlSelectIn;      // 0Dh
    private const byte ControlIdle =
        ParallelPort.ControlInitialize | ParallelPort.ControlSelectIn;                                    // 0Ch
    private const byte ControlInitializeAsserted =
        ParallelPort.ControlSelectIn;                                                                     // 08h

    /// <summary>Bits the BIOS clears on the way from STATUS into AH: the three that STATUS always reads as 1.</summary>
    private const byte StatusKeptBitsMask = 0xF8;

    /// <summary>Bits the BIOS inverts on the way from STATUS into AH: /ACK (bit 6) and /ERROR (bit 3).</summary>
    private const byte StatusInvertedBits = 0x48;

    /// <summary>What AH answers when DX names a printer other than 0.</summary>
    private const byte TimeOut = 0x01;

    private readonly ParallelPort _port;

    public PrinterServices(ParallelPort port) => _port = port;

    /// <summary>Dispatches one INT 17h call, reading AH and DX and AL, and leaving the result in AH.</summary>
    public void Handle(CpuNs.Cpu cpu)
    {
        if (cpu.State.Dx != 0)
        {
            cpu.State.SetReg8(4, TimeOut);   // AH
            return;
        }

        byte function = cpu.State.GetReg8(4);   // AH
        switch (function)
        {
            case FunctionPrintCharacter:
                SendCharacter(cpu.State.GetReg8(0));   // AL
                break;

            case FunctionInitialize:
                PulseInitialize();
                break;

            case FunctionReadStatus:
                break;
        }

        cpu.State.SetReg8(4, ReadStatus());   // AH
    }

    /// <summary>
    /// Drives the handshake docs/PARALLEL-PORT.md section 1 describes:
    /// write the byte to DATA, then pulse STROBE high and low again by
    /// writing the BIOS's own two fixed CONTROL values. The port takes the
    /// byte, and would raise IRQ 7 if asked to, on the falling edge -- but
    /// the second value never has the IRQ enable bit set, so nothing a
    /// driver did to CONTROL before this call survives it.
    /// </summary>
    private void SendCharacter(byte value)
    {
        _port.WriteByte(ParallelPort.DataPort, value);
        _port.WriteByte(ParallelPort.ControlPort, ControlPrintStrobeAsserted);
        _port.WriteByte(ParallelPort.ControlPort, ControlIdle);
    }

    /// <summary>Pulses /INIT low and back high by writing the BIOS's own two fixed CONTROL values.</summary>
    private void PulseInitialize()
    {
        _port.WriteByte(ParallelPort.ControlPort, ControlInitializeAsserted);
        _port.WriteByte(ParallelPort.ControlPort, ControlIdle);
    }

    private byte ReadStatus()
    {
        byte status = _port.ReadByte(ParallelPort.StatusPort);
        return (byte)((status & StatusKeptBitsMask) ^ StatusInvertedBits);
    }
}
