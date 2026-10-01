# Claude entry point

@AGENTS.md
@SKILLS.md

AGENTS.md holds this repository's boundaries and critical invariants; SKILLS.md
the shared workflow. Load the references listed in AGENTS.md only when needed.

## Claude Code routing

The host fixes the session model at start; this file cannot change it. Prefer
`claude-sonnet-5` as owner of new sessions and choose `claude-opus-5-5` only for
a session dominated by design or hard diagnosis. An Opus owner still hands
routine implementation to `task-builder`. Route delegated work by difficulty:

| Work | Agent | Model | Effort |
| --- | --- | --- | --- |
| Locate evidence, bounded reading, repetitive low-risk edits | `task-scout` | `claude-haiku-4-5` | low |
| Bounded change, known requirements, exclusive files | `task-builder` | `claude-sonnet-5` | high |
| Independent review of an ordinary non-trivial diff | `code-reviewer` | `claude-sonnet-5` | high |
| Independent review of a critical diff; hard bounded diagnosis | `critical-reviewer` | `claude-opus-5-5` | xhigh |
| One high-consequence question critical-reviewer left open | `frontier-reasoner` | `claude-opus-5-5` | max |
| Register-level spec of one chip or instruction group | `hardware-researcher` | `claude-sonnet-5` | medium |

Model, effort and turn limits are pinned in `.claude/agents/`: always delegate
by agent name so no delegate inherits an expensive owner. The agents start
without CLAUDE.md and read at most AGENTS.md, so the contract must name the
files, invariants and checks.
Use exact IDs; if one does not resolve, disclose the fallback once.

## Escalation ladder

1. Direct tool first: known search, build, test or formatter needs no agent.
2. Cheapest adequate tier; raise effort before changing model.
3. Escalate one bounded question with its evidence, never the conversation.
   Tell the user why before involving Opus. `frontier-reasoner` only after a
   `critical-reviewer` attempt left the same question open.
4. Return implementation and verification to Sonnet, Haiku or direct tools.

## Independent review (mandatory)

- Diff touching a critical invariant in AGENTS.md: `critical-reviewer`.
- Other non-trivial diff (logic change, several files, public contract):
  `code-reviewer`.
- Trivial diff (text, formatting, comments, obvious one-liner): none.

Save the final diff where Git never commits it, `git diff > .git/review-<topic>.diff`
(outside Git, list the changed files), and pass its path with the invariants
and test evidence, never the author's reasoning. Fix the findings, then
re-review only the changed hunks. Self-review is never independent review.

## Delegation contract

At most two active delegates, on disjoint files. Pass outcome, evidence
locations, exclusive files, constraints, checks and done condition. Delegates
never commit, push or spawn agents. Keep with the owner (never the pinned
builder) changes to a critical invariant or to the shared spine named in
AGENTS.md, and any change whose contract would take longer than the change.
Never spawn an agent to run a command or summarize its output. Use fresh-context
subagents, not forks; built-in Explore/Plan agents know no project rules.
