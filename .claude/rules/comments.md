---
paths:
  - "**/*.cs"
  - "**/*.ts"
  - "**/*.tsx"
  - "**/*.mjs"
  - "**/*.cjs"
---

# Comments

A comment earns its place by carrying what the code cannot: a constraint, an external fact, a
reason a safe-looking change is unsafe. Before writing one, ask what a reader would get wrong
without it. If nothing, delete it.

Default to none. Most code here needs no comment.

## Worth a comment

- A domain rule the code does not show, such as a routing precedence order.
- A non-obvious edge case, and why it is handled that way.
- An external-system quirk: the Cove ABI, a host API limit, a wire casing, a platform path rule.
- Safety or security reasoning, such as resolving symlinks late to shrink a TOCTOU window.
- A concurrency, performance or consistency assumption, such as `CoveContext` not being thread-safe.
- A contract the signature cannot show: null behavior, what throws, ordering.
- A temporary workaround, with the condition for removing it.

## Slop

Slop is prose written to look thorough. The tell: it would still be true, and still read fine,
above different code. It describes the shape of the code instead of the constraint on it.

- Restating a name, or describing what the next line plainly does.
- A walkthrough of the code below it. The steps are the code.
- Narrating the edit, or comparing with code that is no longer there. That belongs in the commit
  message.
- A comment on every member of a group because the first one needed one.
- Explaining the language or the framework.
- The argument for a decision, written out. State the constraint and stop.

## Never

- A measurement: a line number, count, version, date, hash or timing. It goes stale with no signal.
  State the durable form ("the rollback catch", not "the catch at :153"), or cover it with a test.
  A limit a reader must know is an external fact, not a measurement: keep that.
- Process or tooling vocabulary: phases, plans, tickets, tasks, agents, or the name of a planning
  tool. Shipped code is tool-agnostic.
- Capitalised emphasis or arrows.

## Comparisons

A comparison with an alternative is worth the words only where the alternative looks safe and is
not. "Compared over fixed-width digests, so neither the comparison time nor a length check tells a
caller how much of a guess was right" stops someone simplifying to `==`. "A typed client rather
than a constructed HttpClient, so the handler is pooled" narrates a decision nobody was going to
undo. Keep the first kind. Cut the second.

## Length

State a point once, in the place that owns it. An invariant restated on every method that relies on
it belongs on the type, or on the field that enforces it.

A file or class header says why the type exists and what its safety spine is, in a sentence or two.
A numbered walkthrough is always too long.

A long block is earned in three places: a boundary interface's contract, a settings record's
per-setting semantics, and a safety invariant whose loss corrupts or loses a user's file. Everywhere
else, one summary sentence and at most one short paragraph.

Existing code may be denser than this asks. Bring a file to these rules when you change it. Do not
sweep files you are not otherwise touching.

## Describe what the code does, not what it should do

A comment stating a requirement and a comment stating a fact read alike and age differently. "The
retention window has to be measured from the earliest batch" is a requirement; rewriting it as "is
measured from" turned it into a false claim about a purge that keys on each batch's own timestamp.

When a comment describes behavior, check the code path before writing it. Where the code does not
do what the comment wants, describe what it does, name the consequence, and report the defect.
