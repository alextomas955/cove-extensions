---
sidebar_position: 1
---

# Get a first build

This page takes a fresh clone to one build you have checked, and stops there. For the day-to-day
loop read [Development](./development), and for the test tiers read [Testing](./testing).

## Prerequisites

- **The .NET SDK.** `global.json` at the repo root pins the version and how far an installed SDK may
  roll forward from it. Read both values there and install an SDK that satisfies them. Every
  `dotnet` command in this repo resolves through that pin, so a missing match fails on the first one
  you run rather than later.
- **Node.js.** The root `package.json` declares the minimum in `engines.node` and the exact version
  Volta switches to in `volta.node`. `extensions/Renamer/src/Renamer.Ui/package.json` carries its
  own Volta pin and `website/package.json` its own minimum; install a version that satisfies all
  three. If you use Volta, it reads the nearest pin and switches for you as you change directory.
- **Git.**

Docker is needed only for the containerized end-to-end suite, which [Testing](./testing) covers. A
first build does not need it.

## The optional Cove checkout

An extension compiles against the Cove host, and you do not need Cove's source to build. With no
checkout present the build restores the Cove SDK from NuGet, which is the clean-clone path. Keep a
checkout when you want the whole C# suite to compile, or a build that matches the host you run
locally.

Three things differ:

- **Where the Cove assemblies come from.** Without a checkout they are restored packages. With one,
  the Cove projects compile from your checkout and appear by name in the build output.
- **Whether the C# suite builds at all.** The tests need a real `CoveContext`, so without a checkout
  the build stops with one error naming the project and how to point it at one. There is no smaller
  set it falls back to. The extension itself builds and publishes without a checkout.
- **Whether the host-import check can run.** It reads a generated shim out of the checkout, so with
  none present it reports that it skipped and names the file it looked for. That is expected, not a
  failure.

To use a checkout, place it as a `cove` sibling of the monorepo root, or point `COVE_REPO` at it. A
checkout anywhere else with no variable set is not found, and the build takes the no-checkout path
without complaining.

Read which source the build selected rather than inferring it from your directory layout. Every
build prints one line naming the resolved mode and the absolute root, ahead of compiling anything,
and that line is the answer. For the property query that answers the same question without a build,
and for the full precedence, see
[Configuration reference](./configuration#cove-source-selection). Why the wiring is shaped this way
is in [Monorepo architecture](./architecture).

## Build the monorepo

Run every step from the repo root unless the step says otherwise.

1. Build the C# side:

   ```sh
   dotnet build CoveExtensions.slnx
   ```

2. Install the root tooling and generate the wire types. An extension UI's wire types are derived
   from a committed OpenAPI document and are gitignored, so a fresh clone has none and a frontend
   command fails on a missing module. Generate them before the first frontend command:

   ```sh
   npm ci --no-workspaces
   npm run generate:wire
   ```

3. Install the frontend package. The UI projects and `website/` are not npm workspaces, so the root
   install does not reach them; each carries its own lockfile and needs its own install in its own
   directory. For Renamer's panel:

   ```sh
   cd extensions/Renamer/src/Renamer.Ui
   npm ci
   ```

4. Verify the frontend, still in that directory:

   ```sh
   npm run verify
   ```

## Confirm the build

Check each of these, not just the exit status:

- `dotnet build` ends with `Build succeeded` and no errors. Any compiler or analyzer warning is an
  error here, so a build that succeeded is also a build with nothing reported.
- The `Cove:` line names the mode and root you intended. With a checkout, the Cove projects appear
  in the output as projects that were built.
- `npm run generate:wire` names the file it generated for each catalog entry it examined. If it
  names none, a frontend command still fails on the missing module.
- `npm run verify` ends with the frontend test summary. It chains several checks into one pass, so
  it is green only when every one of them passed.

## Where to go next

- [Development](./development) for the day-to-day loop and the checks that gate a merge.
- [Testing](./testing) for the test tiers and how to run each of them.
- [Extension authoring patterns](./authoring-patterns) for the shape rules a new module follows.
- [`CONTRIBUTING.md`](https://github.com/alextomas955/cove-extensions/blob/main/CONTRIBUTING.md) at
  the repo root for the contribution contract, including what a pull request is expected to verify
  and the docs that ship with a change.
