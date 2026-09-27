using Aurora386.Core;
using Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// The snapshot file format: sections with a name, a version and a length,
/// the state the sectioned format added, and the files written before it.
///
/// A snapshot is how every long investigation starts, so a file from last
/// month has to keep loading after the format changes, and a field the format
/// forgets shows up only as a guest that diverges some time after a restore.
/// </summary>
public class MachineSnapshotFormatTests
{
    /// <summary>A machine parked in real mode at 0000:2000 with a stack.</summary>
    private static Machine Build()
    {
        var machine = new Machine();
        machine.RunInstructions(200);
        machine.Cpu.State.Halted = false;

        machine.Cpu.SetSegment(SegReg.Cs, 0);
        machine.Cpu.SetSegment(SegReg.Ds, 0);
        machine.Cpu.SetSegment(SegReg.Ss, 0);
        machine.Cpu.State.Esp = 0x7000;
        machine.Cpu.State.Eip = 0x2000;

        return machine;
    }

    private static void RunProgram(Machine machine, params byte[] program)
    {
        for (int i = 0; i < program.Length; i++)
        {
            machine.Memory.WriteByte(0x2000 + (uint)i, program[i]);
        }

        machine.Cpu.State.Eip = 0x2000;

        while (machine.Cpu.State.Eip < 0x2000 + program.Length)
        {
            machine.Cpu.Step();
        }
    }

    private static MachineSnapshot ThroughAFile(MachineSnapshot snapshot)
    {
        string path = Path.GetTempFileName();
        try
        {
            snapshot.Save(path);
            return MachineSnapshot.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The coprocessor's registers, its top-of-stack pointer and its control
    /// word come back from a file. Before the sectioned format none of them
    /// were saved, so a program restored in the middle of a floating-point
    /// loop carried on with whatever the unit happened to hold.
    /// </summary>
    [Fact]
    public void TheCoprocessorStackSurvivesAFile()
    {
        using Machine machine = Build();

        // FLD1, FLDPI, then FLDCW from 0x3000 with rounding set to truncate.
        machine.Memory.WriteWord(0x3000, 0x0F7F);
        RunProgram(machine, 0xD9, 0xE8, 0xD9, 0xEB, 0xD9, 0x2E, 0x00, 0x30);

        MachineSnapshot snapshot = ThroughAFile(MachineSnapshot.Capture(machine));

        machine.Cpu.Fpu.Reset();
        snapshot.RestoreTo(machine);

        Assert.Equal(Math.PI, machine.Cpu.Fpu.Get(0).ToDouble());
        Assert.Equal(1.0, machine.Cpu.Fpu.Get(1).ToDouble());
        Assert.False(machine.Cpu.Fpu.IsUsed(2));
        Assert.Equal(6, (machine.Cpu.Fpu.StatusWord >> 11) & 7);
        Assert.Equal(0x0F7F, machine.Cpu.Fpu.ControlWord);
    }

    /// <summary>
    /// DR0-DR7 and TR6-TR7 come back from a file. A debugger's breakpoint
    /// armed in DR0 and DR7 would otherwise vanish on restore.
    /// </summary>
    [Fact]
    public void TheDebugAndTestRegistersSurviveAFile()
    {
        using Machine machine = Build();

        machine.Cpu.State.Dr[0] = 0x00012345;
        machine.Cpu.State.Dr[7] = 0x00000401;
        machine.Cpu.State.Tr[1] = 0xABCD0000;

        MachineSnapshot snapshot = ThroughAFile(MachineSnapshot.Capture(machine));

        Array.Clear(machine.Cpu.State.Dr);
        Array.Clear(machine.Cpu.State.Tr);
        snapshot.RestoreTo(machine);

        Assert.Equal(0x00012345u, machine.Cpu.State.Dr[0]);
        Assert.Equal(0x00000401u, machine.Cpu.State.Dr[7]);
        Assert.Equal(0xABCD0000u, machine.Cpu.State.Tr[1]);
    }

    /// <summary>
    /// The Pentium's system state comes back from a file: CR4, the
    /// performance-monitoring MSRs, and a time-stamp counter that carries on
    /// from the value the guest last read rather than from the cycle count of
    /// the machine it is restored into.
    /// </summary>
    [Fact]
    public void ThePentiumSystemStateSurvivesAFile()
    {
        using Machine machine = Build();
        machine.Cpu.State.SelectModel(CpuModel.Pentium);

        machine.Cpu.State.Cr4 = CpuState.Cr4Tsd | CpuState.Cr4De;
        machine.Cpu.Msrs.Cesr = 0x00C600C6;
        machine.Cpu.Msrs.Counter1 = 0x12_3456_789A;
        machine.Cpu.WriteTsc(0x40000000);
        ulong timeStampCounter = machine.Cpu.Tsc;

        MachineSnapshot snapshot = ThroughAFile(MachineSnapshot.Capture(machine));

        machine.Cpu.State.Cr4 = 0;
        machine.Cpu.Msrs.Cesr = 0;
        machine.Cpu.Msrs.Counter1 = 0;
        machine.RunInstructions(10_000);
        snapshot.RestoreTo(machine);

        Assert.Equal(CpuState.Cr4Tsd | CpuState.Cr4De, machine.Cpu.State.Cr4);
        Assert.Equal(0x00C600C6ul, machine.Cpu.Msrs.Cesr);
        Assert.Equal(0x12_3456_789Aul, machine.Cpu.Msrs.Counter1);
        Assert.Equal(timeStampCounter, machine.Cpu.Tsc);
    }

    /// <summary>
    /// A Pentium II's family-six MSRs and the PAE page-directory-pointer
    /// registers come back from a file: SYSENTER_EIP (176h), PAT (277h), an
    /// MTRR base (200h) and the four PDPTEs the processor loaded.
    /// </summary>
    [Fact]
    public void ThePentiumIiSystemStateSurvivesAFile()
    {
        const uint sysenterEip = 0x176;
        const uint pageAttributeTable = 0x277;
        const uint firstVariableBase = 0x200;
        const ulong patValue = 0x0006040600070406;
        const ulong mtrrBase = 0x00000000_000C0006;

        using Machine machine = Build();
        machine.Cpu.State.SelectModel(CpuModel.PentiumII);

        Assert.True(machine.Cpu.Msrs.TryWrite(sysenterEip, 0x80001234));
        Assert.True(machine.Cpu.Msrs.TryWrite(pageAttributeTable, patValue));
        Assert.True(machine.Cpu.Msrs.TryWrite(firstVariableBase, mtrrBase));
        machine.Cpu.Pdpte[2] = 0x0000_0000_0012_3001;

        MachineSnapshot snapshot = ThroughAFile(MachineSnapshot.Capture(machine));

        machine.Cpu.Msrs.Reset();
        Array.Clear(machine.Cpu.Pdpte);
        snapshot.RestoreTo(machine);

        Assert.True(machine.Cpu.Msrs.TryRead(sysenterEip, out ulong eip));
        Assert.True(machine.Cpu.Msrs.TryRead(pageAttributeTable, out ulong pat));
        Assert.True(machine.Cpu.Msrs.TryRead(firstVariableBase, out ulong mtrr));
        Assert.Equal(0x80001234ul, eip);
        Assert.Equal(patValue, pat);
        Assert.Equal(mtrrBase, mtrr);
        Assert.Equal(0x0000_0000_0012_3001ul, machine.Cpu.Pdpte[2]);
    }

    /// <summary>
    /// XMM7 and MXCSR come back from a file ("SSE " section); a RESET of the
    /// processor returns them to zero and 1F80h.
    /// </summary>
    [Fact]
    public void TheSseRegistersSurviveAFileAndResetClearsThem()
    {
        using Machine machine = Build();
        machine.Cpu.State.SelectModel(CpuModel.PentiumIII);

        machine.Cpu.Sse.Xmm(7) = Aurora386.Core.Cpu.Sse.XmmValue.FromHalves(0x0123456789ABCDEF, 0xFEDCBA9876543210);
        machine.Cpu.Sse.Mxcsr = 0x1FA0;

        MachineSnapshot snapshot = ThroughAFile(MachineSnapshot.Capture(machine));

        machine.Cpu.Sse.Reset();
        snapshot.RestoreTo(machine);

        Assert.Equal(0x0123456789ABCDEFul, machine.Cpu.Sse.Xmm(7).Low);
        Assert.Equal(0xFEDCBA9876543210ul, machine.Cpu.Sse.Xmm(7).High);
        Assert.Equal(0x1FA0u, machine.Cpu.Sse.Mxcsr);

        machine.Cpu.Reset();

        Assert.Equal(0ul, machine.Cpu.Sse.Xmm(7).Low);
        Assert.Equal(0x1F80u, machine.Cpu.Sse.Mxcsr);
    }

    /// <summary>
    /// An MTRR does not accept UC- (type 7), which only a PAT entry may hold:
    /// WRMSR 200h with type 7 is refused (#GP), while PAT accepts it.
    /// </summary>
    [Fact]
    public void AnMtrrRefusesTheUncachedMinusType()
    {
        using Machine machine = Build();
        machine.Cpu.State.SelectModel(CpuModel.PentiumII);

        Assert.False(machine.Cpu.Msrs.TryWrite(0x200, 0x0000_0000_0010_0007));
        Assert.True(machine.Cpu.Msrs.TryWrite(0x277, 0x0007040600070407));
    }

    /// <summary>
    /// A pending ADC keeps its incoming carry through a file. With the carry
    /// in, 0xFFFFFFFF + 0 + 1 wraps to zero and sets CF; the old format
    /// dropped the carry-in, and the same pending operation then read as
    /// 0xFFFFFFFF + 0 with CF clear.
    /// </summary>
    [Fact]
    public void APendingAddWithCarryKeepsItsCarryThroughAFile()
    {
        using Machine machine = Build();

        // STC; MOV EAX,FFFFFFFF; ADC EAX,0 (operand-size prefix in real mode).
        RunProgram(machine,
            0xF9,
            0x66, 0xB8, 0xFF, 0xFF, 0xFF, 0xFF,
            0x66, 0x83, 0xD0, 0x00);

        MachineSnapshot snapshot = ThroughAFile(MachineSnapshot.Capture(machine));

        machine.Cpu.Flags.CarryIn = false;
        snapshot.RestoreTo(machine);

        Assert.Equal(0u, machine.Cpu.State.Eax);
        Assert.True((machine.Cpu.Flags.Value & 1) != 0, "ADC of FFFFFFFF + 0 + carry sets CF");
    }

    /// <summary>
    /// A file written before sections existed still loads. The test writes
    /// the old layout through <see cref="LegacySnapshotFile"/>, stopping after
    /// A20 as the earliest files did.
    /// </summary>
    [Fact]
    public void AFileFromBeforeSectionsStillLoads()
    {
        string path = Path.GetTempFileName();
        try
        {
            using Machine machine = Build();
            machine.Cpu.State.Eax = 0xCAFEF00D;
            machine.Cpu.State.Segments[(int)SegReg.Es].Limit = 0xFFFFFFFF;
            machine.Memory.WriteByte(0x5000, 0x42);

            LegacySnapshotFile.Write(machine, path, LegacySnapshotEnd.AfterA20);

            machine.Cpu.State.Eax = 0;
            machine.Cpu.State.Segments[(int)SegReg.Es].Limit = 0xFFFF;
            machine.Memory.WriteByte(0x5000, 0);

            MachineSnapshot.Load(path).RestoreTo(machine);

            Assert.Equal(0xCAFEF00Du, machine.Cpu.State.Eax);
            Assert.Equal(0xFFFFFFFFu, machine.Cpu.State.Es.Limit);
            Assert.Equal(0x2000u, machine.Cpu.State.Eip);
            Assert.Equal(0x42, machine.Memory.ReadByte(0x5000));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A known section written by a newer build is refused when the file is
    /// loaded, before the machine is touched. Refusing it later, inside the
    /// restore, would leave RAM from the file under the machine's own
    /// registers.
    /// </summary>
    [Fact]
    public void ANewerSectionVersionIsRefusedBeforeAnythingIsRestored()
    {
        string path = Path.GetTempFileName();
        try
        {
            using Machine machine = Build();
            MachineSnapshot.Capture(machine).Save(path);

            // Raise the CPU section's version, the u16 right after its name.
            byte[] bytes = File.ReadAllBytes(path);
            int tag = bytes.AsSpan().IndexOf("CPU "u8);
            bytes[tag + 4] = 0xFF;
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() => MachineSnapshot.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// An old file that ends inside the processor fields is refused at load
    /// rather than restored with registers read past its end.
    /// </summary>
    [Fact]
    public void AnOldFileCutInsideTheProcessorFieldsIsRefused()
    {
        string path = Path.GetTempFileName();
        try
        {
            using Machine machine = Build();
            LegacySnapshotFile.Write(machine, path, LegacySnapshotEnd.AfterA20);

            // RAM length, RAM, then keep only 100 of the 195 processor bytes.
            int ramLength = machine.Memory.CaptureRam().Length;
            using (FileStream file = File.OpenWrite(path))
            {
                file.SetLength(4 + ramLength + 100);
            }

            Assert.ThrowsAny<Exception>(() => MachineSnapshot.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The coprocessor's special values survive exactly: an infinity from
    /// 1/0 and the NaN from 0/0, which the memory format FSAVE writes would
    /// also carry, but a denormal-range exponent it would not.
    /// </summary>
    [Fact]
    public void SpecialCoprocessorValuesSurviveAFile()
    {
        using Machine machine = Build();

        // FLD1, FLDZ, FDIVP ST(1),ST -> +infinity; FLDZ, FLDZ, FDIVP -> NaN.
        RunProgram(machine, 0xD9, 0xE8, 0xD9, 0xEE, 0xDE, 0xF9, 0xD9, 0xEE, 0xD9, 0xEE, 0xDE, 0xF9);

        bool nanBefore = machine.Cpu.Fpu.Get(0).IsNaN;
        bool infinityBefore = machine.Cpu.Fpu.Get(1).IsInfinity;

        MachineSnapshot snapshot = ThroughAFile(MachineSnapshot.Capture(machine));
        machine.Cpu.Fpu.Reset();
        snapshot.RestoreTo(machine);

        Assert.True(nanBefore && infinityBefore, "the program must produce a NaN over an infinity");
        Assert.True(machine.Cpu.Fpu.Get(0).IsNaN);
        Assert.True(machine.Cpu.Fpu.Get(1).IsInfinity);
    }

    /// <summary>
    /// A section this build does not know is skipped by its length, so a
    /// file from a newer build still restores what this one understands.
    /// </summary>
    [Fact]
    public void AnUnknownSectionIsSkipped()
    {
        string path = Path.GetTempFileName();
        try
        {
            using Machine machine = Build();
            machine.Cpu.State.Ebx = 0x13572468;
            MachineSnapshot.Capture(machine).Save(path);

            // Append a section named "ZZZZ", version 9, with three bytes.
            using (var file = new BinaryWriter(File.Open(path, FileMode.Append)))
            {
                file.Write("ZZZZ"u8.ToArray());
                file.Write((ushort)9);
                file.Write(3);
                file.Write(new byte[] { 1, 2, 3 });
            }

            machine.Cpu.State.Ebx = 0;
            MachineSnapshot.Load(path).RestoreTo(machine);

            Assert.Equal(0x13572468u, machine.Cpu.State.Ebx);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
