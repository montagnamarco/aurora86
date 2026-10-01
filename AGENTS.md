# Aurora86 agent instructions

Shared workflow: [SKILLS.md](SKILLS.md), read once per session. Claude Code
routing: [CLAUDE.md](CLAUDE.md). These repository rules take precedence. Load
the references below only when the task needs them.

Aurora86 is an x86 PC emulator in C#, from the 80386 to the Pentium 4. MS-DOS
6.22, Windows 3.11 and Windows 95 are established guests; a modern Linux is a
goal, never a reason to special-case hardware without evidence. Diagnosis
dominates here: the reasoning tier suits investigation sessions, while
implementation from a measured plan goes to the builder tier.

## Boundaries (every tier; delegation grants nothing)

- `images/` and disk images are state, not source: never commit them, never
  open them with read tools. Back an image up before modifying it
  (`hdd.img.prima-di-<what>`); run benches on a copy, each concurrent
  experiment on its own copy and output path. Keep CRLF in CONFIG.SYS and
  AUTOEXEC.BAT (bare LF makes DOS reject driver lines).
- Never change dependencies, guest configuration or disk contents to hide an
  emulator defect. Never push without explicit authorization.
- Commit each feature or fix on its own once its checks are green (fast
  suite, covering vectors, DOS boot where it applies) and any prescribed
  review is addressed. Stage only that change, check that it builds and
  passes in a clean worktree, and commit on a feature branch, never directly
  on `master`. Preserve unrelated edits.

## Critical invariants (diff touching them: critical-reviewer before commit)

CPU semantics in protected, V86 or long mode; paging and TLB invalidation;
exception delivery, fault priority and the state left by a faulting
instruction; privilege and limit checks; operand/address size; flags
(including the undefined ones the vectors fix); interrupt and DMA timing;
guest-visible PCI configuration; the snapshot format and determinism; guest
disk integrity.

## Method: measure, do not guess

- Find out what the guest observed (which call, which arguments, who wrote
  the wrong value) before changing anything. Diagnose from specifications,
  traces and a reproducer; one guest appearing to work proves nothing.
- Never report an unmeasured cause or present a hypothesis as a finding; say
  plainly when a measurement contradicts something said earlier.
- Fix the rule, not the symptom: the answer is in a datasheet, a reference
  emulator or a measurement. Never delegate an unmeasured hypothesis downward.
- Read guest state without altering it: use the trace hooks and `Cpu.PeekLinear`.
  Write each measurement down once (plan, commit body or code comment).

## Code and language

- Repository code, comments, docs and commits in English; only
  `docs/ARCHITETTURA.md` and the ignored plans are in Italian.
- net10.0, Nullable enabled, LangVersion latest, no `.editorconfig`: match the
  surrounding file. Readability over brevity; never compress to save tokens.
- Every class opens with an XML summary: which chip or service, its period, how
  period software drives it. Ports, register indices, offsets and bits get a
  named constant or a comment naming them.

## Delegation seams

Delegate one device, one instruction group, one firmware service, one
specification question (`hardware-researcher`) or one review. Keep with the
owner, serialized: decoder and dispatch tables, `Cpu` state layout, memory bus,
device and PCI registration contracts, snapshot format, `Machine` composition.

## Checks

- Fast suite at integration boundaries:
  `dotnet test tests/Aurora86.Tests --filter "Speed!=Slow"`; narrow it while
  implementing. Build with `dotnet build Aurora86.slnx`. Instruction-only
  edits need reference checks, not a guest boot.
- After any protected-mode CPU change: `dotnet run --project tools/HardwareSuite
  -c Release -- path/to/v1`, expecting 6100/6100. Without vectors, disclose the
  missing check; never claim a pass.
- Every guest-visible behaviour has a test naming register, offset or bit:
  device tests drive ports, firmware tests run `INT` through the real CPU,
  protocol tests build DOS structures by hand.
- Benches in Release, from a `MachineSnapshot` taken at the DOS prompt; recreate
  it when emulator state, schema, image or configuration changes.
- Traps: `KEYB IT` puts the colon on Shift+0x34; after an `INT` the stacked
  flags sit at SP+4; `VgaRenderer.ToRgba` is `0xAABBGGRR` (red in the low byte).

## Plans and commits

- Plans live in the ignored `plans/` folder, each with a 40-80 line
  `-state.md` validated against `git status` before use. Locate or add the
  plan item before changing source; tick it only with green checks and the
  commit hash.
- One change per commit; capitalized imperative English subject under 72
  characters, no type prefix, no attribution trailers; a body of one or two
  sentences saying what changed and, when it matters, what was measured or
  remains unverified (e.g. `Boot from a CD through El Torito`).

## Reference map

| When needed | Read |
| --- | --- |
| C# layout, names, comments in detail | [Code style](.agents/code-style.md) |
| Trace hooks, benches, tests, layout, disk images, FatTool | [Emulator reference](.agents/emulator-reference.md) |
| Task contracts, handoff, evidence | [Workflow templates](.agents/workflow.md) |
| Claude delegation troubleshooting | [Claude adapter](.agents/claude.md) |
| One chip or subsystem | `docs/<CHIP>.md` (PCI, VGA, VIRGE, USB, X87, ...) |
