using Aurora386.Core.Cpu;
using Aurora386.Core.Cpu.Sse;

namespace NativeOracle;

/// <summary>One case's outcome: nothing wrong, or the list of fields that differed.</summary>
public sealed class CaseResult
{
    public required TestCase Case { get; init; }
    public required List<string> Differences { get; init; }
    public bool Passed => Differences.Count == 0;
}

/// <summary>
/// Runs one case on both engines and compares them: the seven general
/// registers (never Esp), EFLAGS held down to the case's defined mask, and
/// the whole shared buffer. A fault the emulator raised that the host did
/// not is always a difference -- every family only ever generates operands
/// its instruction cannot fault on, so an emulator fault here is either a
/// bug in that family's generator or a bug in the emulator, never a
/// legitimate divergence.
/// </summary>
public sealed class OracleComparer
{
    private readonly HostRunner _host;

    public OracleComparer(HostRunner host)
    {
        _host = host;
    }

    public CaseResult Run(TestCase testCase)
    {
        bool isMmx = testCase.InitialMmx is not null;
        bool isSse = testCase.InitialXmm is not null;

        _host.WriteBuffer(testCase.InitialBuffer);

        // One shared reserved image: PrepareFxsaveDefaults clears the whole
        // thing, so it must run exactly once, before either register file
        // is written into it -- a case that carries both (CVTPI2PS's MM
        // source alongside its XMM destination) needs both writes to land
        // on top of the same cleared image, not one wiping the other's.
        if (isMmx || isSse) _host.PrepareFxsaveDefaults();
        if (isMmx) _host.WriteMmxRegisters(testCase.InitialMmx!);
        if (isSse)
        {
            _host.WriteXmmRegisters(testCase.InitialXmm!);
            _host.WriteMxcsr(testCase.InitialMxcsr ?? SseUnit.MxcsrPowerOnDefault);
        }
        (uint[] hostGpr, uint hostEflags) = _host.Run(testCase.Instruction, testCase.InitialGpr, testCase.InitialEflags, useExtendedState: isMmx || isSse);
        byte[] hostBuffer = _host.ReadBuffer()[..testCase.InitialBuffer.Length].ToArray();

        var emulator = new EmulatorRunner(testCase.Model);
        (uint[] emulatorGpr, uint emulatorEflags, byte[] emulatorBuffer, byte? fault, ulong[] emulatorMmx, ushort emulatorFsw, ushort emulatorTagWord, XmmValue[] emulatorXmm, uint emulatorMxcsr) = emulator.Run(
            testCase.Instruction, testCase.InitialGpr, testCase.InitialEflags,
            _host.BufferAddress, testCase.InitialBuffer, testCase.InitialMmx, testCase.InitialXmm, testCase.InitialMxcsr);

        var differences = new List<string>();

        if (fault is byte vector)
        {
            differences.Add($"emulator faulted (vector {vector:X2}h), host did not");
        }

        foreach (Reg register in InstructionEncoding.AddressableRegisters)
        {
            if (testCase.IgnoredRegisters.Contains(register)) continue;

            int index = (int)register;
            if (hostGpr[index] != emulatorGpr[index])
            {
                differences.Add($"{register} host={hostGpr[index]:X8} emulator={emulatorGpr[index]:X8}");
            }
        }

        uint flagsDifference = (hostEflags ^ emulatorEflags) & testCase.DefinedFlagsMask;
        if (flagsDifference != 0)
        {
            differences.Add($"eflags (masked {testCase.DefinedFlagsMask:X4}h) host={hostEflags & testCase.DefinedFlagsMask:X4} emulator={emulatorEflags & testCase.DefinedFlagsMask:X4}");
        }

        for (int i = 0; i < hostBuffer.Length; i++)
        {
            if (hostBuffer[i] != emulatorBuffer[i])
            {
                differences.Add($"buffer[{i}] host={hostBuffer[i]:X2} emulator={emulatorBuffer[i]:X2}");
            }
        }

        if (isMmx)
        {
            ulong[] hostMmx = _host.ReadMmxRegisters();
            for (int i = 0; i < 8; i++)
            {
                if (hostMmx[i] != emulatorMmx[i])
                {
                    differences.Add($"MM{i} host={hostMmx[i]:X16} emulator={emulatorMmx[i]:X16}");
                }
            }

            int hostTop = (_host.ReadFsw() >> 11) & 7;
            int emulatorTop = (emulatorFsw >> 11) & 7;
            if (hostTop != emulatorTop)
            {
                differences.Add($"FPU TOP host={hostTop} emulator={emulatorTop}");
            }

            byte hostFtw = _host.ReadFtw();
            byte emulatorFtw = AbridgedTagWord(emulatorTagWord);
            if (hostFtw != emulatorFtw)
            {
                differences.Add($"FPU tag word (abridged) host={hostFtw:X2} emulator={emulatorFtw:X2}");
            }
        }

        if (isSse)
        {
            XmmValue[] hostXmm = _host.ReadXmmRegisters();
            for (int i = 0; i < 8; i++)
            {
                if (!hostXmm[i].Equals(emulatorXmm[i]))
                {
                    differences.Add($"XMM{i} host={hostXmm[i].D3:X8}{hostXmm[i].D2:X8}{hostXmm[i].D1:X8}{hostXmm[i].D0:X8} emulator={emulatorXmm[i].D3:X8}{emulatorXmm[i].D2:X8}{emulatorXmm[i].D1:X8}{emulatorXmm[i].D0:X8}");
                }
            }

            // The whole register is compared, flags and masks and RC and FZ
            // alike: a family that touches MXCSR only through its own sticky
            // flags still has to agree with the host on every other bit it
            // did not change.
            uint hostMxcsr = _host.ReadMxcsr();
            if (hostMxcsr != emulatorMxcsr)
            {
                differences.Add($"MXCSR host={hostMxcsr:X8} emulator={emulatorMxcsr:X8}");
            }
        }

        return new CaseResult { Case = testCase, Differences = differences };
    }

    /// <summary>
    /// FXSAVE's tag word is one bit per register (1 = not empty); FSTENV/
    /// FSAVE's -- what Aurora's own <c>TagWord</c> property reports -- is two
    /// (3 = empty, everything else = a value present). The two formats agree
    /// on empty-or-not, which is all an MMX case ever needs: TOP/tag effects
    /// are about "did EMMS empty the registers", not which of the three
    /// non-empty codes a value happens to carry.
    /// </summary>
    private static byte AbridgedTagWord(ushort fullTagWord)
    {
        byte abridged = 0;
        for (int i = 0; i < 8; i++)
        {
            if (((fullTagWord >> (i * 2)) & 3) != 3) abridged |= (byte)(1 << i);
        }
        return abridged;
    }
}
