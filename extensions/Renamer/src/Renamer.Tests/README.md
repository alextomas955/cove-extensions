# Renamer's C# tests

Renamer's backend suite is two xUnit projects that sit side by side:

| Project              | What it holds                                              | What it needs to build                               |
| -------------------- | ---------------------------------------------------------- | ---------------------------------------------------- |
| `Renamer.Tests`      | Everything that can be proven without a real `CoveContext` | Nothing beyond the repo and the published packages   |
| `Renamer.Cove.Tests` | Everything that needs a real `CoveContext`                 | A Cove **source** checkout (`CoveSourceMode=source`) |

Both mirror the source layers, so a folder in either has a counterpart in `../Renamer/`. Read the
folder list off the projects rather than from a list here. The directories with no source counterpart
are the test-only `TestSupport/` fakes plus the groups named for what they exercise instead of a
layer.

`Renamer.Cove.Tests` takes a project reference on `Renamer.Tests`, so the shared `TestSupport/`
helpers exist in one copy and both projects use them.

## Which project a test belongs in

One question decides it: **does the test need a real `CoveContext`?** That type comes from
`Cove.Data`, which resolves only from a Cove source checkout — every other Cove assembly the suite
touches, `Cove.Core` and `Cove.Plugins` included, comes from the NuGet feed and is available either
way.

- **Yes** — it belongs in `Renamer.Cove.Tests`.
- **No** — it belongs in `Renamer.Tests`.

Get the first direction wrong and the compiler stops you: `Renamer.Tests` holds no reference to
`Cove.Data`, so a test there that names `CoveContext` fails to build with `CS0234` whether or not a
checkout is on disk.

The other direction compiles. A test that needs no `CoveContext` builds fine inside
`Renamer.Cove.Tests`, and it then runs only on the leg that has a checkout. So when you write a test
that needs no host, put it in `Renamer.Tests` — that is what keeps it running on every leg.

## The platform skip gates

Some tests assert platform path semantics and gate with `Assert.SkipUnless`, so off the platform they
skip with a stated reason rather than failing. Two distinct gates are in play, and it is worth knowing
which one you hit:

- an `OperatingSystem.IsWindows()` gate for Windows-only semantics — case-insensitive paths, drive and
  UNC root classification, mandatory locking, backslash folder paths;
- a `SecondVolume.IsAvailable` gate for the cross-volume tests, which need a real second mount and skip
  with the reason that helper reports.

The Windows-gated path tests live in `Renamer.Tests`, and the Windows CI leg runs `Renamer.Tests`, so
that leg is coverage for them.

## Run the two projects one after another, not at once

The cross-volume tests claim a `subst` drive letter on Windows, and an xUnit collection serializes the
tests that claim one. A collection is scoped to a single assembly, so the two projects each declare
their own — which serializes within each project and not between them. Run them concurrently and one
project can reclaim a letter the other is still using.

CI runs them sequentially, and so should you on Windows. On other platforms the drive-letter cases
skip and the constraint does not apply.

## The CI legs that see these projects

`build.yml` and `lint.yml` aggregate several jobs into required status checks. Read their `needs:`
lists for the current set; what matters here is which leg runs which project.

- **The `build` job** runs `Renamer.Tests` with `-p:CoveSourceMode=none`. It proves the extension
  still builds and the whole no-host tier passes with no checkout present. It is a compile and
  pure-logic **SMOKE**, and it is **not** the safety gate — it says nothing about the tests that need
  a real `CoveContext`.
- **The `test-cove-present` job** checks out Cove at the version under test and runs **both**
  projects in `source` mode. If you want to know whether the host-dependent tests passed in CI, this
  is the leg to read.
- **The `windows-build-test` job** in `lint.yml` runs `Renamer.Tests` on Windows in `none` mode. That
  is where the Windows-gated path assertions actually execute.
- **The `csharp-format` job** in `lint.yml` builds the whole solution in `source` mode with warnings
  as errors, so both projects are inside the analyzer and format gates.
- **The containerized end-to-end job** drives the real rename and relocate flow against the Cove app
  image. It exercises the shipped extension rather than either of these projects, and it is the
  required safety gate.

For how this tier sits against the others, see the repo's Testing guide at
`website/docs/contributing/testing.md`.
