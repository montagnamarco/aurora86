# Agent code style reference

Read the relevant language sections before editing source. Repository-specific
C# conventions in AGENTS.md take precedence.

## Code quality

Optimise for the reader, never for brevity, and never for the number of lines
saved. Code is read many more times than it is written.

### Layout and spacing

- **One statement per line.** Never put several statements on one line, and
  never put a block's body on the same line as its `if`, `for` or `while`.
  `if (busy) { Wait(); return; }` is not acceptable; write it on four lines.
- **Separate logical blocks with a blank line.** Setup, the loop, the result;
  guard clauses, then the main path. A method body that reads as a wall of
  text has not been laid out.
- **Indent consistently**, with the indentation unit the language's
  convention prescribes, never tabs where the convention says spaces, and
  matching the surrounding file. Continuation lines of a long expression are
  indented one level past the line they continue.
- **Break long expressions and argument lists** across lines, one argument
  or one operand per line when they no longer fit comfortably in about 100
  columns. Do not fold them into a dense single line to save space.
- **No crammed or "inline" code.** Do not chain calls into one long
  expression when named intermediate locals would say what each step is; do
  not nest lambdas inside argument lists; do not write a `switch` expression
  whose arms carry logic. If a piece of code needs a comment to be parsed, it
  needs to be spread out first.
- **Order the members of a type** consistently: constants, fields,
  constructor, properties, public methods, then private helpers, with related
  members kept together and section comments where a type has several
  concerns.
- **Order within a method** follows the reader's need: validate, prepare,
  act, report. Declare a local close to its first use.

### Names and constructs

- **Expressive, explicit names. No single-letter abbreviations.** Write
  `sourceAddress`, not `sa`; `commandRegister`, not `cr`. The one exception
  is the index of a genuinely trivial loop.
- **No complex one-liners, no nested ternaries.** Break the logic into named
  locals and ordinary `if` statements. A ternary is acceptable only when both
  branches are short values with no side effects.
- **No magic numbers in logic.** A constant gets a name, or a comment saying
  what the number means.
- **Small functions with one job.** A function that needs a paragraph to
  describe is two functions.

### Conventions of each language

Follow the standard style of the language, not a personal one.

- **C#**: the .NET conventions — `PascalCase` for types, methods, properties
  and constants; `camelCase` for locals and parameters; `_camelCase` for
  private fields; braces on their own line; `var` only when the type is
  obvious from the right-hand side; expression-bodied members only for
  one-line accessors.
- **Python**: PEP 8 — four-space indentation, `snake_case`, a docstring on
  every module and function, no semicolons, no several statements per line.
- **XAML and other XML dialects**: one attribute per line when an element
  has more than two, children indented under their parent.
- **Shell**: `set -euo pipefail`, quoted variables, one command per line.
- **Any other language**: its official style guide or the de-facto standard
  tool (`gofmt`, `rustfmt`, `black`, `clang-format`); when the project ships
  a formatter or linter configuration, that configuration wins.

### Comments explain why, not what

Every part of the code that a reader would stop at deserves a comment, and the
comment says why, not what.

- Each type opens with a documentation comment saying what it is for and how
  its callers use it.
- Each non-trivial function has a documentation comment saying what it does
  for its caller.
- Inside a function, a short comment introduces each logical block: what this
  step establishes and why it comes here. The decisions a reader could not
  reconstruct get a fuller note: why a value is set, why an order of
  operations matters, what went wrong when it was done otherwise.
- A comment that restates the code is noise. Delete it.
- Write comments in prose, in full sentences.

