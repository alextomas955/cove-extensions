---
paths:
  - "**/*.cs"
  - "**/*.ts"
  - "**/*.tsx"
  - "**/*.mjs"
  - "**/*.cjs"
---

# Comments

Comments explain why, not what. Default to no comment. Match the surrounding density.

Write a comment only for:

- A domain rule the code does not show, such as a routing precedence order.
- A non-obvious edge case, and its reason.
- An external-system quirk: the Cove ABI, a host API limit, a wire casing, a platform path rule.
- Safety or security reasoning, such as resolving symlinks late to shrink a TOCTOU window.
- A concurrency, performance, or consistency assumption, such as `CoveContext` not being
  thread-safe.
- A temporary workaround, with the condition for removing it.
- A contract the signature cannot show: null behavior, what throws, ordering.

Never write:

- A restatement of a name, or a description of what the next line obviously does.
- A step-by-step walkthrough of the code below it. The steps are the code.
- Narration of the edit, author voice, or a comparison with code that is no longer there. That
  belongs in the commit message.
- Process or tooling vocabulary: phases, plans, tickets, tasks, agents, or the name of a planning
  tool. Shipped code is tool-agnostic.
- A measurement: a line number, count, version, date, hash, or timing. It goes stale with no
  signal. Cover it with a test, or state the durable form ("the rollback catch", not "the catch at
  :153").
- The argument for a decision. State the constraint and stop.
- A comparison with an alternative the code does not take ("rather than", "instead of").
- Capitalised emphasis or arrows.

## Length

State a point once, in the place that owns it. An invariant restated on every method that relies on
it belongs on the type, or on the field that enforces it.

A file or class header says why the type exists and what its safety spine is. A numbered walkthrough
of the code below it is always too long.

A long block is earned in three places and nowhere else: a boundary interface's contract, a settings
record's per-setting semantics, and a safety invariant whose loss corrupts or loses a user's file.
Everywhere else, one summary sentence and at most one short paragraph.

## Describe what the code does, not what it should do

A comment stating a requirement and a comment stating a fact read alike and age differently. "The
retention window has to be measured from the earliest batch" is a requirement; rewriting it as "is
measured from" turned it into a false claim about a purge that keys on each batch's own timestamp.

When a comment describes behavior, check the code path before writing it. Where the code does not do
what the comment wants, describe what it does, name the consequence, and report the defect.
