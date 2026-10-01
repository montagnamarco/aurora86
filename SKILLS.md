# Agent workflow

Shared rules for every agent host. Read once per session with AGENTS.md;
reread only changed sections. User instructions and AGENTS.md take precedence.
Goal: the fewest total tokens per *verified* result, counting context, handoffs
and rework. A cheap unverified result is unfinished work. Never invent token
counts or savings; use real usage telemetry when available.

## Route by difficulty

1. Define outcome, evidence, affected scope and completion check.
2. Known search, edit, build, test or formatter: direct tool, no agent.
3. Otherwise use the cheapest tier that can do it reliably. Raise effort before
   changing model. Escalate one bounded question with its evidence, never the
   whole task, and tell the user why. A sensitive domain, big diff or long test
   suite alone is not a reason. After two attempts without new evidence stop,
   isolate a reproducer and escalate that. Missing credentials, services or
   requirements need the user, not a stronger model.

| Tier | Claude Code (see CLAUDE.md) | Codex | Effort |
| --- | --- | --- | --- |
| Scout, mechanical, repetitive | `claude-haiku-4-5` | `gpt-6-luna` | low |
| Owner, builder, ordinary review | `claude-sonnet-5` | `gpt-5.6-terra` | medium-high |
| Critical review, hard diagnosis | `claude-opus-5-5` | `gpt-6-astra` | xhigh |
| Last escalation, one question | `claude-opus-5-5` | `gpt-6-astra` | max |

Codex IDs were checked on 2026-09-27: the live model list wins, never invent an
ID. `gpt-6-sol` suits a large implementation package with a settled design.
The host picks the primary model at session start; set delegate models
explicitly so they never inherit an expensive primary.

## Delegate only when it pays

- Default to one owner; delegate a bounded, independent task only when local
  work can continue meanwhile. Count setup, context and rework, not model price.
- Contract: outcome, evidence locations, exclusive files, constraints, checks,
  done condition. Fresh minimal context; reference files, do not paste them.
- Roles: scout reads, builder implements, reviewer judges a final diff
  read-only. Delegates never commit, push or spawn delegates. Quality does not
  drop with the tier: every tier follows the same rules.
- Reports: changed files, findings with file:line, check results, open issues,
  next action; 150-250 words.
- Never duplicate a delegate's reads, edits or test runs. Inspect its diff and
  evidence; repeat a check only when stale, incomplete or contradicted.

## Keep context lean

- Start from `git status`, the current state file and the relevant paths. Use
  `rg` and targeted excerpts; batch independent reads; never dump whole logs
  or reread instructions already in context.
- Load AGENTS.md references only for affected work. Note a missing tool once.
- Full logs and screenshots go to files; report counts, errors and paths.
- Multi-commit work keeps one 40-80 line state file in the repository's ignored
  planning folder (`plans/` unless AGENTS.md says otherwise), updated at
  milestones, handoffs or interruptions only.

## Verify, review, commit

- Do the authorized milestone and its dependencies; log unrelated findings,
  do not chase them. Refactor only what the outcome needs.
- Smallest check that proves the behaviour, then affected regressions. Full
  suite only for shared changes, release gates or on request. Never skip
  payment, tenant, fiscal, security or data-integrity checks to save tokens;
  never use mocks as proof of real concurrency.
- Record command, revision, environment and outcome; failures or dependency
  changes invalidate earlier evidence.
- Independent review follows the host adapter (CLAUDE.md for Claude): critical
  invariants get the reasoning tier, other non-trivial diffs the ordinary
  tier. Pass diff, invariants and evidence, not reasoning. If no reviewer is
  available, record the gap; never call self-review independent.
- Done means requested behaviour, meaningful assertions, resolved findings and
  an inspected diff, not an agent's claim or a test count. Never weaken tests
  and never commit a red test.
- Readable code: explicit English names, focused functions, one statement per
  line, logical spacing, comments that explain why. Run the project formatter.
- When AGENTS.md or the user allows committing, commit each finished feature
  or fix with its tests, apart from unrelated edits; short imperative English
  subject, no co-author or generated trailers. Never push or deploy without
  explicit authorization.
- Updates to the user explain findings and decisions, not tool narration.
