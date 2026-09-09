---
paths:
  - "**/*.{ts,tsx,mjs,cjs}"
---

# TypeScript and React comments

Comments explain why, not what. Default to no comment, even when surrounding code is more verbose.
Document a constraint once, near the code that enforces it. Preserve non-obvious safety and
compatibility reasoning.

Add a comment only when omitting it would hide a non-obvious constraint needed to change the code
safely. The topics below are candidates, not a requirement to comment:

- A host-contract quirk the code cannot show. A Cove UI slot passes its context as top-level props
  (`props.studio`), not `props.context.*`. `OverrideComponent` and `actionType: "context-menu"` do
  nothing and report nothing. The video detail-rail tab icon is drawn by the host.
- A wire-format fact: a PascalCase field that must match a C# options record, or an enum casing the
  server emits.
- Non-obvious UI reasoning: why a fetch is deduped through a store, why a popover renders through a
  portal, why a control is disabled.
- An invariant whose reason cannot be expressed clearly by the code or signature.

Never write:

- A restatement of a name, or narration of obvious JSX or hooks.
- Edit narration, author voice, or a comparison with code that is no longer there.
- Process or tooling vocabulary: phases, tickets, agents, or the name of a planning tool.
- Incidental measurements or a long decision history. Keep a brief rationale or an external limit
  when it is needed to maintain the code; reference the owning constant or contract when possible.

Keep comments as short as the constraint allows. A brief comparison is useful only when it explains
why an apparently reasonable change would be unsafe or incompatible.

Write JSDoc only on the public surface (the `defineExtension` entry, exported slot and tab
components, `*Logic.ts` contracts). Exported visibility alone does not justify documentation;
document caller obligations or behavior the signature cannot express. None on
tests or internal helpers. XML tags (`<summary>`, `<remarks>`) are C# only and render as literal
text in JSDoc. Use prose plus `@param` and `@returns`.

No doc-presence lint exists on the TypeScript side. Do not add one.
