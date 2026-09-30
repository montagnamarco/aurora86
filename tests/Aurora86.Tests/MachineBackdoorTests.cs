using Aurora86.Core;
using Aurora86.Core.Cpu;
using Aurora86.Core.Devices.Host;
using Aurora86.Core.Devices.Input;

namespace Aurora86.Tests;

/// <summary>
/// The VMware-compatible backdoor as fitted to a machine
/// (docs/VMWARE-BACKDOOR.md): silent by default, answering once enabled,
/// reached through a real <c>IN EAX, DX</c>, and surviving a snapshot.
/// </summary>
public class MachineBackdoorTests
{
    private const ushort GetVersionCommand = 10;
    private const uint AllOnes = 0xFFFFFFFF;

    [Fact]
    public void ADefaultMachineDoesNotAnswerTheBackdoor()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);

        CallBackdoor(machine, GetVersionCommand);

        // Nothing decodes port 5658h: the read floats high and EBX keeps
        // what the caller put there, which is what detection code tests.
        Assert.Equal(AllOnes, machine.Cpu.State.Eax);
        Assert.NotEqual(VmwareBackdoor.Magic, machine.Cpu.State.Ebx);
    }

    [Fact]
    public void AnEnabledMachineAnswersGetVersionThroughTheProcessor()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.Backdoor.Enabled = true;

        CallBackdoor(machine, GetVersionCommand);

        Assert.Equal(6u, machine.Cpu.State.Eax);
        Assert.Equal(VmwareBackdoor.Magic, machine.Cpu.State.Ebx);
    }

    [Fact]
    public void TheVmConfigurationSwitchEnablesThePort()
    {
        var configuration = new Aurora86.Core.Configuration.VmConfiguration { HostIntegration = true };

        using Machine machine = configuration.BuildMachine();

        Assert.True(machine.Backdoor.Enabled);
    }

    /// <summary>
    /// Whether the port answers is the receiving machine's configuration: a
    /// snapshot taken with the port on, resumed on a machine started without
    /// it, stays off -- and the absolute pointer's mode comes from the file.
    /// </summary>
    [Fact]
    public void ASnapshotKeepsTheReceivingMachinesConfigurationAndCarriesThePointerMode()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (var source = new Machine())
            {
                source.RunInstructions(200);
                source.Backdoor.Enabled = true;
                EnableAbsolutePointer(source);
                MachineSnapshot.Capture(source).Save(path);
            }

            using var disabledTarget = new Machine();
            disabledTarget.RunInstructions(200);
            MachineSnapshot.Load(path).RestoreTo(disabledTarget);

            Assert.False(disabledTarget.Backdoor.Enabled);
            Assert.True(disabledTarget.Backdoor.PointerEnabled);
            Assert.True(disabledTarget.Backdoor.PointerAbsolute);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Host motion reaches a guest that asked for absolute packets as a
    /// position scaled to 0-FFFFh across the displayed picture. The position
    /// starts at the centre; driving it far past the top-left corner clamps
    /// it there, which is 0,0.
    /// </summary>
    [Fact]
    public void HostMotionBecomesAnAbsolutePacketOnceTheGuestAsksForOne()
    {
        const uint AbsolutePointerData = 39;
        const uint AbsolutePointerStatus = 40;
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.Display.Render();
        machine.Backdoor.Enabled = true;
        EnableAbsolutePointer(machine);

        machine.Mouse.Move(-10_000, -10_000);
        machine.Mouse.SetButton(MouseButtons.Left, true);

        CpuState status = Call(machine, AbsolutePointerStatus, 0);
        Assert.Equal(8u, status.Eax);                    // two packets of four dwords

        CpuState corner = Call(machine, AbsolutePointerData, 4);
        Assert.Equal(0u, corner.Ebx);
        Assert.Equal(0u, corner.Ecx);

        CpuState pressed = Call(machine, AbsolutePointerData, 4);
        Assert.Equal(0x20u, pressed.Eax & 0x20);           // left button
    }

    /// <summary>
    /// A button pressed while the ordinary mouse owned it and released after
    /// the guest switched to absolute packets must not stay held on the
    /// ordinary mouse once the absolute pointer goes away again.
    /// </summary>
    [Fact]
    public void AButtonReleasedOnTheAbsoluteRouteIsReleasedOnTheOrdinaryMouseAfterwards()
    {
        using var machine = new Machine();
        machine.RunInstructions(200);
        machine.Display.Render();
        machine.Backdoor.Enabled = true;
        machine.Mouse.ConnectedInterface = MouseInterface.Ps2;

        machine.Mouse.SetButton(MouseButtons.Left, true);
        EnableAbsolutePointer(machine);
        machine.Mouse.SetButton(MouseButtons.Left, false);
        machine.Backdoor.Reset();                            // the guest driver goes away
        machine.Mouse.Move(1, 0);

        Assert.Equal(MouseButtons.None, machine.Mouse.Buttons & MouseButtons.Left);
    }

    /// <summary>Enables the absolute pointer the way Linux's vmmouse driver does: ENABLE, read the version, REQUEST_ABSOLUTE.</summary>
    private static void EnableAbsolutePointer(Machine machine)
    {
        const uint AbsolutePointerCommand = 41;
        const uint AbsolutePointerData = 39;
        const uint EnableSubcommand = 0x45414552;
        const uint RequestAbsoluteSubcommand = 0x53424152;

        Call(machine, AbsolutePointerCommand, EnableSubcommand);
        CpuState version = Call(machine, AbsolutePointerData, 1);
        Assert.Equal(0x3442554Au, version.Eax);
        Call(machine, AbsolutePointerCommand, RequestAbsoluteSubcommand);
    }

    /// <summary>One backdoor call made directly on the device, with EBX as its argument.</summary>
    private static CpuState Call(Machine machine, uint command, uint argument)
    {
        var state = new CpuState
        {
            Eax = VmwareBackdoor.Magic,
            Ecx = command,
            Ebx = argument,
            Edx = VmwareBackdoor.Port,
        };
        Assert.True(machine.Backdoor.TryHandleInput(state, VmwareBackdoor.Port));
        return state;
    }

    /// <summary>MOV EAX,magic / MOV ECX,command / MOV DX,5658h / IN EAX,DX, in real mode.</summary>
    private static void CallBackdoor(Machine machine, ushort command)
    {
        const uint EbxSentinel = 0x12345678;
        machine.Cpu.State.Ebx = EbxSentinel;

        byte[] code =
        [
            0x66, 0xB8, 0x68, 0x58, 0x4D, 0x56,                        // MOV EAX, 564D5868h
            0x66, 0xB9, (byte)command, (byte)(command >> 8), 0, 0,     // MOV ECX, command
            0xBA, 0x58, 0x56,                                          // MOV DX, 5658h
            0x66, 0xED,                                                // IN EAX, DX
            0xF4,                                                      // HLT
        ];
        code.CopyTo(machine.Memory.GetRamSpan(0x20100, code.Length));
        machine.Cpu.SetSegment(SegReg.Cs, 0x2000);
        machine.Cpu.State.Eip = 0x100;
        machine.Cpu.State.Halted = false;
        for (int count = 0; count < 100 && !machine.Cpu.State.Halted; count++)
        {
            machine.RunInstructions(1);
        }

        Assert.True(machine.Cpu.State.Halted);
    }
}
