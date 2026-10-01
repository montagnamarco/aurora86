# Agent task, review and handoff contracts

Read only the template needed. SKILLS.md owns the operating policy; these
contracts do not require a new agent or document for each command.

## Routing examples

| Situation | Path | Escalation trigger |
| --- | --- | --- |
| Find a port handler, run a known test/build | Direct tool | A new unresolved question appears |
| Mechanical edit or proven local fix | Scout tier, or the owner if handoff costs more | Behavior or scope becomes unclear |
| Bounded device implementation with a specification | Builder tier | Cross-device uncertainty or failed diagnosis |
| Cross-module guest diagnosis or focused critical review | Reasoning tier | Unresolved architecture or correctness risk |
| Subtle protected-mode fault ordering or disk corruption | Reasoning tier at maximum effort, on the critical question | Return to ordinary implementation after diagnosis |
| Missing image, vector set or tool | Resolve the dependency | A stronger model cannot supply missing evidence |

Use actual exposed runtimes, not invented aliases. Model choice is not a
mandatory sequence of attempts. Roles are assignments, not resident agents.
A protected-mode test command is still a tool operation; designing an unknown
fault-ordering fix may require a stronger investigator.

## Task / delegation

```text
Outcome: observable result and completion criterion.
Role / runtime / reasoning: explicit choice and short reason.
Evidence: measured symptom, trace/specification/test locations.
Ownership: exclusive writable files; relevant read-only dependencies.
Constraints: task-specific invariants beyond AGENTS.md and SKILLS.md.
Checks: commands, isolated image copies, snapshot identity, existing evidence.
Deliver: diff, findings, check counts/artifacts, unresolved issues; no scope expansion.
```

Aim for 200–400 words in a brief and 150–250 in a report when sufficient.
Do not omit hardware invariants to meet a word limit. A direct action usually
needs only its intended result and verification, not the entire template.

## Quality paths

- Simple: owner → edit → relevant check → commit.
- Ordinary: implementation and tests together; coordinator reviews the actual diff.
- Critical: diagnosis if needed → implementation → independent read-only review
  while the coordinator checks integration → owner fixes findings and reruns
  invalidated checks → commit.
- Blocked: identify missing evidence or dependency rather than adding agents.

No permanent commit agent. Refactoring needs a separate owner only for a
substantial, explicitly scoped change. A tester agent is useful for designing
new experiments; running an existing bench does not require another model.
Delegates do not create recursive teams. Review findings go back to the owner.

## Reviewer contract

```text
Read-only scope: final diff, baseline and necessary dependent files.
Invariants: instruction/device behavior, fault ordering, data preservation.
Evidence: existing commands, results, vectors and artifacts.
Deliver: concrete defects with file/line, consequence and proof; or no defects
         found, stating what was inspected and what remains unverified.
```

Review correctness and missing coverage, not stylistic taste. Inspect real
assertions and outcomes; a green summary or large test count is not proof of
an invariant. Never weaken tests to make a change pass.

## Resume state: plans/<initiative>-state.md

```text
Active outcome and completion criterion:
Detailed plan and current items:
Branch / checked commit / dirty files:
Relevant decisions and invariants:
First files to read:
Valid checks: command, source revision, environment, result, artifact.
Open work: owner, files and next required action.
Active resources: processes, writable image copies, snapshots, agents.
Known limits: what remains unverified or incomplete.
```

Normally 40–80 lines. Update at a milestone, handoff or interruption; do not
rewrite the full historical plan after each command. Check git status and
source revision on resume. Preserve history separately and consult as needed.
Never put secrets or full traces in the state file.

## Evidence reuse and evaluation

Record command, revision/dirty scope, runtime, guest image/snapshot provenance,
result and artifact. Avoid copying large disk images just to record evidence:
use known fixture identity and record mutations to the experimental copy.
Invalidate checks when relevant code, image, snapshot schema or configuration
changes. Preserve full traces in artifacts and report only the decisive excerpt.
A successful DOS boot does not prove protected-mode vector correctness.
An instruction unit test does not prove a complete Windows installation.

Evaluate completed results, delegation count, repeated work and corrections.
Record actual token usage only when exposed by the runtime; do not invent a
saving from model names. A cheaper model requiring repeated repairs may cost
more overall. Keep quality gates while reducing coordination and context waste.
