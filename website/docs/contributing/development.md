---
sidebar_position: 2
---

# Development

This is the day-to-day loop: what to run after an edit, which directory to run it from, and what a
red result means. It assumes you already have a working first build - if you do not, start with
[Getting started](./getting-started).

Pages this one does not restate:

- The test tiers and how to run them - [Testing](./testing).
- Every configuration knob, and every gate tool's config file - [Configuration
  reference](./configuration).
- How the build wiring works, and why each fact is declared where it is - [Monorepo
  architecture](./architecture).
- Module shape, the wire contract, and the correctness rules - [Extension authoring
  patterns](./authoring-patterns).
- Adding an end-to-end suite - [Adding an extension's E2E suite](./authoring-e2e).
- The branch model - [Branching](./branching). Cutting a release - [Releasing](./releasing).

**Every command below names the directory it runs from, and that matters.** `npm run <script>`
resolves against the nearest `package.json`, and this repo holds several, so the same script name
does different things in different directories. [Configuration reference](./configuration) has the
worked examples.

## Build and check the C# side

From the repo root, build every project the solution declares:

```sh
dotnet build CoveExtensions.slnx
```

To iterate on one extension, build its project alone:

```sh
dotnet build extensions/Renamer/src/Renamer/Renamer.csproj
```

Warnings are errors, so an analyzer finding fails the build instead of appearing as a suggestion in
an editor, and an unused private member or parameter fails it too. The levers are in [Configuration
reference](./configuration).

Two things to read before you take a green build as proof of anything:

- `dotnet build` prints one line naming the Cove source it resolved and the absolute path it
  resolved it from. `dotnet test` drops that line, so a test run stays silent about which source it
  built against. To read the selection back without building at all, query the properties: [Configuration
  reference](./configuration#check-which-source-was-selected) has the command.
- A project missing from `CoveExtensions.slnx` is not compiled by the solution build and is not seen
  by the C# formatting gate. The catalog validator is what catches that, not the build.

Then run the tests - see [Testing](./testing).

## Build and check an extension's UI bundle

Run these from the extension's UI directory, `extensions/Renamer/src/Renamer.Ui/`:

```sh
npm run typecheck   # generates the wire types, then tsc --noEmit
npm run test        # generates the wire types, then one Vitest run
npm run verify      # typecheck, Prettier check, class check, host-import check, tests
npm run build       # rebuilds dist/index.mjs
```

`verify` is what the pull-request build runs for a UI bundle, so clear it before you push. `dist/` is
build output and is gitignored - a normal source change never builds or commits a bundle, because CI
rebuilds it from source and packages what it built.

Two of the checks inside `verify` are worth knowing separately:

- `check-classes` fails on a Tailwind utility class the host does not emit, and on the raw-HTML React
  prop. The host's Tailwind pass never scans this bundle, so a class the host does not already emit
  renders unstyled with nothing reported anywhere.
- `check-host-imports` resolves each host import-map external against the module the host actually
  serves, read out of a local Cove checkout. A name can typecheck, build, and pass every other check
  while the shipped bundle fails to load in the browser. With no checkout present it prints a skip
  and exits 0, which is why it can never be a gate - see [What blocks a merge](#what-blocks-a-merge).

## Regenerate the wire types after a handler change

The UI's wire types are generated from a committed OpenAPI document and are gitignored. That document
is itself derived from the C# handler registrations by a test. So changing an endpoint's shape moves
two generated things, in this order:

1. **Rewrite the committed document.** `RenamerOpenApiDocumentTests` emits it from the extension's
   own shipped registrations and fails when the committed copy differs. Set `COVE_WIRE_DOC_UPDATE=1`
   for one run of `extensions/Renamer/src/Renamer.Tests` to make it write the document instead of
   comparing against it.
2. **Regenerate the types.** From the repo root, for every extension that declares a UI:

   ```sh
   npm run generate:wire
   ```

   or for one extension:

   ```sh
   node scripts/generate-wire-types.mjs --extension com.alextomas955.renamer
   ```

   The UI directory's own `npm run generate:wire` does that one extension, and `typecheck` and `test`
   there each run it first. All of these load the generator's dependency from the root install, so a
   root `npm ci --no-workspaces` has to have happened.

3. **Typecheck the UI.** A field that changed name or type surfaces here.

What each skipped step costs you:

- Skip the document rewrite and the test fails on drift, which is the check working.
- Skip the type regeneration and the UI typechecks against stale types. A field the server no longer
  sends still typechecks and reads `undefined` in the browser, with no error anywhere.
- Skip both on a fresh clone or a new worktree and the module does not exist at all: the UI typecheck
  fails on it, and the root ESLint and knip passes report errors across files your change never
  touched.

Never hand-write one of these types instead. Why, and how request casing differs from response
casing, is on [Extension authoring patterns](./authoring-patterns).

## Deploy into a local Cove host

Loaded assemblies are not hot-reloaded, so seeing a change in a running Cove means rebuild,
reinstall, restart. Renamer ships one script for that whole loop, runnable from any directory:

```sh
pwsh extensions/Renamer/scripts/deploy-dev.ps1
```

It publishes against a local Cove source checkout so the extension is ABI-identical to the running
host, builds the UI bundle, assembles exactly the file set `extensions/catalog.json` declares for the
extension - the same set a release ships - installs it under the Cove data root, and restarts the
host.

Two conditions before you run it. Invoke it as `pwsh`, not Windows PowerShell 5.1, which does not
define an automatic variable the script reads. On macOS and Linux set `COVE_HOME`, because the data
root it falls back to is Windows-only and it throws rather than guessing at a directory Cove never
reads. `extensions/Renamer/README.md` has the rest.

## Format and lint

### TypeScript, Markdown, JSON, and YAML

One Prettier config and one ESLint config cover the whole repo, so there is nothing per-extension to
run. From the repo root:

```sh
npm run format        # Prettier, writes
npm run format:check  # Prettier, verifies
npm run lint          # ESLint
npm run lint:fix      # ESLint, writes what it can fix
```

ESLint is type-aware, so it builds a TypeScript program per UI bundle. Install each UI's dependencies
and generate the wire types first, or it reports most of the tree.

For Markdown, from the repo root:

```sh
npx markdownlint-cli2
```

Pass no path. The file list lives in `.markdownlint-cli2.jsonc` and only there, because
markdownlint-cli2 appends its config's globs to the command line - a path you pass can only widen
the set while reading as a narrowing.

### C# formatting

From the repo root:

```sh
npm run format:cs        # writes
npm run format:cs:check  # verifies
```

**Run the C# pass through the script rather than calling `dotnet format` yourself.** Two reasons,
both of which have bitten here:

- With a `../cove` sibling checkout present, the extensions reference Cove by project, so
  `dotnet format` walks the ProjectReference graph into Cove's own source and reports hundreds of
  findings that are not yours. The script excludes that path. The exclude does the same work in CI,
  which checks Cove out beside this repo.
- A folder path passed to `--include` or `--exclude` must end in a path separator. Without one it
  matches nothing and exits 0, so a scoping mistake does not fail - it silently passes.

The C# job in `.github/workflows/lint.yml` runs that same script, so your local run and CI check the
same subject set and both print the same partial-coverage disclosure. The depth can still differ: CI
checks Cove out, and without a local checkout the test project gets a whitespace-only check on your
machine. [Known traps](#known-traps) has the symptom and how to get the coverage back.

## Run the merge gates

Each of these runs on every pull request. Run the ones your change can plausibly break before you
push; run all of them if you touched root tooling.

| Gate               | What it catches                                                                                                          | Command                                                                | Directory        |
| ------------------ | ------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------- | ---------------- |
| Prettier           | Formatting drift in everything not ignored, including this page                                                          | `npm run format:check`                                                 | repo root        |
| ESLint             | Lint and import-boundary violations across every UI bundle and every first-party script                                  | `npm run lint`                                                         | repo root        |
| C# formatting      | `.editorconfig` violations across the solution                                                                           | `npm run format:cs:check`                                              | repo root        |
| C# analyzers       | Any compiler or analyzer warning, which is an error here                                                                 | `dotnet build CoveExtensions.slnx -c Release -p:CoveSourceMode=source` | repo root        |
| syncpack           | The same dependency pinned to different versions across the repo's `package.json` files                                  | `npm run syncpack`                                                     | repo root        |
| knip               | Dead files, unused exports, and unused dependencies on the TypeScript side                                               | `npm run knip`                                                         | repo root        |
| jscpd              | New copy-paste, above the threshold its config declares                                                                  | `npm run jscpd`                                                        | repo root        |
| markdownlint       | Markdown rule violations in the docs                                                                                     | `npx markdownlint-cli2`                                                | repo root        |
| Catalog validator  | A declared catalog path that does not exist, a project missing from the solution, a floor that disagrees with a manifest | `node scripts/validate-extension-repo.mjs`                             | repo root        |
| Repo tooling tests | A regression in the scripts under `scripts/`                                                                             | `npm test`                                                             | repo root        |
| UI verify          | Typecheck, formatting, class discipline, and unit tests for one bundle                                                   | `npm run verify`                                                       | the UI directory |

The C# analyzers row needs a Cove source checkout. Its `-p:CoveSourceMode=source` refuses to fall
back, so on a clone without one the command fails before it compiles anything. A `../cove` sibling
supplies a checkout by auto-detect, and `COVE_REPO` names one explicitly; [Configuration
reference](./configuration#cove-source-selection) has both knobs and the precedence between them. CI
clones Cove itself and points the build at that clone, which is why the row's command runs there
without you having a checkout of your own.

knip resolves the end-to-end packages' configs, so it needs a plain `npm ci` at the root rather than
the `--no-workspaces` form, and it needs the wire types generated. The dead-code class it gates on the
TypeScript side is gated on the C# side by the analyzer build instead, so neither tier is left
ungated.

### What blocks a merge

**Only a check a CI workflow runs can block a merge.** A local hook is advice a contributor can skip,
so a gate you need enforced belongs in a workflow. Every gate in the table above runs in
`.github/workflows/lint.yml` or `.github/workflows/build.yml`, so every one of them can fail a pull
request.

Three qualifications:

- `check-host-imports` is the exception in the other direction. It needs a local Cove checkout, so
  the copy that runs in CI inside a UI's `verify` always skips. It is a local check only, and it can
  never fail a pull request.
- `build.yml` aggregates its own validate, build, test, and end-to-end legs into a single status
  check, so that one status stands for the whole chain. What a green aggregate does and does not prove
  is on [Monorepo architecture](./architecture). The jobs in `lint.yml` report separately, with no
  `needs:` link into that chain.
- Which status checks branch protection actually requires is a repository setting rather than a file.
  Read it on the settings for `main`; do not infer it from this page.

The docs site is a third workflow, `.github/workflows/docs.yml`. On a pull request it builds only
when the change touches one of the paths that file lists, and it never publishes.

## The pre-commit hook

`lefthook.yml` at the repo root declares what runs when you commit: Prettier and ESLint on staged
files, the class check for each UI bundle, the host-import check, and `dotnet format` on staged
C# files. The formatting and lint entries fix and restage rather than report, because the check costs the
same either way. It is deliberately light - no build and no test run on commit.

The runner is installed by the root `prepare` script. The binary it installs comes from the lefthook
package's own install script, so if your package manager withholds install scripts, the binary is
never built, `lefthook install` cannot write the hook, and a commit runs no checks at all. If the hook
file exists but the binary is gone, the hook prints that it cannot find lefthook and the commit
succeeds anyway.

Confirm the hook is really installed, rather than assuming it:

```sh
npx lefthook version
```

That prints a version when the binary is present. Check that `.git/hooks/pre-commit` exists as well -
both have to be true. If either is missing, run `npm install` at the repo root and approve the install
script your package manager is holding back.

Because a passing commit is not evidence the hook ran, do not report "the hook passed" as a check. If
you need a hook's guarantee on a particular commit, run its checks by hand and say which ones you ran.

## Run the docs site locally

`website/` carries its own lockfile and is not a workspace, so it needs its own install. From
`website/`:

```sh
npm ci
npm start
```

`npm start` serves the site with hot reload, which is what you want while writing. It does not prove
your links: broken links throw at build time, so build the site before you push a docs change:

```sh
npm run build
```

The site sources `website/docs` and each extension's own `docs/` folder, so one build covers both.

Two link rules follow from broken links throwing. Link to another site page with a relative doc link
and no file extension, as in `./testing`. Refer to a repo file in backticks, or with a full
`github.com` URL - never with a markdown relative link, which resolves against site routes rather
than the filesystem.

## Known traps

Each of these is stated symptom first, because the symptom is what you arrive with.

- **The UI typecheck fails on a missing module, or the root ESLint or knip pass reports errors across
  files you never touched.** The generated wire types are absent. They are gitignored, so a fresh
  clone and a new worktree both start without them. Run `npm ci --no-workspaces` and then
  `npm run generate:wire` at the repo root.
- **The UI typecheck cannot find the type definitions for `react-dom`.** The UI project is not an npm
  workspace, so a root install never reaches it, and its `tsconfig.json` names its type packages
  explicitly. Run `npm ci` inside the UI directory, using `cd <dir> && npm ci` rather than the
  `--prefix` form.
- **`dotnet format` reports hundreds of findings in files under `../cove`.** It followed the
  ProjectReference graph into the sibling Cove checkout. Use `npm run format:cs`, which excludes it.
- **A `dotnet format` scoping flag reports nothing and exits 0.** A folder passed to `--include` or
  `--exclude` has to end in a path separator; without one it matches nothing and passes.
- **`dotnet format` reports no analyzer finding for the test project, and exits 0.** Without a Cove
  source checkout that project's references do not load, so only whitespace is checked there and the
  run still passes. It says `Required references did not load for Renamer.Tests` and continues. Point
  `COVE_REPO` at a checkout, or add a `../cove` sibling. The CI format leg checks Cove out, so this
  cannot happen there.
- **A commit completes and nothing was checked.** The hook runner is missing. Confirm with
  `npx lefthook version`, and treat CI as the gate either way.
- **The docs site build fails on a link that looks correct.** A markdown relative link to a repo file
  resolves against site routes, not the filesystem. Use backticks or a full `github.com` URL.
- **A script does nothing, or something other than what you meant.** You ran it in the wrong
  directory. Several script names exist in more than one `package.json` here with different meanings.
