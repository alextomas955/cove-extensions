## Project

A Cove extension (**Renamer**, `com.alextomas955.renamer`) — a C# class library that plugs into a
self-hosted Cove media-library instance. It lets users rename — and optionally relocate — their
media files based on metadata, using configurable naming templates, all configured from a settings
panel inside Cove's own UI and persisted in Cove's backend.

**Core Value:** Users can rename their media files from metadata **safely** — the operation never
loses track of a file (Cove's database stays authoritative) and is previewable before it touches
disk. If everything else is cut, a reliable dry-run-then-rename that keeps the library intact is
the thing that must work.

## What NOT to do

| Avoid | Why |
|-------|-----|
| Shipping `Cove.Core` / `Cove.Plugins` / `Cove.Sdk` / EF Core / Npgsql / Pgvector in the package | Host-provided; bundling them causes `AssemblyLoadContext` type-identity mismatches at runtime. `Cove.Sdk.targets` strips them, but verify the published output. |
| Direct SQLite/Postgres writes to rename records (Stash anti-pattern) | Schema-fragile, corrupts the DB. Use `CoveContext` + `SaveChangesAsync`. |
| Assuming a core "rename/move file" service exists on the host | **It does not.** Only `POST /api/files/move` (changes folder, keeps basename). The extension does the disk rename itself. |

## Contract

- This extension implements **`IExtension`** from **`Cove.Plugins`** (`using Cove.Plugins;`).
- It references **`Cove.Sdk`** (which transitively carries `Cove.Plugins` + `Cove.Core`) via the
  monorepo root's `Directory.Build.props`/`Directory.Build.targets` auto-wiring — `Renamer.csproj`
  does NOT add its own direct Cove reference or its own `Directory.Build.props`.
- `extension.json` declares the manifest (`id`, `name`, `entryDll`) Cove loads the built assembly
  through. `entryDll` MUST match the built assembly name (`Renamer.dll`).

## Working on this extension

- This extension lives at `extensions/Renamer/` inside the `extensions/` monorepo. It is **not**
  its own git repo — it has no own remote and no own CI workflow file. CI is defined once at the
  monorepo root (`extensions/.github/workflows/build.yml`) and driven by this extension's entry in
  `extensions/catalog.json`.
- Launch your editor/agent tooling with the sibling Cove core checkout also available (e.g. via an
  `--add-dir`-style flag) so the local Cove source is available for SDK/source reference — the
  exact relative path depends on where you launch from; don't hardcode a path literal here, follow
  your own workspace's routing convention instead.
- This extension's own planning lives at `extensions/Renamer/.planning/` — its own nested planning
  root, distinct from the thin `extensions/.planning/` at the monorepo root (which is scoped to
  cross-cutting monorepo concerns, not Renamer's own feature work). Planning notes are this repo's
  own workflow, gitignored, and not part of the published extension.

## Build note

- The **dev local-source build** is the path used to load into the running dev Cove: it references
  `Cove.Sdk` (transitively `Cove.Plugins` + `Cove.Core`) from a local Cove checkout (the sibling
  the root `Directory.Build.props` auto-detects, or a checkout pointed at by `COVE_REPO`), so the
  extension is ABI-identical to the running host. The selector in the root `Directory.Build.props`
  picks local source when an explicit `-p:UseLocalCoveSource=true` is set, else a `COVE_REPO`
  checkout, else the `../cove` sibling auto-detect (relative to the monorepo root); otherwise it
  falls back to the published NuGet packages. Use `scripts/deploy-dev.ps1` for the full
  build → strip-verify → deploy → restart loop.
  - Local-source caveat: the `Cove.Sdk` host-assembly stripping rules ship in the NuGet package's
    `buildTransitive/` and are **not** auto-imported on a ProjectReference, so the root
    `Directory.Build.targets` explicitly imports `Cove.Sdk.targets` on the local path to strip the
    transitive `Cove.Core.dll` — this is handled once at the monorepo root, not per-extension.
- **Publish-readiness** targets the published NuGet packages (the pinned `CoveSdkVersion`,
  `Private=false`); on that path `Cove.Sdk.targets` is imported transitively from the package.

## SDKs

- **`Cove.Sdk`** (NuGet, via root wiring) is the contract this extension uses — it transitively
  carries `Cove.Plugins` (the `IExtension` boundary) and `Cove.Core` (entity model).
- **`@cove/extension-sdk`** is the separate *frontend* SDK. This extension ships a frontend — the
  settings/preview panel bundle built from `src/Renamer.Ui/` to `dist/index.mjs` — so the SDK is
  used to build that bundle. It is not published to npm, so it is vendored as a committed tarball
  at `src/Renamer.Ui/vendor/` and consumed through a `file:` dependency that `npm ci` installs
  offline (regenerate the tarball with `scripts/update-cove-sdk.ps1` when the SDK version changes).

## C# Comments & XML docs

**The code explains the what. Comments explain the why.** Default to no comment: if the code
already says it plainly, a comment only adds drift risk. This extension's value is *safe*
renaming, and the subtle invariants that keep it safe (TOCTOU windows, copy-then-verify-then-delete
across volumes, MAX_PATH re-anchoring) are exactly the things that earn a comment.

- **Write a comment only for:**
  - Domain / business rules not visible in the code — e.g. routing precedence (Tags over Studio
    over Default).
  - Non-obvious edge cases — MAX_PATH re-anchoring, the order in which `..` segments are collapsed.
  - External-system quirks — the Cove ABI, the `\\?\` long-path prefix disabling `..` collapse,
    the fact that there is no atomic cross-volume move.
  - Security / safety reasoning — resolving symlinks as late as possible to keep the TOCTOU window
    minimal.
  - Perf / concurrency / data-consistency assumptions — `CoveContext` is not thread-safe; each
    worker gets its own scope; the revert log is single-writer.
  - Temporary workarounds — and only with a removal condition (why it is here, when it can go).
  - Public-API contracts a caller cannot infer from the signature — null behavior, whether a
    method throws, ordering guarantees.

- **Never write:**
  - **Name restatement** — a comment that just repeats the member, variable, or type name.
  - **Tutorial narration of obvious code** — e.g. a comment above a `foreach` saying it loops over
    the items.
  - **Change-narrative / author voice** — phrasings that describe the edit or speak as the person
    making it rather than describing the code.
  - The first two are the primary deletion targets when cleaning up existing comments.

- **XML docs (`///`)** — earned by judgment, not mandated:
  - **Earned** on the public / SDK-facing surface — the `IExtension` boundary, interfaces,
    shared-vocabulary contract types, anything other tooling binds against — where the tag states a
    contract a caller cannot read from the signature.
  - **Optional and discouraged-when-redundant** on internal app code.
  - **None** on test or generated code — test names are the documentation.
  - Each tag earns its place: no `<param>` that merely restates the parameter name. `<remarks>` is
    the home for the *why* and the edge cases; `<exception>` is genuinely useful because it
    documents which throws a caller must catch.

```csharp
// BAD — the summary just restates the signature; it adds nothing a reader cannot see.
/// <summary>Gets the user by id.</summary>
User GetUserById(int id);
```

```csharp
// GOOD — the summary states the contract; <remarks> carries the why and the edge case.
/// <summary>Resolves <paramref name="candidate"/> to its canonical on-disk path.</summary>
/// <remarks>
/// Resolves symlinks as late as possible so the gap between the safety check and the move stays
/// small (a smaller TOCTOU window). Returns the canonical path, or throws when the target escapes
/// the allowed roots.
/// </remarks>
string ResolveCanonicalPath(string candidate);
```

- **AI tools (Claude Code / Copilot / Cursor):** when generating code, do **not** add comments or
  XML docs unless they clear the *why* bar above. Match the surrounding comment density. Never
  narrate the edit. Never restate a name.

- **Analyzer posture (why there is no forced-doc rule):** `GenerateDocumentationFile` is ON only so
  `IDE0005` (dead `using`) is reported on build; `CS1591` (missing XML doc on a public member) is
  deliberately silenced via `NoWarn`, so doc *presence* is not mandated. There is intentionally
  **no** StyleCop / Sonar / Meziantou doc-enforcement analyzer — StyleCop's `SA1600` / `SA1611` /
  `SA1615` would manufacture exactly the filler this policy forbids. Do not add forced-doc rules;
  this policy is prose- and AI-instruction-enforced. See the root `Directory.Build.props` for the
  actual lever state.

- The repo's standing ban on planning-process jargon and AI/author-narrative voice in all shipped
  artifacts governs comments too — this policy reinforces it, it does not replace it.
