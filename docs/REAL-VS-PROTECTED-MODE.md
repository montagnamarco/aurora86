# Real mode, protected mode, and the places in between

This is the guide to the part of the 386 that confuses everybody, written
around the code in `src/Aurora386.Core/Cpu` and around the bugs that were
actually found while getting MS-DOS 6.22 and Windows 3.11 to run. Every
section names the file that implements it, so you can read the two side by
side.

## 1. Two ways of turning an address into a byte

An x86 program never names a byte directly. It names a *segment* and an
*offset*, and the processor combines them. What "combine" means is the whole
difference between the modes.

**Real mode** (the 8086's mode, and the one the 386 wakes up in) is
arithmetic: the segment register holds a paragraph number, and the address is
`segment * 16 + offset`. Ordinary real-mode segments have a 64 KB limit. The largest
segment-plus-offset sum is `0x10FFEF`, which exceeds one megabyte: the A20
gate determines whether bit 20 reaches the bus or wraps as on an 8086. A
386 also accepts address-size overrides; access still obeys the cached limit. There is no protection
of any kind: any program can write anywhere, including over the interrupt
vector table at address 0.

**Protected mode** is a lookup: the segment register holds a *selector*, an
index into a table of *descriptors*, and the descriptor says where the
segment starts (the base), how long it is (the limit), what it may be used
for (code, data, stack, system), and who may use it (the privilege level).
The address is `base + offset`, but only after the processor has checked the
offset against the limit and the access against the rights. Offsets can be
32 bits, so a segment can cover 4 GB.

In Aurora386 the two live in `CpuState.cs` as the *segment cache*: for every
segment register we keep the base, limit and rights that were loaded with it,
exactly as the silicon does. `LoadSegmentRealMode` updates the base to `paragraph * 16` while retaining
the cached limit; its reset variant also restores the ordinary 64 KB limit.
`LoadSegmentProtected` in
`Cpu.Protected.cs` fetches the descriptor and fills it from that. Once the
cache is loaded, *every* memory access in either mode goes through the same
code: `CheckLimit` and `base + offset` in `Cpu.cs`.

That is not a simplification. It is how the hardware works, and it explains
the first strange thing.

### Unreal mode

Load a segment register in protected mode with a descriptor whose limit is
4 GB, then switch back to real mode without touching that register. The
cache still says "limit 4 GB". Real mode only changes how the *base* is
computed on the next load; it does not reset the limit. From now on, in real
mode, `mov eax, [es:0x12345678]` works.

HIMEM.SYS uses exactly this to copy to extended memory without leaving real
mode, and it is the reason the cache must survive a mode switch untouched.
The regression is in `UnrealModeTests`. V86 loads use the reset variant,
so a virtual DOS task does not inherit an unreal-mode segment limit.

## 2. The tables

Protected mode needs three tables, and the registers that point at them are
the first thing an operating system sets up:

| Table | Register | Holds |
|---|---|---|
| GDT, global descriptor table | `GDTR` (`GdtBase`, `GdtLimit`) | descriptors everyone can see |
| LDT, local descriptor table | `LDTR` (a selector into the GDT) | descriptors for one task |
| IDT, interrupt descriptor table | `IDTR` | one *gate* per interrupt vector |

A selector is 16 bits: 13 bits of index, one bit (`TI`) choosing GDT or LDT,
and two bits of *requested privilege level* (`RPL`). `ReadDescriptor` in
`Cpu.Protected.cs` does the lookup. Descriptors are eight bytes with the
fields scattered in the least convenient order history could devise; the
decoding is in `LoadSegmentProtected`, and `WriteDescriptor` in `ProtectedModeTests` constructs those fields for
executable regression cases.

In an earlier measured standard-mode boot Windows put its GDT at 0x310800 and its LDT at 0x314C80,
both in extended memory; in enhanced mode the tables live behind paging at
linear addresses like 0x80663000. Neither address means anything to a real
mode program, which is the point.

## 3. Rings, and why CLI faults

Every descriptor carries a *descriptor privilege level* (DPL, 0 to 3), and
the code segment currently executing defines the *current privilege level*
(CPL). Ring 0 is the kernel; ring 3 is the application. A ring 3 program may
not load a ring 0 data selector, may not jump into ring 0 code except through
a gate, and may not execute the instructions that would let it take the
machine over.

The rules differ by instruction. In protected mode, `CLI` and `STI`
require `CPL <= IOPL`; insufficient privilege raises `#GP(0)`. `POPF` and
`IRET` instead filter which flag bits may change. Port instructions consult
the TSS I/O bitmap when privilege does not grant direct access. In V86,
`CLI`, `STI`, `PUSHF`, `POPF`, `INT n` and `IRET` require IOPL 3, while port
access always consults the bitmap. A clear bitmap bit permits the access;
a set bit traps it. Every byte of a word or dword access must be permitted.

Two rules follow, and both bit us:

- Only ring 0 may change IOPL. `POPF` and `IRET` at any other level keep the
  old IOPL whatever the stacked image says; `IF` moves only when
  `CPL <= IOPL`. The filter is `GuardFlagsFromStack` in `Cpu.Ops.cs`.
  **The bug**: the protected mode `IRET` did not apply it. DOSX enters
  Windows at ring 3 with IOPL 3, but its real mode side builds frames with
  IOPL 0; one ring 3 `IRET` later every `CLI` in USER.EXE faulted, the kernel
  answered with a MessageBox, whose painting executed another `CLI`, and 26
  levels down DOSX's 4 KB exception stack ran out. The symptom was Windows
  Setup freezing with an hourglass after the fonts dialog.
- Port access is also allowed through the *I/O permission bitmap* in the
  TSS, bit by bit, whatever IOPL says (`IoPermitted` in `Cpu.Ops.cs`).
  Windows enhanced mode uses it to trap exactly the VGA ports it shadows and
  let everything else through at full speed.

## 4. Interrupts and gates

In real mode an interrupt reads a `segment:offset` pair from the vector
table at address 0 and pushes `FLAGS`, `CS`, `IP`. In protected mode it reads
a *gate* from the IDT, and the gate decides what happens:

- an **interrupt gate** or **trap gate** names a code selector and an
  offset; the processor checks that the target ring is not less privileged
  than the current one, switches to the destination privilege level’s stack in the TSS if the
  privilege rises, pushes `SS:ESP` (only then), `EFLAGS`, `CS:EIP`, and for
  some exceptions an *error code*; an interrupt gate also clears `IF`;
- a **task gate** names a TSS and switches the task. This is implemented in
  `Cpu.Tasks.cs`: the outgoing state is saved, the incoming state loaded,
  and busy bits, backlinks and the nested-task flag follow the transfer
  type. CALL/interrupt nesting and an IRET with NT set differ from a JMP.
  `TaskSwitchTests` exercises these paths; task switching has fault points
  both before and after the incoming task becomes current.

`EnterInterruptProtected` in `Cpu.Protected.cs` does all of it.
`IretProtected` undoes it, in the same order, including popping `SS:ESP`
when returning to a less privileged ring. A call gate is a separate GDT/LDT
mechanism: it can switch stacks and copy a specified count of parameters.
`CallThroughGate` and the far-transfer helpers implement it.

Gate validation order matters. EMM386 executed a V86 `INT 12h` against a
non-present gate that also failed the software-INT privilege check. Testing
presence first delivered #NP; checking type and privilege first delivers
#GP, which the monitor can reflect. `VirtualModeTests` covers this ordering.

Which exceptions push an error code is a table you have to know: `#DF`,
`#TS`, `#NP`, `#SS`, `#GP`, `#PF`, `#AC` do; the others do not; and a
*software* `INT 8` or `INT 13` pushes nothing, because the error code belongs
to the fault, not to the vector. `ExceptionPushesErrorCode` encodes the
table; `isException` tells the two apart. **The bug**: pushing an error code
for a hardware `IRQ 0` on vector 8 -- DOSX leaves the PIC on vectors 8-15 --
made the standard mode kernel print "errore in MS-DOS Extender" the moment
the timer ticked.

The TSS comes in two sizes, the 286 one (types 1 and 3) with 16-bit fields
and the 386 one (types 9 and 11) with 32-bit fields, and the ring 0 stack
pointer sits at a different offset in each. `ReadStackFromTss` handles
both; DOSX uses the 286 layout, WIN386 the 386 one.

## 5. Faults are restartable, or nothing works

A fault reports the address of the instruction that caused it, not the one
after, so that the handler can fix the cause and return to retry it. Page
faults depend on this: the kernel brings the page in and returns. So does
every "load on demand" trick: Windows marks a segment *not present*, lets
the program `CALL FAR` into it, takes the `#NP`, loads the segment from
disk, and returns to the same `CALL FAR`.

That last case is harder than it looks. A far call that faults halfway may
already have pushed the return address or loaded the new `CS`; if the fault
is delivered with the stack half-unwound, the retry pushes twice. `CallFarInto`
in `Cpu.Ops.cs` restores `ESP` before rethrowing, and `IretProtected` keeps
the whole frame if it fails partway. The `#NP` restart is tested in
`VirtualModeTests`.

A second example is `POP [memory]`: the stack read can succeed before the
write faults. The S3 VDD used `POP [EAX+87h]` at 0028:8001493Eh to restore
state into a paged-out buffer. Advancing ESP before delivering #PF made the
retry skip a dword and eventually return into VM data. `Cpu.Execute.cs`
now restores ESP on that failure. Four `ProtectedModeTests` cases page in
the missing destination and retry through IRETD, covering 16/32-bit values
and writes across a page boundary. See [the DOS-session trace](WINDOWS-DOS.md).

Delivering a fault is itself a series of memory accesses that can fault. The
architecture's table of which second faults are *contributory* (and become
`#DF`) versus *benign* (and are simply delivered instead) is in
`IsDoubleFault`; a double fault that cannot be delivered is a triple fault,
and the processor resets. `Escalate` in `Cpu.Execute.cs` follows the table,
and `TripleFault` is the event the machine raises when it happens -- you can
watch for it in a harness, which is how the V86 problems were found.

A note on cost. Every fault in the emulator was a C# `throw`, about eight
microseconds. Windows in enhanced mode raises about a million per 300 million
instructions -- its monitor plants `ARPL` as a breakpoint in every V86 stub
and traps every `CLI`, `STI`, port access and `INT n` from ring 3. That
halved the emulator's speed. The hot paths now deliver the fault directly
(`FaultNow`) without unwinding the host stack.

## 6. Virtual 8086 mode

The 386 can run a real mode program as a *task* inside protected mode: the
`VM` bit in EFLAGS makes segment loads arithmetic again (`paragraph * 16`),
but paging, privilege and the IOPL rules stay in force, and privileged operations trap when their individual privilege or bitmap
checks fail. That is how EMM386 gives DOS
expanded memory and how Windows enhanced mode runs DOS boxes.

Three things about V86 are non-obvious, and each was a bug here:

- **The VM bit is not in the general EFLAGS image.** We keep it as
  `Virtual8086Mode` in the state, and put it back into the value that goes on
  the stack when an interrupt enters the monitor, so that the monitor's
  `IRET` returns into V86 rather than into ring 3 protected code
  (`EnterInterruptProtected`, `EnterVirtual8086`). Leave it out and the
  monitor returns to a paragraph number as if it were a selector.
- **Undefined opcodes are a feature.** WIN386 places `63h` (`ARPL`), which
  is undefined in V86 and real mode, in its V86 stubs and takes the resulting
  `#UD` as its cue to step back in. Emulate `ARPL` there and the stub runs on
  into data. `Cpu.Execute.cs` raises `#UD` for `63h` outside protected mode.
- **The monitor's entry has to look exactly right**: on entering ring 0
  from V86 the processor pushes `GS`, `FS`, `DS`, `ES` first, then
  `SS`, `ESP`, `EFLAGS`, `CS`, `EIP`, and any exception error code. Thus the
  saved EIP is nearest the top of the completed frame. The data segment
  registers are cleared for protected-mode execution; `EnterVirtual8086`
  restores them from the frame on IRET.

## 7. Paging

With `CR0.PG` set, the address that comes out of segmentation is *linear*,
not physical, and one more translation follows: the top ten bits index the
page directory named by `CR3`, the next ten index a page table, the low
twelve are the offset within a 4 KB page. Both levels carry present, writable
and user bits; the effective permission is the stricter of the two.
`TranslatePaged` in `Cpu.Paging.cs` walks the tables, sets the *accessed*
bits on both levels and the *dirty* bit on a written page, and keeps a TLB.
A change to a page-table entry does not automatically invalidate a cached
translation: software reloads CR3 on the 386 (or uses INVLPG on the 486).
The emulator’s `FlushTlb` provides the corresponding invalidation.

Two lessons:

- **A word can straddle two pages that are not adjacent in physical memory.**
  Translating the first byte and reading on is wrong. `ReadSplit` and
  `WriteSplit` in `Cpu.cs` translate each byte, and translate every page
  before writing anything, so that a fault on the second page leaves memory
  untouched. **The bug**: KRNL386 walking the fixup chain of GDI.EXE wrote a
  selector at linear `40FFF`, lost its high byte, followed a link into
  nowhere, and gave up with "Errore nel caricamento del file GDI.EXE".
- **Everything you index by a linear address must be translated**: the GDT,
  LDT, IDT and TSS included. Windows keeps them high in the address space,
  where linear and physical have nothing to do with each other.

## 8. How DOSX and WIN386 use all this

**Standard mode** (`win /s`): DOSX runs at ring 0 with its own GDT, IDT and a
286 TSS; it loads KRNL386 as a DPMI client at ring 3 with IOPL 3, so the
client's `CLI` and `STI` are real. DOS and BIOS calls are made by switching
the processor back to real mode (`MOV CR0` with `PE` cleared, which is legal
on the 386 but not on the 286), running the real interrupt, and switching
back. Hardware interrupts that arrive in protected mode are reflected the
same way. The exception frames DOSX hands to the client are the DPMI ones
(return address, error code, `CS:IP`, flags, `SS:SP`); the kernel's fault
handler at `037F:5EF1` reads them and, for a fault it does not recognise,
opens the "Errore dell'applicazione" box.

**Enhanced mode** (`win`): WIN386 is a full monitor at ring 0 with paging on.
DOS runs in a V86 task; Windows applications run at ring 3 in protected mode
with IOPL 0, so every `CLI` traps and the VMM keeps a virtual interrupt flag
per VM. Ports are governed by the I/O bitmap; the VDD keeps a shadow of the
VGA registers for each VM and restores them -- by writing the registers,
never through `INT 10h` -- when it hands the display back. The DPMI services
(`INT 31h`) and the DOS calls (`INT 21h`) are reached through gates at ring
0, so an `INT n` from ring 3 faults with `#GP` and the VMM does the work.

If you want to see it happen, the harnesses in the project's history do:
boot the installed image, run `win`, and watch `ProcessorException` and
`SoftwareInterruptIssued`. The counts per 300 million instructions are in
the plan (`PIANO.md`, phase 1.4).

## References and scope

The architectural reference is Intel’s 80386 Programmer’s Reference Manual:
[exceptions and restart](https://www.ardent-tool.com/CPU/docs/Intel/386/manuals/prref386/s09_08.htm)
and [virtual-8086 execution](https://www.ardent-tool.com/CPU/docs/Intel/386/manuals/prref386/s15_03.htm).
The addresses above identify measured guest runs, not fixed Windows APIs.
Use `tools/WindowsDosProbe` for the current cold-boot DOS-session regression;
`tools/EmmBootProbe` checks EMM386 installation independently.
