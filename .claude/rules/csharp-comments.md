---
paths:
  - "**/*.cs"
---

# C# comments and XML docs

Comments explain why, not what. Default to no comment. Match the surrounding comment density.

Write a comment only for:

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
- Process or tooling vocabulary: GSD, phases, plans, tickets, tasks, agents. Shipped code is
  tool-agnostic.
- A measurement: a line number, count, version, date, hash, or timing. It goes stale with no
  signal. Cover it with a test, or state the durable form ("the rollback catch", not "the catch at
  :153").
- The argument for a decision. State the constraint and stop.

Write XML docs (`///`) only on the SDK-facing surface (the `IExtension` boundary, interfaces, shared
contract types), and only where a tag states something the signature cannot. Skip them on internal
code, tests, and generated code. No `<param>` that restates the parameter name. `<remarks>` explains
why and lists the edge cases. `<exception>` documents what a caller must catch.

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
