---
paths:
  - "**/*.{ts,tsx,mjs,cjs}"
---

# TypeScript and React comments

Comments explain why, not what. Default to no comment. Match the surrounding comment density.

Write a comment only for:

- A host-contract quirk the code cannot show. A Cove UI slot passes its context as top-level props
  (`props.studio`), not `props.context.*`. `OverrideComponent` and `actionType: "context-menu"` do
  nothing and report nothing. The video detail-rail tab icon is drawn by the host.
- A wire-format fact: a PascalCase field that must match a C# options record, or an enum casing the
  server emits.
- Non-obvious UI reasoning: why a fetch is deduped through a store, why a popover renders through a
  portal, why a control is disabled.
- The invariant a `*Logic.ts` module exists to hold.

Never write:

- A restatement of a name, or narration of obvious JSX or hooks.
- Edit narration, author voice, or a comparison with code that is no longer there.
- Process or tooling vocabulary: GSD, phases, tickets, agents.
- A measurement, or the argument for a decision.

Write JSDoc only on the public surface (the `defineExtension` entry, exported slot and tab
components, `*Logic.ts` contracts), and only when it states what the signature cannot. None on
tests or internal helpers. XML tags (`<summary>`, `<remarks>`) are C# only and render as literal
text in JSDoc. Use prose plus `@param` and `@returns`.

No doc-presence lint exists on the TypeScript side. Do not add one.
