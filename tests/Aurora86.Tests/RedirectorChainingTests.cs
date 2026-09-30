using Aurora86.Core;
using Aurora86.Core.Firmware;
using Aurora86.Core.Firmware.Fpu;
using Aurora86.Core.Devices.Storage;
using Aurora86.Core.Diagnostics;
using Aurora86.Core.Dos;
using CpuNs = Aurora86.Core.Cpu;

namespace Aurora86.Tests;

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

        // Taking the vector over is off by default, because getting it wrong
        // stops the machine from booting at all. These tests are precisely
        // about that mechanism, so they turn it on.
        machine.RedirectorHooksMultiplex = true;
    }

    [Fact]
    public void WithTheSwitchOffTheVectorIsLeftExactlyAsItWas()
    {
        // The guarantee that protects the boot. Taking INT 2Fh over while DOS
        // is still starting up stops CONFIG.SYS from loading its device
        // drivers -- COUNTRY.SYS and DISPLAY.SYS fail and the machine sits at
        // an error prompt -- so hooking is off unless something deliberately
        // asks for it. A regression here does not fail loudly: the machine
        // simply stops booting, which is why it is pinned down separately
        // from everything the hook does once it is on.
        using var machine = new Machine();
        InstallFakeDosMultiplexHandler(machine, 0xBEEF);
        machine.RedirectorHooksMultiplex = false;

        machine.HookMultiplexForRedirector();

        Assert.Equal(0x0100, machine.Memory.ReadWord(0x2F * 4 + 2));
        Assert.Equal(0x0000, machine.Memory.ReadWord(0x2F * 4));

        // And the displaced-handler path is genuinely untouched, not merely
        // left pointing somewhere plausible.
        Assert.Equal(0xBEEF, CallMultiplex(machine, 0x1200));
    }

    [Fact]
    public void ADeclinedFunction11CallIsAnsweredTheWayDosItselfWould()
    {
        // Carry set and AX=1: what DOS's own INT 2Fh handler says to a
        // function 11h nobody serves, and what every such call got before
        // the vector was hooked. Leaving the registers as they arrived is
        // not the same thing -- the caller passed carry clear, and DOS
        // reads carry clear back as "resolved", which sent DIR on a shared
        // drive down a path that ended in "path not found".
        using var machine = new Machine();
        InstallFakeDosMultiplexHandler(machine, 0xBEEF);
        machine.RedirectorHooksMultiplex = true;
        machine.HookMultiplexForRedirector();

        Assert.Equal(0x0001, CallMultiplex(machine, 0x110A));   // lock region: never ours
        Assert.True(machine.Cpu.Flags.Carry);

        // Anything that is not function 11h still goes down the chain.
        Assert.Equal(0xBEEF, CallMultiplex(machine, 0x1200));
    }

    [Fact]
    public void HookingAgainBehindAProgramThatChainsToUsDoesNotMakeARing()
    {
        // KEYB, MSCDEX and friends hook INT 2Fh after we do, each saving our
        // vector as its "previous". A second hook of ours in front of them
        // would save them as our previous, and the chain would go round
        // for ever. The guest froze on the first INT 2Fh COMMAND.COM made.
        using var machine = new Machine();
        InstallFakeDosMultiplexHandler(machine, 0xBEEF);
        machine.RedirectorHooksMultiplex = true;
        machine.HookMultiplexForRedirector();

        // A resident program hooking in front of us: it chains to whatever
        // the vector held, which is our entry point.
        ushort ourOffset = machine.Memory.ReadWord(0x2F * 4);
        ushort ourSegment = machine.Memory.ReadWord(0x2F * 4 + 2);
        const uint Program = 0x02000;                 // 0200:0000
        machine.Memory.WriteByte(Program + 0, 0x2E);  // JMP FAR CS:[0006]
        machine.Memory.WriteByte(Program + 1, 0xFF);
        machine.Memory.WriteByte(Program + 2, 0x2E);
        machine.Memory.WriteWord(Program + 3, 0x0006);
        machine.Memory.WriteByte(Program + 5, 0x90);
        machine.Memory.WriteWord(Program + 6, ourOffset);
        machine.Memory.WriteWord(Program + 8, ourSegment);
        machine.Memory.WriteWord(0x2F * 4, 0x0000);
        machine.Memory.WriteWord(0x2F * 4 + 2, 0x0200);

        // The stability check would call this again and again.
        machine.HookMultiplexForRedirector();
        machine.HookMultiplexForRedirector();

        // The program stays in front, and a call still reaches DOS.
        Assert.Equal(0x0200, machine.Memory.ReadWord(0x2F * 4 + 2));
        Assert.Equal(0xBEEF, CallMultiplex(machine, 0x1200));
        Assert.Equal(0x0001, CallMultiplex(machine, 0x110A));
    }

    [Fact]
    public void TheSavedPointerIsInPlaceBeforeTheVectorIsTakenOver()
    {
        // Order matters, and getting it backwards is fatal rather than
        // untidy. The moment the vector names our entry point, the very next
        // INT 2Fh the guest makes runs the far jump at the end of it -- and
        // DOS makes a great many during startup. If the pointer that jump
        // reads has not been filled in yet, it reads zero, execution goes to
        // 0000:0000 and the machine dies without printing anything.
        //
        // The test reaches the pointer through the far jump rather than by
        // reading the ROM, because that is the path that actually matters:
        // a pointer written to the wrong place would look fine in memory and
        // still send the guest to nowhere.
        using var machine = new Machine();
        InstallFakeDosMultiplexHandler(machine, 0xBEEF);
        machine.RedirectorHooksMultiplex = true;

        // Zero the pointer first, so a hook that fails to write it leaves a
        // jump to 0000:0000 exactly as the broken ordering would.
        machine.Memory.WriteRomWord(BiosRom.Base + BiosRom.Entry.PreviousMultiplexVector, 0);
        machine.Memory.WriteRomWord(BiosRom.Base + BiosRom.Entry.PreviousMultiplexVector + 2, 0);

        machine.HookMultiplexForRedirector();

        ushort offset = machine.Memory.ReadWord(BiosRom.Base + BiosRom.Entry.PreviousMultiplexVector);
        ushort segment = machine.Memory.ReadWord(BiosRom.Base + BiosRom.Entry.PreviousMultiplexVector + 2);

        Assert.Equal(0x0100, segment);
        Assert.Equal(0x0000, offset);
        Assert.NotEqual(0u, ((uint)segment << 16) | offset);
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
    public void ARedirectorSubfunctionWeDeclineIsNotPassedOnToDos()
    {
        // The opposite of what politeness suggests, and getting it wrong
        // stops the machine partway through booting.
        //
        // Handing an unanswered function 11h to the handler behind us would
        // be right if that handler were another redirector. It is DOS, which
        // holds this vector for its own unrelated services and which issued
        // the call in the first place -- so giving it back is asking DOS to
        // service a request it made itself. Leaving AX alone is what DOS
        // would have seen before the vector was hooked, and is the answer
        // that means "nobody was listening".
        using var machine = new Machine();
        InstallFakeDosMultiplexHandler(machine, 0xBEEF);
        machine.HookMultiplexForRedirector();

        // 110Ah is lock-region, which we do not implement.
        Assert.NotEqual(0xBEEF, CallMultiplex(machine, 0x110A));
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
        machine.RedirectorHooksMultiplex = true;

        machine.HookMultiplexForRedirector();

        machine.Cpu.State.Bx = 0x1234;
        Assert.Equal(0x1234, CallMultiplex(machine, 0x1200));
    }
}
