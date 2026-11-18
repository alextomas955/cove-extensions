---
paths:
  - "**/*.ts"
  - "**/*.tsx"
---

# TypeScript and React

`.claude/rules/typescript-comments.md` carries the comment rules. This file carries the shape rules.

## Check the host, then a served library, then write it

`@cove/runtime/components` is the host's own component barrel. It carries confirm and edit dialogs,
form fields, list pages, pagers, an infinite-scroll sentinel, filter and bulk-edit dialogs, entity
cards and grids, virtualized layouts, multi-select, list URL state and document title. Read it
before writing a component or a hook. Where a host export exists and does not fit, name it and say
why in the code that replaces it.

The host's form fields are the one measured exception. `Field`, `TextInput`, `TextArea`,
`NumberInput`, `SelectInput` and `SaveButton` do render standalone, themed, outside `EditModal` and
with no provider, so they are available. They are a smaller set than
`shared/ui-shared/src/fields.tsx`: the host `Field` nests its control without an `htmlFor`
pairing and offers no helper text, `SelectInput` always prepends an empty option and is not generic
over the value type, and `NumberInput` does not clamp to `min` and `max`. The shared primitives also
carry `FieldGroup`, which exists because a `<label>` forwards a click to its first labelable
descendant, so a label over a chip row deletes a chip. Use the shared primitives for forms, the host
components for the dialog shell around them, and do not collapse the first into the second.

`@cove/runtime/api` has `extensionFetch`, the host's authenticated same-origin fetch. A bare
`fetch`, and the SDK's own `request`, carry no credential, so every call answers 401 on an instance
with auth enabled. Nothing reaches it directly: `shared/ui-shared/src/wireClient.ts` binds it into
every generated client once, and every request goes through that client.

The host import map serves `@tanstack/react-query` and `shared/ui-shared/vite/host-externals.json`
externalizes it, so it adds nothing to a bundle, and the host mounts the `QueryClientProvider` above
the tree an extension surface renders in. Prefer `useQuery` over a hand-written loading, error and
data state machine, and `refetchInterval` over a hand-written poll loop. A dependency the import map
does not serve ships inside the bundle, so it needs a stated benefit.

## A file exists when it carries something

- A `use*` hook is for data access a view should not hold: a request, a subscription, a poll. State
  that lives and dies with one component stays in it.
- A `*Store.ts` is for a request lifecycle or state that outlives one component, or that several
  components share. A hook that reads one route needs no store beside it.
- Do not split a view to give a test a seam. Test the view.
- Feature slices sit directly under `src/`, beside `index.ts`, `wire/` and `common/`. A sub-concern
  reachable from one slice nests under it. No `features/` or `hooks/` folder, and no `ui/`, `lib/`
  or `model/` folder inside a slice.
- The filename suffix tells the kind: `*Logic.ts` pure, `*Store.ts` infrastructure, `use*.ts` data
  hook, `*.tsx` view. Add a suffix only for a kind the set lacks.
- A `*Logic.ts` module imports only its relative siblings, so it runs with no environment and no
  mocks. ESLint enforces this.

## Overlays

A confirmation, or an edit form over a few fields, uses the host's `ConfirmDialog` or `EditModal`.
The focus, keyboard and outside-click hook in `shared/ui-shared` is for an overlay the host has no
component for: a menu, or a modal with a body of its own. No overlay library, and no native
`<dialog>`: `showModal()` makes the host page inert, covers the host's own overlays, and can let a
repeated Escape close it while the page is blocking a close.

## Types tell the truth

- Named exports only. The one default export is the bundle entry object in `index.ts`.
- Everything about a route comes from the generated `src/wire/`: the types (`import type`), one
  function per operation, and the TanStack Query helpers over them. Call
  `useQuery(renamerGetOptionsOptions())`, never a hand-written path, method, query key or response
  type. The client's fetch is bound once in `shared/ui-shared/src/wireClient.ts`; nothing else
  calls `fetch`.
- Where a type cannot be generated, pin the values in a test whose expected value you copied from
  the server rather than computed from the module under test.
- A value the server may omit is optional in the type and handled where it is read. Do not widen a
  type, assert with `as`, add a non-null `!`, or disable a rule to make a build pass. Where a
  generated type contradicts the code, the code is wrong, or the server is.
- A failed request surfaces as something the user can read; `errorText` turns an `ApiError` into
  one. A `catch` that returns an empty list makes a broken route look like an empty library.
- Host Tailwind token classes only. No `dangerouslySetInnerHTML`.
