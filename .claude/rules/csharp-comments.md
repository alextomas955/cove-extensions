---
paths:
  - "**/*.cs"
---

# C# comments and XML docs

Comments explain why, not what. Default to no comment, even when surrounding code is more verbose.
Document a constraint once, near the code that enforces it. Preserve non-obvious safety and
compatibility reasoning.

Add a comment only when omitting it would hide a non-obvious constraint needed to change the code
safely. The topics below are candidates, not a requirement to comment:

- A domain rule the code does not show, such as a routing precedence order.
- A non-obvious edge case and its reason.
- An external-system quirk: the Cove ABI, a host API limit, a platform path rule.
- Safety or security reasoning, such as resolving symlinks late to shrink a TOCTOU window.
- A concurrency, performance, or consistency assumption, such as `CoveContext` not being
  thread-safe.
- A temporary workaround, with the condition for removing it.
- A public-API contract the signature cannot show: null behavior, what throws, ordering.

Never write:

- A restatement of a name, or a description of what the next line obviously does.
- Narration of the edit, author voice, or a comparison with code that is no longer there. That
  belongs in the commit message.
- Process or tooling vocabulary: phases, plans, tickets, tasks, agents, or the name of a planning
  tool. Shipped code is tool-agnostic.
- Incidental measurements such as source line numbers or current member counts. Reference the owning
  constant or contract for a timeout, protocol limit, or compatibility version when possible.
- A long decision history. Keep the brief rationale needed to maintain a safety or compatibility
  constraint, including relevant external limits.
- Capitalised emphasis or decorative arrows.

Keep comments as short as the constraint allows. A brief comparison is useful only when it explains
why an apparently reasonable change would be unsafe or incompatible.

Write XML docs (`///`) only on the SDK-facing surface (the `IExtension` boundary, interfaces, shared
contract types). Public visibility alone does not justify documentation; document caller obligations
or behavior the signature cannot express. Skip them on internal
code, tests, and generated code. No `<param>` that restates the parameter name. `<remarks>` explains
why and lists the edge cases. `<exception>` documents what a caller must catch.

## Describe what the code does, not what it should do

A comment stating a requirement and a comment stating a fact read alike and age differently. "The
retention window has to be measured from the earliest batch" is a requirement; rewriting it as "is
measured from" turned it into a false claim about a purge that keys on each batch's own timestamp.

When a comment describes behavior, check the code path before writing it. Where the code does not do
what the comment wants, describe what it does, name the consequence, and report the defect.

## Record types document all constructor parameters or none

`CS1573` is an error here: a record whose doc block carries a `<param>` for some positional
parameters and not others fails the build. Documenting the rest to satisfy it reintroduces the
name-restating tags this file forbids, so put the substance in `<remarks>` and carry no `<param>` at
all.

`CS1591` is silenced on purpose and no doc-enforcement analyzer is installed. Do not add one.

```csharp
// Bad: restates the signature.
/// <summary>Gets the user by id.</summary>
User GetUserById(int id);

// Good: states the contract; remarks explain why.
/// <summary>Resolves <paramref name="candidate"/> to its canonical on-disk path.</summary>
/// <remarks>
/// Resolves symlinks as late as possible to keep the TOCTOU window small. Throws when the target
/// escapes the allowed roots.
/// </remarks>
string ResolveCanonicalPath(string candidate);
```
