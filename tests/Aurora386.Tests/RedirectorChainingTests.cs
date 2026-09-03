using Aurora386.Core;
using Aurora386.Core.Bios;
using Aurora386.Core.Dos;
using CpuNs = Aurora386.Core.Cpu;

namespace Aurora386.Tests;

/// <summary>
/// Tests that the redirector shares INT 2Fh instead of taking it over.
///
/// This is the part of the feature that was hardest to find and is easiest to
/// break. INT 2Fh is not a service but a bus: dozens of unrelated resident
/// programs hang off it, each answering its own function number and passing
/// the rest along. DOS installs a handler of its own during startup, so a
/// redirector must sit in front of it -- and having done so, must pass on
/// everything that is not its business. Swallowing a call silences whoever
/// was behind, and the damage shows up far from its cause: DOS's own
/// multiplex functions stop working, and the machine misbehaves in ways that
/// have nothing visibly to do with a shared folder.
/// </summary>
public class RedirectorChainingTests
{
    /// <summary>
    /// Runs an INT 2Fh in the guest and returns AX, having first put a
    /// recognisable handler in the vector so we can see whether the call
    /// reached it.
    ///
    /// Going through the real interrupt rather than calling the C# handler is
    /// the whole point: what is being tested is the vector and the chaining,
    /// not the subfunction table.
    /// </summary>
    private static ushort CallMultiplex(Machine machine, ushort ax)
    {
        const uint CodeAddress = 0x7000;

        // MOV AX,imm16 ; INT 2Fh ; HLT
        byte[] code = [0xB8, (byte)ax, (byte)(ax >> 8), 0xCD, 0x2F, 0xF4];
        for (int i = 0; i < code.Length; i++)
            machine.Memory.WriteByte(CodeAddress + (uint)i, code[i]);

        CpuNs.Cpu cpu = machine.Cpu;
        cpu.State.Halted = false;
        cpu.SetSegment(CpuNs.SegReg.Cs, 0);
        cpu.State.Eip = CodeAddress;
        cpu.SetSegment(CpuNs.SegReg.Ss, 0);
        cpu.State.Esp = 0x6000;

        for (int step = 0; step < 10_000 && !cpu.State.Halted; step++)
            cpu.Step();

        return cpu.State.Ax;
    }

    /// <summary>
    /// Plants a handler at 0100:0000 that answers by putting a marker in AX,
    /// and points the INT 2Fh vector at it -- standing in for the handler DOS
    /// installs during startup.
    /// </summary>
    private static void InstallFakeDosMultiplexHandler(Machine machine, ushort marker)
    {
        const uint HandlerAddress = 0x1000;   // 0100:0000

        // MOV AX,marker ; IRET
        machine.Memory.WriteByte(HandlerAddress + 0, 0xB8);
        machine.Memory.WriteByte(HandlerAddress + 1, (byte)marker);
        machine.Memory.WriteByte(HandlerAddress + 2, (byte)(marker >> 8));
        machine.Memory.WriteByte(HandlerAddress + 3, 0xCF);

        machine.Memory.WriteWord(0x2F * 4, 0x0000);
        machine.Memory.WriteWord(0x2F * 4 + 2, 0x0100);
    }

    [Fact]
    public void HookingTakesTheVectorButRemembersWhoHadIt()
    {
        using var machine = new Machine();
        InstallFakeDosMultiplexHandler(machine, 0xBEEF);

        machine.HookMultiplexForRedirector();

        Assert.Equal(0xF000, machine.Memory.ReadWord(0x2F * 4 + 2));
        Assert.Equal(BiosRom.Entry.RedirectorMultiplex, machine.Memory.ReadWord(0x2F * 4));
    }

    [Fact]
    public void ACallThatIsNotOursReachesTheHandlerWeDisplaced()
    {
        // The heart of the matter. Function 12h is DOS's own -- its internal
        // service routines -- and it has to keep working after we hook the
        // vector, or DOS breaks in ways that look unrelated.
        using var machine = new Machine();
        InstallFakeDosMultiplexHandler(machine, 0xBEEF);
        machine.HookMultiplexForRedirector();

        Assert.Equal(0xBEEF, CallMultiplex(machine, 0x1200));
    }

    [Fact]
    public void ARedirectorCallIsAnsweredWithoutReachingTheHandlerBehind()
    {
        // The installation check, which is ours. If it were passed on, the
        // displaced handler's marker would come back instead of AL=FFh.
        using var machine = new Machine();
        InstallFakeDosMultiplexHandler(machine, 0xBEEF);
        machine.HookMultiplexForRedirector();

        ushort result = CallMultiplex(machine, 0x1100);

        Assert.Equal(0xFF, result & 0xFF);
        Assert.NotEqual(0xBEEF, result);
    }

    [Fact]
    public void ARedirectorSubfunctionWeDeclineIsStillPassedOn()
    {
        // Declining is not the same as answering. A subfunction inside
        // function 11h that we do not implement must go to whoever is behind
        // us, because another redirector may well handle it -- that is how
        // two of them coexisted on the same machine.
        using var machine = new Machine();
        InstallFakeDosMultiplexHandler(machine, 0xBEEF);
        machine.HookMultiplexForRedirector();

        // 110Ah is lock-region, which we do not implement.
        Assert.Equal(0xBEEF, CallMultiplex(machine, 0x110A));
    }

    [Fact]
    public void HookingTwiceDoesNotChainTheHandlerToItself()
    {
        // A second hook that recorded our own address as "the previous
        // handler" would make every unhandled call loop forever, and the
        // machine would simply stop. The re-check that runs periodically
        // makes this a real possibility rather than a theoretical one.
        using var machine = new Machine();
        InstallFakeDosMultiplexHandler(machine, 0xBEEF);

        machine.HookMultiplexForRedirector();
        machine.HookMultiplexForRedirector();

        Assert.Equal(0xBEEF, CallMultiplex(machine, 0x1200));
    }

    [Fact]
    public void ChainingLeavesTheCallersRegistersAlone()
    {
        // The displaced handler must see the call exactly as the caller made
        // it. Rewriting the return address is the only thing that may change.
        using var machine = new Machine();

        const uint HandlerAddress = 0x1000;

        // MOV AX,BX ; IRET -- reports back whatever BX held on entry.
        machine.Memory.WriteByte(HandlerAddress + 0, 0x89);
        machine.Memory.WriteByte(HandlerAddress + 1, 0xD8);
        machine.Memory.WriteByte(HandlerAddress + 2, 0xCF);
        machine.Memory.WriteWord(0x2F * 4, 0x0000);
        machine.Memory.WriteWord(0x2F * 4 + 2, 0x0100);

        machine.HookMultiplexForRedirector();

        machine.Cpu.State.Bx = 0x1234;
        Assert.Equal(0x1234, CallMultiplex(machine, 0x1200));
    }
}
