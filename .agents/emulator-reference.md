# Emulator diagnostics and repository reference

Read only the sections needed for the affected subsystem or experiment.
Hardware test requirements and image safety also remain in the compact root
instructions. Historical findings below are evidence, not guest-specific hacks.

## The instruments

They cost nothing when unused, and they are how "measure, do not guess" is
done in this codebase:

| Hook | What it gives you |
|---|---|
| `Machine.Cpu.SoftwareInterruptIssued` | every INT, with the registers |
| `Machine.Cpu.ProcessorException` | every fault, with its vector |
| `Machine.Cpu.UnknownOpcode`, `UnknownTwoByteOpcode` | instructions not implemented |
| `Machine.Io.PortAccessed` | every byte read or written on a port |
| `Machine.Memory.SetWatchRanges` + `Watched` | who writes a byte, with CS:IP |
| `Machine.Memory.SetReadWatchRanges` + `ReadWatched` | who reads a byte |
| `Redirector.Trace`, `Redirector.TraceResult` | every redirector call and its answer |
| `DosCallLog`, `DiskWriteLog` | DOS calls that move files; sectors written |
| `MachineSnapshot` | freeze at the prompt once, restore in a second afterwards |
| `Cpu.PeekLinear` | read through the page tables without disturbing anything |

Lessons this project paid for, so nobody pays twice:

- The DOS redirector protocol, the ISA Plug and Play key and the NE2000's
  word mode all differ from the sensible guess; the comments say where and
  why.
- The volume label that came out as a file name was a wrong SDA offset, not
  a wrong search. The "path not found" on a shared folder was three
  unrelated defects, none of them in the search.

## Benches

A bench is a small console project that boots an image, types a command through
the keyboard controller, and prints the screen. `tools/` holds the ones worth
keeping (`EmmBootProbe`, `S3WindowsProbe`, `WindowsDosProbe`,
`Win95SetupProbe`); throwaway ones belong in the scratchpad directory.

Three traps that have cost real time:

- **Keyboard layout.** An image that loads `KEYB IT` puts the colon on
  Shift+0x34. `dir e:` typed with US scancodes becomes `dir e>`.
  `Win95SetupProbe` keeps Italian punctuation by default; set
  `AURORA_KEYBOARD_LAYOUT=us` when the guest uses a US keyboard. Its `text`
  and `type` commands reject unsupported characters before injecting any
  key, preserving the running guest and any unsaved checkpoint.
- **Reading the carry flag after an INT.** The stacked flags sit at **SP+4**, not
  below SP. Reading the wrong place makes a failed DOS call look like a
  successful one.
- **The framebuffer byte order.** `VgaRenderer.ToRgba` puts red in the low
  byte (`0xAABBGGRR`). A screenshot writer that assumes the opposite swaps
  red and blue and makes a teal desktop look olive.

Always take a `MachineSnapshot` at the DOS prompt and restore it, rather than
booting from cold for every experiment. Build benches in `Release`. Run them on
a **copy** of a disk image: an interrupted run leaves the filesystem dirty and
the next boot starts ScanDisk.

## Tests

Every behaviour a guest depends on has a unit test stating it in the words a
driver or DOS would use: which register, which offset, which bit.

- Device tests drive the ports.
- Firmware tests run an `INT` through the real CPU with the registers set up.
- Protocol tests stand the DOS structures up by hand rather than booting DOS,
  because what a live DOS keeps in its SDA depends on what it was doing.

Run the suite with the slow test left out:

```
dotnet test tests/Aurora86.Tests --filter "Speed!=Slow"
```

After any change to the CPU in protected mode, run the hardware vectors and
expect 6100 of 6100:

```
dotnet run --project tools/HardwareSuite -c Release -- path/to/v1
```

## Repository layout

```
src/Aurora86.Core/      the machine: CPU, memory, devices, firmware, DOS
src/Aurora86.Gui/       Avalonia front end
src/Aurora86.Sdl/       SDL front end
src/Aurora86.FatTool/   read and write files inside a disk image
tests/Aurora86.Tests/   the specification
tools/                   probes and the hardware test runner
docs/                    architecture and per-subsystem notes
plans/                   working plans — ignored by git, never committed
images/                  disk images — ignored by git, never committed
AGENTS.md, SKILLS.md     rules for agents working on the repository
CLAUDE.md                Claude Code routing; .claude/agents holds the profiles
.agents/                 references AGENTS.md loads on demand
```

Build the whole solution with `dotnet build Aurora86.slnx`.

## Disk images

`images/` holds real, large disk images, most of them copyrighted software.
They are state, not source, and the whole directory is ignored by git.

- **Back one up before modifying it**, using the existing naming convention:
  `hdd.img.prima-di-<what-you-are-about-to-do>`.
- `CONFIG.SYS` and `AUTOEXEC.BAT` need **CRLF** line endings. Writing them with
  bare LF makes DOS report "invalid parameter" on driver lines.
- Use `src/Aurora86.FatTool` to get and put files:
  `dotnet run --project src/Aurora86.FatTool -c Release -- get images/hdd.img CONFIG.SYS /tmp/out.txt`
  and `... -- put images/hdd.img /tmp/new.txt CONFIG.SYS` (local path first,
  name inside the image second).

