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

The four are not interchangeable, and the C# tier in particular is split across two projects that
cover different things. Read [Which set of C# tests you just ran](#which-set-of-c-tests-you-just-ran)
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

From the repo root, run every test project the solution declares. This needs a Cove source checkout,
because one of the projects it holds cannot build without one:

```sh
dotnet test CoveExtensions.slnx
```

An extension declares two test projects, and the solution holds both. Renamer's are:

```sh
dotnet test --project extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj
dotnet test --project extensions/Renamer/src/Renamer.Cove.Tests/Renamer.Cove.Tests.csproj
```

The first needs no Cove source checkout; the second needs one and refuses to build without it. Read
[Which set of C# tests you just ran](#which-set-of-c-tests-you-just-ran) before you report either
result on its own, and run them one after another rather than concurrently — [that project's
README](https://github.com/alextomas955/cove-extensions/blob/main/extensions/Renamer/src/Renamer.Tests/README.md)
has the drive-letter reason.

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

`extensions/Renamer/src/Renamer.Tests/README.md` describes both of Renamer's test projects: their
folder conventions, which one a new test belongs in, and the platform gates some of their tests carry.

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

An extension's backend suite is split across two test projects, and **which project you named is what
decides the set**. For Renamer:

| Project                                                               | Covers                                           | Needs a Cove source checkout |
| --------------------------------------------------------------------- | ------------------------------------------------ | ---------------------------- |
| `extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj`           | Everything provable without a real `CoveContext` | No                           |
| `extensions/Renamer/src/Renamer.Cove.Tests/Renamer.Cove.Tests.csproj` | Everything that needs a real `CoveContext`       | Yes                          |

Name the solution and you get both. Name one project and you get that project, whatever mode the
build resolved — the set no longer moves with how the build located Cove, so the same command reports
the same total either way.

Two consequences worth knowing:

- **Naming only the first project is a partial run.** It reports `Passed!` over the tier it holds and
  says nothing about the endpoint and host-dependent tests. Say which project you ran when you report
  a result.
- **Naming the second project without a checkout fails immediately**, with one sentence telling you a
  checkout is required. It does not fall back to a smaller run.

To read the Cove source selection itself, every build prints one line naming the resolved mode and the
absolute root, and a property query answers the same question without a build. [Configuration
reference](./configuration#cove-source-selection) has both, plus the precedence that decides it.

The mode is declared explicitly in CI rather than inferred from whether a checkout landed, so a failed
clone fails the leg instead of quietly changing what it builds.

## The safety gate and the smoke leg

`.github/workflows/build.yml` runs the C# tier three ways, and only two of them are evidence about
behavior.

- The **`build` job** compiles and tests the checkout-free project with `-p:CoveSourceMode=none`. It
  proves the extension still compiles and that whole project passes with no host present. It is a
  compile and pure-logic smoke, and it is **not** the safety gate. Treat a green result here as
  proving the smaller claim it makes.
- The **`test-cove-present` job** shallow-clones Cove at the version the workflow's axis resolved and
  runs **both** test projects with `-p:CoveSourceMode=source`. It is the only leg that runs the tests
  needing a real `CoveContext`.
- The **`e2e` job** installs the assembled package into a running released Cove container. It is the
  only leg that would notice a package the host refuses to load.

`lint.yml` adds two more. Its **`windows-build-test` job** builds and runs the checkout-free project
on Windows in `none` mode, which is where the Windows-gated path assertions execute. Its
**`csharp-format` job** checks out Cove at the floor the extensions declare and builds the whole
solution in `source` mode with warnings as errors, so both test projects sit inside the format and
analyzer gates. The end-to-end tier cannot run on Windows at all: GitHub-hosted Windows runners fix
Docker to Windows containers and Cove ships no Windows image.

`build.yml` aggregates its own legs into one job that asserts each result, and that aggregate is what
branch protection is meant to require. Two qualifications:

- A green aggregate proves the containerized install ran only for a catalog entry that declares an
  end-to-end suite. For an entry declaring none, that job skips every step and still reports success.
  [Monorepo architecture](./architecture) has the mechanism.
- Which status checks branch protection actually requires is a repository setting, not a file. Read
  it on the settings for `main` rather than inferring it from this page.

## Prerequisites and failure symptoms

Each entry leads with the symptom, because that is what you arrive with.

- **A C# run reports far fewer tests than you expect and still says `Passed!`.** You named one test
  project when you meant both. Name the solution instead. See [Which set of C# tests you just
  ran](#which-set-of-c-tests-you-just-ran).
- **A C# build stops with one sentence saying a Cove source checkout is required.** You named the
  Cove-dependent project, or the solution, with no checkout available. [Configuration
  reference](./configuration#cove-source-selection) has the knobs that point the build at one.
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
