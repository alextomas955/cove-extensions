---
paths:
  - "**/*.{ts,tsx,mjs,cjs}"
---

# TypeScript and React comments

`.claude/rules/comments.md` carries the comment rules. This file adds what is specific to the UI.

Host-contract quirks are worth a comment because the code cannot show them. A Cove UI slot passes
its context as top-level props (`props.studio`), not `props.context.*`. `OverrideComponent` and
`actionType: "context-menu"` do nothing and report nothing. The video detail-rail tab icon is drawn
by the host.

So is a wire-format fact: a PascalCase field that must match a C# options record, or an enum casing
the server emits.

Write JSDoc only on the public surface: the `defineExtension` entry, exported slot and tab
components, `*Logic.ts` contracts. None on tests or internal helpers. XML tags (`<summary>`,
`<remarks>`) are C# only and render as literal text. Use prose plus `@param` and `@returns`.

No doc-presence lint exists on the TypeScript side. Do not add one.
