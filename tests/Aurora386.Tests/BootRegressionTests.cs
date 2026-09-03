using Aurora386.Core;
using Aurora386.Core.Bios;
using Aurora386.Core.Memory;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Regressions found while getting MS-DOS 6.22 to boot from a real disk
/// image. Every one of these bugs presented as "the system does not start",
/// with the visible symptom a very long way from the cause; without a test
/// pinning each one down, they would creep back unnoticed.
/// </summary>
public class BootRegressionTests
{
    private const uint CodeBase = 0x1000;

    private static (CpuNs.Cpu cpu, PhysicalMemoryBus bus) Setup(params byte[] code)
    {
        var bus = new PhysicalMemoryBus();
        var cpu = new CpuNs.Cpu(bus);
        foreach (CpuNs.SegReg s in Enum.GetValues<CpuNs.SegReg>())
            cpu.State.LoadSegmentRealMode(s, 0);
        cpu.State.Esp = 0x8000;
        cpu.State.Eip = CodeBase;
        code.CopyTo(bus.GetRamSpan(CodeBase, code.Length));
        return (cpu, bus);
    }

    [Fact]
    public void DirectFarCallPushesCsAndIpThenJumps()
    {
        // 0x9A: without this instruction DOS cannot make a single
        // inter-segment call, and every attempt was raising an invalid-opcode
        // exception -- which is to say an INT, quietly sending execution
        // somewhere else entirely.
        //   9A 00 20 00 30   CALL FAR 3000:2000
        var (cpu, bus) = Setup(0x9A, 0x00, 0x20, 0x00, 0x30);
        using (bus)
        {
            cpu.Step();

            Assert.Equal(0x3000, cpu.State.Cs.Selector);
            Assert.Equal(0x2000u, cpu.State.Eip);

            // On the stack: the return segment is pushed first, then the
            // offset, so the offset ends up at the lower address.
            Assert.Equal(0x7FFCu, cpu.State.Esp);
            Assert.Equal(CodeBase + 5, bus.ReadWord(0x7FFC));   // return IP
            Assert.Equal(0x0000, bus.ReadWord(0x7FFE));         // return CS
        }
    }

    [Fact]
    public void FarCallAndRetfAreSymmetric()
    {
        // The test that actually matters: out and back must land exactly
        // where execution left off, with the stack balanced.
        //   9A 00 20 00 00   CALL FAR 0000:2000
        //   90               NOP  (the return point)
        var (cpu, bus) = Setup(0x9A, 0x00, 0x20, 0x00, 0x00);
        using (bus)
        {
            bus.WriteByte(0x2000, 0xCB);      // RETF in the called routine

            cpu.Step();                        // CALL FAR
            Assert.Equal(0x2000u, cpu.State.Eip);

            cpu.Step();                        // RETF
            Assert.Equal(CodeBase + 5, cpu.State.Eip);
            Assert.Equal(0x0000, cpu.State.Cs.Selector);
            Assert.Equal(0x8000u, cpu.State.Esp);   // stack balanced
        }
    }

    [Fact]
    public void XlatTranslatesThroughTheTable()
    {
        // 0xD7: DOS executes this millions of times for character
        // translation, so getting it wrong is not subtle.
        //   D7   XLAT  ->  AL = DS:[BX + AL]
        var (cpu, bus) = Setup(0xD7);
        using (bus)
        {
            // Table at 0x3000: maps each index to twice its value.
            for (uint i = 0; i < 256; i++) bus.WriteByte(0x3000 + i, (byte)(i * 2));

            cpu.State.Bx = 0x3000;
            cpu.State.SetReg8(0, 5);      // AL = index into the table
            cpu.Step();

            Assert.Equal(10, cpu.State.GetReg8(0));
        }
    }

    [Fact]
    public void XlatHonoursTheSegmentOverride()
    {
        //   2E D7   XLAT with a CS segment override
        var (cpu, bus) = Setup(0x2E, 0xD7);
        using (bus)
        {
            cpu.State.LoadSegmentRealMode(CpuNs.SegReg.Cs, 0);
            cpu.State.LoadSegmentRealMode(CpuNs.SegReg.Ds, 0x1000);   // a different DS
            bus.WriteByte(0x4000, 0xAA);      // what CS would reach
            bus.WriteByte(0x14000, 0xBB);     // what DS would reach

            cpu.State.Bx = 0x4000;
            cpu.State.SetReg8(0, 0);
            cpu.Step();

            Assert.Equal(0xAA, cpu.State.GetReg8(0));   // the override was honoured
        }
    }

    [Fact]
    public void NoInterruptVectorIsLeftAtZero()
    {
        // DOS reads words out of the interrupt vector table and uses them as
        // segment values, even for vectors it does not itself handle. A zero
        // there does not mean "unused": it means a pointer to 0000:0000,
        // where there is no code at all.
        using var machine = new Machine();
        machine.RunInstructions(300);

        for (int vector = 0; vector < 256; vector++)
        {
            ushort offset = machine.Memory.ReadWord((uint)vector * 4);
            ushort segment = machine.Memory.ReadWord((uint)vector * 4 + 2);

            Assert.True(segment != 0 || offset != 0,
                $"vector {vector:X2}h is null: DOS would still use it as a pointer");
        }
    }

    [Fact]
    public void EveryVectorPointsAtExecutableCode()
    {
        // Being non-zero is not enough: the vector has to lead to real code.
        //
        // Three of them lead to data instead, and always have: the PC
        // architecture reuses the vector table as a directory of tables.
        // 1Eh addresses the diskette parameters, 1Fh the upper half of the
        // graphics font, 43h the font a program has loaded. Nothing ever
        // calls them -- software follows the pointer and reads.
        int[] pointToData = [0x1E, 0x1F, 0x43];

        using var machine = new Machine();
        machine.RunInstructions(300);

        for (int vector = 0; vector < 256; vector++)
        {
            if (pointToData.Contains(vector)) continue;

            ushort offset = machine.Memory.ReadWord((uint)vector * 4);
            ushort segment = machine.Memory.ReadWord((uint)vector * 4 + 2);
            uint linear = (uint)segment * 16 + offset;

            byte first = machine.Memory.ReadByte(linear);
            // Every entry point begins either with the native trap hook
            // (0F FF) or with an immediate IRET.
            Assert.True(first == 0x0F || first == 0xCF,
                $"vector {vector:X2}h points at {segment:X4}:{offset:X4}, " +
                $"where the first byte is {first:X2}, which is not handler code");
        }
    }

    [Fact]
    public void Int13hClearsAhOnSuccess()
    {
        // AH is the error code on return, so leaving the function number in
        // it convinces DOS that the read failed. With AH=02 ("address mark
        // not found") DOS declares a file corrupt that was in fact read
        // perfectly.
        //   B4 02  MOV AH,2      (read sectors)
        //   B0 01  MOV AL,1
        //   B5 00  MOV CH,0
        //   B1 01  MOV CL,1
        //   B6 00  MOV DH,0
        //   B2 00  MOV DL,0
        //   BB 00 60  MOV BX,0x6000
        //   CD 13  INT 13h
        //   F4     HLT
        var sector = new byte[512];
        new byte[] {
            0xB4, 0x02, 0xB0, 0x01, 0xB5, 0x00, 0xB1, 0x01,
            0xB6, 0x00, 0xB2, 0x00, 0xBB, 0x00, 0x60, 0xCD, 0x13, 0xF4,
        }.CopyTo(sector, 0);
        sector[510] = 0x55; sector[511] = 0xAA;

        using var machine = new Machine();
        DiskImage disk = DiskImage.CreateInMemory(0x00, 80, 2, 18);
        disk.WriteSectors(0, 0, 1, 1, sector);
        machine.AttachDisk(disk);
        machine.RunInstructions(3000);

        Assert.Equal(0, machine.Cpu.State.GetReg8(4));   // AH = 0: no error
        Assert.Equal(1, machine.Cpu.State.GetReg8(0));   // AL = sectors actually read
    }

    [Fact]
    public void Int13hOnAMissingDriveClearsTheRegisters()
    {
        // A drive that is not there must not leave the count from a previous
        // call sitting in DL: DOS reads that to learn how many drives exist,
        // and would go on to enumerate ones that do not.
        using var machine = new Machine();
        machine.RunInstructions(300);
        var cpu = machine.Cpu;

        // Set up the stack as it would look just after an INT.
        cpu.State.Esp = 0x7000;
        cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Memory.WriteWord(0x7004, 0);

        // Query a drive that exists first, then one that does not.
        DiskImage floppy = DiskImage.CreateInMemory(0x00, 80, 2, 18);
        machine.AttachDisk(floppy);

        cpu.State.SetReg8(4, 0x08);
        cpu.State.SetReg8(2, 0x00);
        machine.DiskServices.Handle(cpu);
        Assert.NotEqual(0, cpu.State.Dx);      // the drive is there

        cpu.State.SetReg8(4, 0x08);
        cpu.State.SetReg8(2, 0x80);            // a hard disk that is not fitted
        machine.DiskServices.Handle(cpu);

        Assert.Equal(0, cpu.State.Dx);         // nothing left over from before
        Assert.Equal(0, cpu.State.Cx);
        ushort flags = machine.Memory.ReadWord(0x7004);
        Assert.Equal(1, flags & 1);            // carry set: the error was reported
    }

    [Fact]
    public void Int13hAh15ReportsTheDriveTypeInAhWithoutCarry()
    {
        // For this one function AH is not an error code but the drive type,
        // so clearing it the way the other functions do would make the call
        // report nothing at all.
        using var machine = new Machine();
        machine.RunInstructions(300);
        var cpu = machine.Cpu;

        cpu.State.Esp = 0x7000;
        cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        machine.Memory.WriteWord(0x7004, 0);

        machine.AttachDisk(DiskImage.CreateInMemory(0x00, 80, 2, 18));

        cpu.State.SetReg8(4, 0x15);
        cpu.State.SetReg8(2, 0x00);
        machine.DiskServices.Handle(cpu);

        // AH=02 means "floppy with a disk change line". The value 01 would
        // be a floppy without one, and DOS would then infer media changes from
        // elapsed time rather than asking function 0x16 -- which is exactly
        // the mechanism a disk swap during installation depends on.
        Assert.Equal(0x02, cpu.State.GetReg8(4));           // floppy with change-line
        Assert.Equal(0, machine.Memory.ReadWord(0x7004) & 1);   // no error
    }

    [Fact]
    public void LoadingSsInhibitsInterruptsForOneInstruction()
    {
        // Switching stacks takes two instructions (MOV SS,.. then MOV SP,..)
        // and in between them SS:SP points at an area that does not exist.
        // The architecture guarantees no interrupt can slip in there; without
        // that guarantee the handler would push its saved context into an
        // arbitrary spot in memory and quietly corrupt it.
        var bus = new PhysicalMemoryBus();
        var io = new Aurora386.Core.IO.IoBus();
        var interrupts = new Aurora386.Core.Devices.InterruptController();
        interrupts.Register(io);
        var cpu = new CpuNs.Cpu(bus, io) { Interrupts = interrupts };

        using (bus)
        {
            foreach (CpuNs.SegReg s in Enum.GetValues<CpuNs.SegReg>())
                cpu.State.LoadSegmentRealMode(s, 0);
            cpu.State.Esp = 0x9000;
            cpu.State.Eip = CodeBase;

            // An IRQ 0 handler that leaves a mark to prove it ran.
            bus.WriteWord(0x08 * 4, 0x0500);
            bus.WriteWord(0x08 * 4 + 2, 0x0000);
            new byte[] {
                0xC6, 0x06, 0x00, 0x70, 0x01,   // MOV byte [0x7000],1
                0xB0, 0x20, 0xE6, 0x20,          // EOI
                0xCF,                            // IRET
            }.CopyTo(bus.GetRamSpan(0x0500, 10));

            //   B8 00 20   MOV AX,0x2000
            //   8E D0      MOV SS,AX      <- interrupts inhibited from here
            //   BC 00 10   MOV SP,0x1000  <- the stack is only coherent from here
            //   90         NOP
            new byte[] { 0xB8, 0x00, 0x20, 0x8E, 0xD0, 0xBC, 0x00, 0x10, 0x90 }
                .CopyTo(bus.GetRamSpan(CodeBase, 9));

            cpu.Flags.Interrupt = true;
            io.WriteByte(0x20, 0x11); io.WriteByte(0x21, 0x08);
            io.WriteByte(0x21, 0x04); io.WriteByte(0x21, 0x01); io.WriteByte(0x21, 0xFE);

            cpu.StepWithInterrupts();          // MOV AX
            interrupts.RaiseIrq(0);            // the interrupt arrives RIGHT NOW
            cpu.StepWithInterrupts();          // MOV SS: must inhibit it

            // The handler must not have run: we are sitting between MOV SS
            // and MOV SP, in the window the architecture protects.
            Assert.Equal(0, bus.ReadByte(0x7000));
            Assert.Equal(0x2000, cpu.State.Ss.Selector);

            // MOV SP completes the switch; the interrupt is serviced right
            // afterwards and the handler pushes IP, CS and FLAGS onto the new
            // stack -- which is now valid. SP therefore drops by six bytes.
            cpu.StepWithInterrupts();
            Assert.Equal(0x1000u - 6, cpu.State.Esp);
            Assert.Equal(0x0500u, cpu.State.Eip);   // inside the handler

            // The handler leaves its mark.
            cpu.StepWithInterrupts();
            Assert.Equal(1, bus.ReadByte(0x7000));

            // The assertion that matters: the saved context landed on the new
            // stack, not in some arbitrary region of memory.
            Assert.Equal(0x2000u * 16 + 0x1000 - 6, cpu.State.Ss.Base + cpu.State.Esp);
        }
    }

    [Fact]
    public void UnknownOpcodeIsReported()
    {
        // The diagnostic that led us to the missing XLAT: without it, an
        // unimplemented opcode is indistinguishable from a bad jump.
        var (cpu, bus) = Setup(0x0F, 0x0B);   // a two-byte opcode that does not exist
        using (bus)
        {
            byte reported = 0;
            bool called = false;
            cpu.UnknownTwoByteOpcode += (op, ip, cs) => { reported = op; called = true; };

            bus.WriteWord(6 * 4, 0x0500);     // vector for exception 6
            bus.WriteWord(6 * 4 + 2, 0x0000);
            cpu.Step();

            Assert.True(called, "the unknown opcode was not reported");
            Assert.Equal(0x0B, reported);
        }
    }
}
