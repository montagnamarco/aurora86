# Contributing

Aurora86 is an emulator that is meant to be read, so the way a change is
made matters as much as the change. These are the working rules the code
was written under; they are what kept the guests running.

## Measure, do not guess

A guest that misbehaves is a guest that observed something. Find out what
it observed before changing anything: which BIOS or DOS function it called,
with which registers and which bytes in memory; which port it read; who
wrote the byte that is wrong. Ten times over in this project's history the
first hypothesis was wrong and the trace was right -- the volume label that
came out as a file name was a wrong SDA offset, not a wrong search; the
"path not found" on a shared folder was three unrelated defects, none of
them in the search.

The machine has the instruments for this, and they cost nothing when
unused:

- `Machine.Cpu.SoftwareInterruptIssued`: every INT, with the registers.
- `Machine.Io.PortAccessed`: every byte read or written on a port.
- `Machine.Memory.SetWatchRanges` and `Watched`: who writes a byte, with
  the CPU's CS:IP at that moment.
- `Machine.Cpu.ProcessorException`: every fault, with its vector.
- `Redirector.Trace` and `Redirector.TraceResult`: every redirector call
  and how it was answered.
- `DosCallLog` and `DiskWriteLog`: the DOS calls that move files, and the
  sectors the guest writes.
- `MachineSnapshot`: freeze the machine at the prompt once, restore it in a
  second for every experiment after.
- `Cpu.PeekLinear`: read memory through the page tables without disturbing
  anything, which is what a guest in enhanced mode needs.

A bench is a small console project that boots an image, types a command
through the keyboard controller, and prints the screen. Keep the keyboard
layout in mind: an image that loads `KEYB IT` puts the colon on Shift+0x34,
and `dir e:` typed with the US scancodes becomes `dir e>`.

## Fix the defect, not the symptom

A special case that makes one program work usually hides a rule that makes
ten programs work. When a guest does something unexpected, the question is
what the hardware or DOS would have done, and the answer is in a
specification, in a reference emulator's behaviour, or in a measurement --
never in what seems reasonable. The DOS redirector protocol, the ISA Plug
and Play key, the NE2000's word mode: all of them differ from the sensible
guess, and the comments say where and why.

## Tests are the specification

Every behaviour the guest depends on has a unit test that states it, in
the words a driver or DOS would use: which register, which offset, which
bit. A test that fails should say which number is wrong. Device tests drive
the ports; firmware tests run an `INT` through the real CPU with the
registers set up; protocol tests stand the DOS structures up by hand rather
than booting DOS, because what a live DOS keeps in its SDA depends on what
it was doing at the time.

Run the suite with the slow test left out:

```
dotnet test tests/Aurora86.Tests --filter "Speed!=Slow"
```

After any change to the CPU in protected mode, run the hardware vectors:

```
dotnet run --project tools/HardwareSuite -c Release -- path/to/v1
```

and expect 6100 of 6100.

## Comments explain why

Each class opens with what the chip or the service is, when it dates from,
and how software of the period drives it. Inline comments are for the
decisions a reader could not reconstruct: why a bit is set, why an order of
operations matters, what went wrong when it was otherwise. A comment that
restates the code is noise; a comment that says "this looked wrong and is
not, here is the trace" is the most valuable line in the file.

## Commits

One change per commit, described by what the machine does differently and
why, in the first person. The message says what was measured. If a change
turns out to be wrong later, the history should show what it was believed
to fix.
