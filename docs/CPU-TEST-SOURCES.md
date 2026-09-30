# CPU test sources

Source audit: 10 September 2026.

| Suite | Source of expected results | Local runner |
|---|---|---|
| [SingleStepTests/80386](https://github.com/SingleStepTests/80386) | Intel 80386EX KU80C386EX33, acquired through ArduinoX86 | Real-mode suite |
| [nand2mario/SingleStepTests_80386_protected](https://github.com/nand2mario/SingleStepTests_80386_protected) | 86Box reference emulator | `tools/HardwareSuite` |

The protected-mode runner retains its historical directory name. Its
6100 passing cases demonstrate agreement with those reference vectors,
not independent verification against a physical 486. Earlier project
documentation incorrectly described these vectors as hardware captures.

The [public SingleStepTests repository list](https://github.com/orgs/SingleStepTests/repositories)
and searches for 486 SingleStepTests did not identify a hardware-generated
486 suite as of this audit; hardware-based validation of 486-specific
behaviour remains an open gap.

The real-mode suite was not rerun for this audit; its README result is
historical.
