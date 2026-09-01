---
sidebar_position: 6
---

# Monorepo architecture

This repository holds several independently released Cove extensions plus the first-party code they
share. This page explains the seams that make that work - where each fact is declared, and which
files read it - so you can tell what a change touches before you make it.

It stops at the monorepo's own seams. For how a module inside an extension is shaped, read
[Extension authoring patterns](./authoring-patterns). For one extension's internals, read its own
architecture page - [Renamer's](../extensions/renamer/architecture). Branch model:
[Branching](./branching). Cutting a release: [Releasing](./releasing). Adding an end-to-end suite:
[Adding an extension's E2E suite](./authoring-e2e).

## The catalog is the registry

`extensions/catalog.json` is where an extension is declared. Everything else derives from it, so
adding or changing a capability is normally a catalog edit rather than a change to the machinery that
reads it.

Read the field set off the file itself and off `scripts/validate-extension-repo.mjs`, which is what
enforces it. Do not copy the field list into prose - one such copy has already drifted here, and
three documents then named a field the build had dropped. The validator checks that every declared
path exists, that every C# project the catalog implies is in `CoveExtensions.slnx` (the formatting and
analyzer gates take their subject list from that solution, so a project missing from it is silently
never compiled), and that the floor an extension advertises agrees with its registry manifest.

The worked example is release capability. No job logic in `.github/workflows/build.yml` names an
extension; every value it acts on comes from the catalog matrix.
Its `validate` job reads the catalog and publishes the build matrix as a job output; `build` and the
end-to-end job consume that output. So a new entry starts building on every pull request the moment it
exists, and a release for it is cut by pushing a tag matching its own `tagPrefix` - the validate job
refuses a tag that matches no entry or more than one. No workflow logic changes.

Optional catalog fields switch whole groups of steps on. An entry that declares a UI path gets the
frontend steps; an entry that declares an end-to-end path and project gets the containerized job; an
entry that declares neither skips them. That is convenient and it has a sharp edge worth knowing: the
end-to-end job's first step decides it has no suite, every later step skips, and the job still reports
success. A green aggregate therefore proves the containerized install ran only for an entry that
declares the path.

Two properties of the workflow come from the absence of something rather than from a line in it. There
is no `paths:` filtering on the pull-request trigger, so every catalog entry builds on every pull
request - an extension is never left unbuilt because a change looked unrelated. And the shipped file
set is declared, not discovered: the assembler copies exactly the entry's `artifacts` list into an
empty directory, so debug symbols and XML docs the build emits alongside cannot reach a package, and a
reviewer reads the catalog instead of a build listing.

## Cove is wired once, at the root

An extension compiles against the Cove host. Where that host comes from is resolved in exactly one
place: `Directory.Build.props` computes it, and `Directory.Build.targets` acts on the result.

Precedence, highest first:

1. An explicit mode (`-p:CoveSourceMode` or `COVE_SOURCE_MODE`, one of `source` or `none`).
2. An explicitly supplied checkout location (`-p:CoveRepoRoot` or `COVE_REPO`), which selects
   `source`.
3. A `../cove` sibling checkout, auto-detected - the zero-config default for local work.
4. Otherwise `none`: the published NuGet packages, which is the clean-clone path.

Explicitness is captured before any defaulting, because the choice turns on how a value arrived rather
than on whether a path happens to exist. Absence is a legitimate state for auto-detection and an error
for explicit configuration, and that distinction is the point of the design: a mode that cannot
deliver what it names stops the build with one plain message instead of falling back, because a silent
fallback compiles a smaller test set and still reports success. The gate lives in the targets file
rather than the props file for a mechanical reason - a property group can only compute, and stopping a
build needs a target. It prints the resolved absolute path, which is what makes a shell's rewriting of
a POSIX path into a drive-lettered one visible at all.

The same file wires the reference. Referencing `Cove.Sdk` is sufficient, since it transitively carries
the plugin-hosting and core-domain assemblies. This is why an individual extension's `.csproj`
declares neither a Cove reference nor its own relative climb to `../cove`: three files agreeing by
luck is a worse contract than one file being read. Renamer's project file states the trade explicitly
and adds only what is genuinely its own - a framework reference, its manifest, its bundled
dependencies.

One exclusion is easy to trip over. The wiring skips `*.Tests` projects, which already receive the SDK
transitively through their reference to the extension project. Adding it directly to a test project as
well makes MSBuild resolve it as non-copy-local, so the assembly drops out of the test project's own
output and the suite fails at reflection time.

`global.json` pins the .NET SDK band and selects the test runner. Read the version there rather than
anywhere else.

## One version per package

Every NuGet version lives in the root `Directory.Packages.props` with central package management on,
so individual project files carry version-less references. The comments there record why a given
version is held where it is, which is the kind of reasoning that goes stale if it is restated
elsewhere.

The Cove SDK pin is the deliberate exception. Its version is a property in `Directory.Build.props`,
derived from the single declared host floor, and the packages file references that property rather
than a literal. Two things follow. The validator can read one value as the host-SDK floor, and
Dependabot cannot bump a property indirection - so the host SDK stays hand-bumped in lockstep with the
local host you build against, which is what you want for a floor that describes a host capability an
extension relies on.

The properties naming which released Cove the end-to-end suite runs against are separate from that
floor on purpose. They name a moving upstream build; the floor is what an extension advertises to
every user. Editing the floor to make a version guard pass would quietly re-advertise a different
floor, so the two never share a value.

## Host-provided assemblies

An extension compiles against the Cove contracts and the data infrastructure they expose, but the host
process already has those assemblies loaded. The extension must not ship them. Read the exact set from
`Cove.Sdk.targets` in the host SDK, which is the file that both declares and strips it - the reference
side is marked so it does not copy local, and two targets remove the set from the build output and the
publish set.

Where that stripping comes from depends on how Cove was located, and this is the one asymmetry in the
build wiring. On the package path the rules are auto-imported from the SDK package's
`buildTransitive/` folder. On a local project reference nothing imports them, so the root
`Directory.Build.targets` imports the targets file explicitly and the local publish set is stripped
identically. Without that import the transitive core assembly leaks into published output on local
builds only - green everywhere a contributor looks.

What a leak actually costs is worth stating precisely, because overstating it invites the wrong fix.
The host prefers the assembly already loaded into its own context, so a shipped copy normally costs
package weight, not correctness. The correctness failure is the narrower one the SDK's own comments
name: a bundled copy loaded into the extension's own load context creates a second identity for types
that cross the host boundary, and then casts and dependency injection break silently. Treat both
mechanisms as claims and verify the published output.

First-party bundled dependencies are the mirror case, and Renamer shows the shape: they copy local,
they are absent from the strip list, and the catalog entry declares them in `artifacts` so the packer
copies them in. A dependency that must ship is deliberately declared outside the Cove reference wiring,
so it ships on both the local and the package path.

## Two levels of shared code

Repo-level `shared/` is for cross-extension code only, and it has one runtime member per tier:

- `shared/Cove.Extensions.Shared` - C#, consumed as a project reference, copies local, and ships
  bundled inside each extension's package. It is first-party rather than host-provided, which is why
  it is not marked non-copy-local and is absent from the strip list.
- `shared/ui-shared` - TypeScript and React, never installed from a registry.

The UI package is resolved from raw source. It ships the Vite library-mode config factory every
extension UI calls, and that factory aliases the package name and each of its subpaths to the real
source file, anchored on the factory's own location so no UI bundle restates the climb to `shared/`.
Subpaths must be listed before the bare entry, because Vite alias matching is prefix-based.

Two consequences follow from "raw source, no install", and both are load-bearing. The package has no
`node_modules` of its own, so anything it imports must either be externalized to the host import map
or pinned to the consuming UI's own copy - which is why the React plugin arrives as a parameter rather
than being imported there, and why the SDK alias points into the consuming UI's vendored copy. And the
externals list names each host module exactly once, in the spelling the host import map serves, because
a prefix match that missed a bare name would silently bundle a second React with no build failure and
break hook identity at runtime.

Test support sits beside these two and is not part of the shipped layer. Helpers needing no Cove types
are an ordinary project a test project references.
The fakes that do need Cove's own types have no project file at all: the Cove-dependent test project
pulls them in as ordinary compile items, unconditionally, rather than as a project reference,
because an IDE does not fully respect a reference condition. Promoting that directory to a project
of its own would give it its own dependency on a Cove checkout, one level further down.
Nothing here decides per mode what compiles. The project simply requires a checkout, a target in it
says so as one plain error before the compiler runs, and a solution build with no checkout skips the
project and names it rather than failing.

What belongs at repo level is decided by reach, never by a directory name. That rule, and the
extension-local `common/` layer it implies, is on [Extension authoring
patterns](./authoring-patterns).

## The wire seam

The seam between an extension's C# backend and its TypeScript UI is an HTTP contract, and the C#
handler signatures are the source of truth for it. Nothing in the chain is hand-written twice.

A committed OpenAPI document lives at a fixed place inside each extension. A test derives that document
from the extension's own shipped endpoint registrations - mounted in an in-memory app, with no request
sent and the host never contacted - and fails when the committed copy no longer matches, with an
environment variable to rewrite it after an intended change. So the document is a drift check rather
than a mirror someone maintains.

`scripts/generate-wire-types.mjs` then turns that document into the UI's wire types, which are
gitignored because they are a deterministic function of a committed file. This step is catalog-driven
and names no extension: an entry that declares a UI is generated from, one that does not is not. Only
the extension's own directory needs declaring, since the document's location inside it is fixed.

The practical consequence: a fresh clone has no generated types, so generate them before a UI
typecheck, before the UI tests, and before the root lint - otherwise those report a wall of errors in
files your change never touched. `README.md` at the repo root has the commands.

Why you must never hand-write one of these types instead, and how request casing differs from response
casing, is on [Extension authoring patterns](./authoring-patterns).

## Where documentation lives

The docs site is a standalone package under `website/`, built with Docusaurus. Its default docs
instance sources `website/docs` at the site root - that is where repo-wide pages like this one live.
One extra content-docs instance per extension sources that extension's own `docs/` folder at its own
route prefix, so an extension's pages live with the extension and the site reads them in place. The
offline search theme takes one entry per instance, as parallel arrays of route prefix and directory.

That is why there is no `docs/` folder at the repo root. Both homes already have an owner - the
extension folder for an extension's pages, the site for repo-wide ones - and a third copy would be a
home with nothing keeping it honest. GitHub's own root files stay at the repo root for the same
reason, reached from the site navbar by canonical `github.com` links rather than duplicated into a
page.

The sidebar is generated from the folder structure, so adding a page or a whole extension docs folder
needs no sidebar edit.

Two configuration choices shape what you can write, and both are in `website/docusaurus.config.ts`:

- Broken links throw. A wrong link fails the site build instead of shipping. Link to another site page
  with a relative doc link and no file extension; refer to a repo file in backticks, or with a full
  `github.com` URL - never with a markdown relative link, which resolves against routes rather than
  the filesystem.
- Markdown format is detected rather than forced. A `.md` file is parsed as CommonMark and MDX is
  reserved for `.mdx`. That is what lets an extension's changelog stay a file GitHub renders correctly
  - where an HTML comment is the only way to hide a note - while the site still builds it.
