# Cove Extensions Monorepo

## Project

This is the Cove extensions monorepo — a single git repository holding one or more Cove
extensions, following [yourcove](https://github.com/yourcove)'s official
`multi-extension-repo-template` pattern. Today it holds one extension, **Renamer**
(`extensions/Renamer/`). See `README.md` for the extension list and dev setup.

## Registry and CI

- `extensions/catalog.json` is the extension registry and the source of truth CI reads to compute
  its build matrix. Each entry declares that extension's `name`, `id`, `path`, `tagPrefix`,
  `projectPath`, `manifestPath`, `versionSourcePath`, and (optionally) `uiPath`. Adding a new
  extension's release capability is a `catalog.json` edit, not a workflow-logic change.
- CI (`.github/workflows/build.yml`) is a catalog-driven `validate → build → release` matrix: every
  catalog entry builds on every PR (no `paths:` filtering); a release for one extension is cut by
  pushing a tag of the form `<tagPrefix>v<semver>` (e.g. `renamer/v1.0.0`), which builds, strip-
  verifies, and packages only that extension.
- See `website/docs/contributing/branching.md` and `website/docs/contributing/releasing.md` for the
  full branching and release process.

## Build wiring

The root `Directory.Build.props`/`Directory.Build.targets` auto-wire `Cove.Sdk` (which
transitively carries `Cove.Plugins` + `Cove.Core`) for every project in the monorepo, either
against a local sibling `../cove` checkout (auto-detected, or via `COVE_REPO`) or from NuGet.
Individual extensions' `.csproj` files should not add their own direct Cove reference or restate
the relative-path math to `../cove` — that's centralized here.

Build the whole monorepo from this root:

```sh
dotnet build CoveExtensions.slnx
```

## Documentation upkeep

When a change alters an extension's settings, configuration options, public API, or user-facing
behavior, update that extension's docs in the same change — `extensions/<Name>/docs/`, its
`README.md`, and `CHANGELOG.md` as applicable, plus the matching docs-site page. Docs are part of
done. Do not defer them to a later change.

## How to write documentation

The docs site (`website/docs/`) follows a research-backed playbook (Diátaxis + Google/Microsoft
style guides). When writing or reviewing docs:

- **Keep the four Diátaxis modes separate** — don't blend them on one page:
  - **How-to guide** (task-oriented, for a competent user): a real-world goal, written from the
    *user's* perspective, a sequence of actions; omit teaching. → a "Rename your library" guide.
  - **Reference** (information-oriented, neutral, factual): its structure **mirrors the product**
    (group settings by the UI panel section, in the same order the user sees). → the settings and
    token references.
  - **Explanation** (understanding-oriented): the "why" / design & safety model. → `ARCHITECTURE`.
  - **Tutorial** (a single happy-path lesson): usually unnecessary for one extension.
- **Settings reference — per-setting anatomy:** name/label (as in the UI) · one neutral sentence of
  what it does · default · valid values/type · a short example when it clarifies. Uniform rows →
  a table; a setting needing nuance (routing precedence, templates) → a subsection with an example.
  Note settings that exist but aren't in the UI as an explicit "advanced / not exposed" callout.
- **Template/token systems:** lead with a complete worked example (a full template → the exact
  filename it produces), then a graduated series (name → +year → +resolution …); pair every token
  with its rendered output (`token = example`); group tokens into thematic tables; document syntax
  rules explicitly (Renamer's `{ … }` group collapses when its inner tokens are empty; `$$` is a
  literal `$`; absent tokens are omitted). List the shipped presets. Document tokens *as they are*
  (`$title`, `$resolution`) — do NOT impose an UPPERCASE_UNDERSCORE convention (that's for
  user-replaced CLI placeholders, not a fixed token vocabulary).
- **README vs site:** the GitHub README is a short entry point (what it is + a link to the site) and
  holds dev/build/release detail; the *user* story (what it does, settings, tokens) lives on the site.
- **Style:** second person ("you"), active voice, present tense; sentence-case headings; task
  headings use the bare infinitive ("Add a per-studio destination"), concept headings use noun
  phrases ("Naming templates"), never an -ing gerund; lead with the most important info and put
  conditions before instructions ("If X, do Y"); show an example before a paragraph of prose;
  progressive disclosure (common path first, advanced behind its own heading); screenshots sparingly.
