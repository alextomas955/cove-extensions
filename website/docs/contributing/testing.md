---
sidebar_position: 3
---

# Testing

This repo has four test tiers. They run with different tools, from different directories, and prove
very different things. This page is the reference for what each one covers, how to run it, what it
needs before it runs at all, and which of them actually gates a merge.

Pages this one does not restate:

- First-time setup and the prerequisites a first build needs - [Getting
  started](./getting-started).
- The daily edit loop, the formatting and lint gates, and the pre-commit hook -
  [Development](./development).
- Every configuration knob these commands read - [Configuration reference](./configuration).
- How to add a new extension's end-to-end suite - [Adding an extension's E2E
  suite](./authoring-e2e).
- The end-to-end harness itself: fixtures, parallel execution, container cleanup, and implementation
  notes - `tests/e2e/README.md`.
- Where a test file belongs, and the shape rules a new test follows - [Extension authoring
  patterns](./authoring-patterns).

## The test tiers

| Tier         | What it covers                                                                                 | Command                              | Directory              |
| ------------ | ---------------------------------------------------------------------------------------------- | ------------------------------------ | ---------------------- |
| Repo tooling | The first-party Node scripts under `scripts/` that CI depends on                               | `npm test`                           | repo root              |
| C#           | An extension's backend, from pure logic up to its endpoints on a real `CoveContext`            | `dotnet test CoveExtensions.slnx`    | repo root              |
| UI           | An extension's UI bundle, plus the shared UI package's own suite                               | `npm run test`                       | the extension's UI dir |
| End-to-end   | The assembled package installed into a released Cove container, driven over HTTP and a browser | `npm test -- --project=<e2eProject>` | `tests/e2e`            |

The four are not interchangeable, and the C# tier in particular covers a different amount depending
on how the build located Cove. Read [Which set of C# tests you just ran](#which-set-of-c-tests-you-just-ran)
before you report a green C# run.

## Run the repo tooling tests

From the repo root:

```sh
npm test
```

That runs Node's own test runner over every `scripts/*.test.mjs`. The subjects are the scripts CI
calls: the catalog validator, the package assembler, the wire-type generator, and the Cove assembly
fetcher. They are fixture-driven, so this tier needs nothing but Node and a root install.

CI globs the same pattern rather than naming files, so a new test file under `scripts/` is covered as
soon as it exists.

## Run the C# suite

From the repo root, run every test project the solution declares:

```sh
dotnet test CoveExtensions.slnx
```

Or one extension's project alone:

```sh
dotnet test --project extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj
```

The runner is xUnit on the Microsoft Testing Platform. `global.json` selects the platform and pins
the SDK, so read both values there rather than anywhere else.

Two things about this tier are worth knowing before you read its result:

- Only a project with its own runner is a test project. `shared/Cove.Extensions.Shared.Testing`
  holds test code shared across extensions, including the base class that emits an extension's wire
  document, but it carries no runner and runs nothing of its own. Its tests execute inside each
  extension's test project, through a derived class there.
- The wire-document drift check lives in this tier and runs in both Cove source modes. It emits the
  committed OpenAPI document from the extension's own endpoint registrations and fails when the two
  differ. [Development](./development#regenerate-the-wire-types-after-a-handler-change) has the
  rewrite-and-regenerate loop for an intended change.

`extensions/Renamer/src/Renamer.Tests/README.md` describes that project's own folder conventions and
the platform gates some of its tests carry.

## Run an extension's UI tests

From the extension's UI directory, for example `extensions/Renamer/src/Renamer.Ui/`:

```sh
npm run test
```

That generates the wire types first, then does one Vitest run. It is also one of the checks inside
`npm run verify`, which is what the pull-request build runs for a UI bundle.

The run covers two Vitest projects, not one. Besides the bundle's own tests, it runs the shared UI
package's suite, rooted at `shared/ui-shared`. That package is consumed as raw source through a Vite
alias rather than installed, so it has no dependencies of its own and cannot host a runner. One
install and one runner serve both surfaces. The project names are declared in that extension's
`vite.config.ts`.

## Run the end-to-end suite

This tier needs Docker running. From `tests/e2e`:

```sh
npm test -- --project=<e2eProject>
```

`<e2eProject>` is the value the extension's `extensions/catalog.json` entry declares in its
`e2eProject` field. For Renamer that is `--project=renamer`. Omit `--project` to run every registered
suite.

Each Playwright worker brings up its own released Cove container and Postgres, installs the exact
file set the catalog entry's `artifacts` array declares, and drives the result over both the REST API
and a real browser. So this is the only tier that sees whether the host will load what a release
ships.

Go through `npm test` rather than calling Playwright directly. A `pretest` hook publishes every
extension that declares a suite, and that hook is what produces the output the harness installs; a
direct `npx playwright test` skips it and installs whatever happens to be on disk already.

## Run one test or one subset

**C#.** Filter by class or by method, against the solution or against one project:

```sh
dotnet test CoveExtensions.slnx --filter-class "*DestinationResolverPrecedenceTests"
dotnet test CoveExtensions.slnx --filter-method "*DirectStudio_OutranksAncestorStudio"
```

Filter on the class name, not the file name. A test file here commonly holds several classes, and
often none of them is named after the file, so a pattern built from a filename matches nothing. When
a filter matches nothing the summary still reads `failed: 0`, which looks green; the run reports
`Zero tests ran` and exits non-zero. Read the `total` line, never the `failed` line.

**UI.** Pass a path, or name a Vitest project. From the UI directory:

```sh
npm run test -- src/settings/options.test.ts
npm run test -- --project ui-shared
```

**Repo tooling.** Name the one file:

```sh
node --test scripts/generate-wire-types.test.mjs
```

**End-to-end.** Add a spec path or a tag selection to the project selection. From `tests/e2e`:

```sh
npm test -- --project=<e2eProject> --grep @smoke
```

Playwright exits non-zero when a `--grep` matches no test, so a selection that has gone stale fails
by name instead of passing over an empty set.

## Which set of C# tests you just ran

The C# test project compiles to a different set depending on how the build located Cove. In `none`
mode, with no Cove source available, the project file removes from compilation every source that
needs Cove's own source types. Four directories go wholesale: `Concurrency/`, `Events/`,
`Execution/` with everything under it, and `Preview/`. Named files go with them, both under `Api/`,
`Jobs/`, `Options/` and `TestSupport/` and at the project root. Read the `Compile Remove` group in the
test project file for the authoritative list rather than reconstructing it from this paragraph.

Both modes print `Passed!`, and the smaller run is much smaller. That is the whole hazard: a
contributor with no Cove checkout can run the suite, see it pass, and report the full suite green.

Check which set you ran rather than assuming, in either of two ways:

- Read the Cove source selection. Every build prints one line naming the resolved mode and the
  absolute root, and the property query answers the same question without a build. [Configuration
  reference](./configuration#cove-source-selection) has both, plus the precedence that decides it.
- Compare the totals. Run the same command a second time with `-p:CoveSourceMode=none` and read the
  `total` line each run prints. The gap between them is the coverage the checkout is buying you. The
  skip count moves too, because some platform-gated tests exist only in the larger set.

The mode is always declared explicitly in CI rather than inferred from whether a checkout landed,
for exactly this reason: a failed clone would otherwise select the smaller set and still report
success.

## The safety gate and the smoke leg

`.github/workflows/build.yml` runs the C# tier three ways, and only two of them are evidence about
behavior.

- The **`build` job** compiles and tests with `-p:CoveSourceMode=none`. Everything in the removal
  list above is absent, so what runs is the pure tier. This leg proves the extension still compiles
  and its pure tests pass with no host present. It is a compile and pure-logic smoke, and it is
  **not** the safety gate. Treat a green result here as proving the smaller claim it makes.
- The **`test-cove-present` job** shallow-clones Cove at the version the workflow's axis resolved
  and runs the same test project with `-p:CoveSourceMode=source`. It is the only leg that runs the
  tests needing a real `CoveContext`.
- The **`e2e` job** installs the assembled package into a running released Cove container. It is the
  only leg that would notice a package the host refuses to load.

`lint.yml` runs the C# tier a fourth way, on Windows. That leg also builds cove-absent, so it is the
same smoke on another operating system rather than extra coverage of the removed directories. The
end-to-end tier cannot run there at all: GitHub-hosted Windows runners fix Docker to Windows
containers and Cove ships no Windows image.

`build.yml` aggregates its own legs into one job that asserts each result, and that aggregate is what
branch protection is meant to require. Two qualifications:

- A green aggregate proves the containerized install ran only for a catalog entry that declares an
  end-to-end suite. For an entry declaring none, that job skips every step and still reports success.
  [Monorepo architecture](./architecture) has the mechanism.
- Which status checks branch protection actually requires is a repository setting, not a file. Read
  it on the settings for `main` rather than inferring it from this page.

## Prerequisites and failure symptoms

Each entry leads with the symptom, because that is what you arrive with.

- **A C# run reports far fewer tests than you expect and still says `Passed!`.** The build took the
  cove-absent path. See [Which set of C# tests you just
  ran](#which-set-of-c-tests-you-just-ran).
- **A C# run reports `Zero tests ran` and exits non-zero, with `failed: 0` in the summary.** Your
  filter matched nothing. Check the class name against the file's actual declarations rather than
  against its filename.
- **A cross-volume C# test skips with a reason naming a second filesystem.** Those tests need a real
  second mount. A `subst` drive supplies it on Windows and `/dev/shm` on Linux, so the skip means
  neither applied and you need to name a directory on another filesystem yourself. The variable is in
  [Configuration reference](./configuration#environment-variables); it refuses a directory that turns
  out to be on the same volume, rather than running the tests against the wrong code path.
- **A UI test run fails on a missing module under `wire/`.** The generated wire types are absent. The
  `test` script generates them first, so this only happens when you call Vitest directly. Generate
  them, or use `npm run test`.
- **`@cove-extensions/e2e` does not resolve.** The end-to-end packages are npm workspaces, and a root
  install is what symlinks the harness under that name. Run `npm ci` at the repo root, with
  workspaces, rather than installing inside `tests/e2e`.
- **Playwright reports a missing browser executable.** The browser download is a separate one-time
  step. From the repo root:

  ```sh
  npm run install:browsers --workspace @cove-extensions/e2e
  ```

- **An end-to-end run fails at container startup, before any assertion.** Docker is not running or
  its daemon is not reachable from your shell. Docker is needed for this tier only; nothing else on
  this page uses it.
- **An end-to-end run fails at install, naming a publish directory that does not exist.** The publish
  step did not run, which is what happens when Playwright is invoked directly instead of through
  `npm test`. This tier needs the .NET SDK for that reason, even though the suite itself is
  JavaScript.
- **Containers are still running after an interrupted end-to-end run.** Cleanup can be missed when a
  run dies mid-startup. `tests/e2e/README.md` has the commands.
