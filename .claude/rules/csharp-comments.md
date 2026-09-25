---
paths:
  - "**/*.cs"
---

# XML docs

`.claude/rules/comments.md` carries the comment rules. This file adds the `///` ones.

Write XML docs where a caller cannot read the implementation: the `IExtension` boundary, the shared
package, and wire contract types. An internal interface whose one implementation sits beside it is
not that boundary; comment the implementation instead. Skip docs on tests and generated code.
Write a tag only where it states something the signature cannot. No `<param>` that restates a
parameter name.

A record documents all its positional parameters or none. `CS1573` is an error here, so a partial
set fails the build, and completing the set reintroduces name-restating tags. Put the substance in
`<remarks>` and carry no `<param>` at all.

`CS1591` is silenced on purpose and no doc-enforcement analyzer is installed. Do not add one.

```csharp
// Bad: restates the signature.
/// <summary>Gets the user by id.</summary>
User GetUserById(int id);

// Good: states the contract; remarks give the reason.
/// <summary>Resolves <paramref name="candidate"/> to its canonical on-disk path.</summary>
/// <remarks>Resolves symlinks late to keep the TOCTOU window small. Throws when the target escapes
/// the allowed roots.</remarks>
string ResolveCanonicalPath(string candidate);
```
