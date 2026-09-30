using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Cpu.Fpu;

namespace Aurora86.Tests;

/// <summary>
/// The 387 control and status words carry deferred errors across saved
/// machine state. Both snapshot layouts must retain that relationship so
/// the resumed program observes the same exception at its next FWAIT.
/// </summary>
public class X87SnapshotExceptionTests
{
    [Theory]
    [InlineData(1, 0x037E, 0x0001, 0x9881)]
    [InlineData(2, 0x037E, 0x0001, 0x9881)]
    [InlineData(1, 0x037F, 0x8081, 0x1801)]
    [InlineData(2, 0x037F, 0x8081, 0x1801)]
    public void BothSnapshotLayoutsReconcileSummaryBitsAndPreserveTop(
        int version, ushort control, ushort status, ushort expectedStatus)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(control);
            writer.Write(status);
            writer.Write((byte)3); // TOP is a separate byte in both snapshot versions.
            for (int register = 0; register < 8; register++)
            {
                writer.Write(register == 3);
                writer.Write(0x8000000000000000UL); // Raw significand of +1.
                if (version == 1)
                {
                    writer.Write(0); // Unbiased exponent of +1.
                    writer.Write(false); // Sign, NaN and infinity flags.
                    writer.Write(false);
                    writer.Write(false);
                }
                else
                {
                    writer.Write((ushort)0x3FFF); // Biased exponent of +1.
                }
            }
        }

        stream.Position = 0;
        var unit = new X87Unit();
        using var reader = new BinaryReader(stream);
        unit.ReadSnapshot(reader, version);

        Assert.Equal(control, unit.ControlWord);
        Assert.Equal(expectedStatus, unit.StatusWord);
        Assert.Equal((expectedStatus & 0x0080) != 0, unit.HasPendingException);
        Assert.Equal(0xFF3F, unit.TagWord); // Only physical register 3 is nonempty.
        Assert.Equal(0x8000000000000000UL, unit.GetRaw(0).Significand);
        Assert.Equal(0x3FFF, unit.GetRaw(0).SignExponent);

        using var roundtrip = new MemoryStream();
        using (var writer = new BinaryWriter(roundtrip, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            unit.WriteSnapshot(writer);
        }

        Assert.Equal(X87Unit.SnapshotLength, roundtrip.Length);
        roundtrip.Position = 0;
        using var roundtripReader = new BinaryReader(roundtrip);
        var restored = new X87Unit();
        restored.ReadSnapshot(roundtripReader, X87Unit.SnapshotVersion);
        Assert.Equal(unit.StatusWord, restored.StatusWord);
        Assert.Equal(unit.ControlWord, restored.ControlWord);
        Assert.Equal(unit.TagWord, restored.TagWord);
    }

    [Theory]
    [InlineData(0x037E, 16)]
    [InlineData(0x037F, 255)]
    public void MachineFileRestoresWhetherFwaitMustDeliverTheSavedException(
        ushort control, byte expectedVector)
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.Cpu.State.SelectModel(CpuModel.Core2);
        machine.Cpu.State.Halted = false;
        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.State.Cr0 = CpuState.Cr0ExtensionType | CpuState.Cr0NumericError;
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = 0x2000;
        machine.Memory.WriteByte(0x2000, 0x9B); // FWAIT.
        machine.Cpu.Fpu.RestoreState(control, 0x0001, 0xFFFF, new Raw80[8]);

        string path = Path.GetTempFileName();
        try
        {
            MachineSnapshot.Capture(machine).Save(path);
            machine.Cpu.Fpu.Reset();
            MachineSnapshot.Load(path).RestoreTo(machine);

            byte vector = 0xFF;
            machine.Cpu.ProcessorException += (number, _, _, _) => vector = number;
            machine.Cpu.Step();

            Assert.Equal(expectedVector, vector);
            Assert.Equal(control, machine.Cpu.Fpu.ControlWord);
            Assert.Equal(expectedVector == 16, machine.Cpu.Fpu.HasPendingException);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
