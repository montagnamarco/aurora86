# Claude Code adapter

The shared rules are in [SKILLS.md](../SKILLS.md) and the short routing table
in [CLAUDE.md](../CLAUDE.md). This note is only for configuring or
troubleshooting delegation; it does not need reading every session.

## Profiles

| Profile | Model | Effort | Tools | When |
| --- | --- | --- | --- | --- |
| `task-scout` | `claude-haiku-4-5` | low | read | locate evidence, bounded reading, repetitive work |
| `task-builder` | `claude-sonnet-5` | high | read, edit, Bash | bounded change with exclusive files |
| `code-reviewer` | `claude-sonnet-5` | high | read | independent review of an ordinary non-trivial diff |
| `critical-reviewer` | `claude-opus-5-5` | xhigh | read | review of a diff touching a critical invariant; hard diagnosis |
| `frontier-reasoner` | `claude-opus-5-5` | max | read | one question left open after `critical-reviewer` |
| `hardware-researcher` | `claude-sonnet-5` | medium | read, web | register-level specification of one chip or instruction group |

Model, effort and `maxTurns` are fixed in the frontmatter under
`.claude/agents/`, so a delegate never inherits the owner's more expensive
model. `frontier-reasoner` is not a stronger model: it adds maximum effort, a
fresh context and one narrowed question. The builder stays at high effort: a
change to a critical invariant is not delegated to it; the owner makes it and
sends it to `critical-reviewer`.

## Why it stays cheap

- Every profile sets `omitClaudeMd: true`: it starts without CLAUDE.md and its
  imports (SKILLS.md) and reads only AGENTS.md. The scout does not even read
  that: it gets the paths in its contract. The contract must therefore name
  the files, the invariants and the checks.
- Effort is the first lever: raise it before changing model.
- `maxTurns` stops explorations that do not converge; a partial result can be
  resumed instead of restarted.
- Reviewers receive the diff saved to a file and the invariants, never the
  author's reasoning: that is what makes them independent.

## Start and verification

Restart the Claude Code session to load new or changed profiles. The owner
model is the user's choice: `claude --model claude-sonnet-5` for an ordinary
session, `/model` to change it. Never change global preferences automatically.

On the first delegation of a session, check the effective model and tools. If
an ID is not available to the account, state the fallback once. Local checks
verify the profiles' format and references, not model access or token
savings: those are measured on real work.
[Subagent documentation](https://code.claude.com/docs/en/sub-agents).
