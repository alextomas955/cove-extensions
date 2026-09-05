# Renamer's C# tests

Renamer's backend suite is one xUnit project. It references Cove's own source unconditionally, so
every test in it compiles and runs together, and building it needs a Cove **source** checkout
(`CoveSourceMode=source`). Without one the build stops with a single error naming the project and how
to point it at a checkout; there is no smaller set it falls back to.

The reason the checkout is needed is `CoveContext`, which lives in `Cove.Data`. That assembly is on no
package feed. Every other Cove assembly the suite touches, `Cove.Core` and `Cove.Plugins` included,
comes from the NuGet feed and would be available without a checkout.

What the tests need is the configured context rather than the type. `CoveContextFactory` constructs a
real `CoveContext` and hands it back as `DbContext`, which is what the host supplies at runtime and the
only type the extension's own code names. Two things a hand-rolled context over `Cove.Core` entities
would not carry are exactly what these tests are for: Cove's EF model, including the
`(ParentFolderId, Basename)` unique index, and Cove's `SaveChangesAsync` overrides, which derive every
touched file's `Path`. Take the context as `DbContext` in a new test, and name `CoveContext` only where
you construct one.

The folders mirror the source layers, so a folder here has a counterpart in `../Renamer/`. Read the
folder list off the project rather than from a list here. The directories with no source counterpart
are the test-only `TestSupport/` fakes plus the groups named for what they exercise instead of a layer.

## The platform skip gates

Some tests assert platform path semantics and gate with `Assert.SkipUnless`, so off the platform they
skip with a stated reason rather than failing. Two distinct gates are in play, and it is worth knowing
which one you hit:

- an `OperatingSystem.IsWindows()` gate for Windows-only semantics — case-insensitive paths, drive and
  UNC root classification, mandatory locking, backslash folder paths;
- a `SecondVolume.IsAvailable` gate for the cross-volume tests, which need a real second mount and skip
  with the reason that helper reports.

The cross-volume tests claim a `subst` drive letter on Windows, and an xUnit collection serializes the
tests that claim one. A collection is scoped to a single assembly, and this suite is a single assembly,
so that serialization covers every test that claims a letter.

## The CI legs that see this project

`build.yml` and `lint.yml` aggregate several jobs into required status checks. Read their `needs:`
lists for the current set; what matters here is which leg runs what.

- **The `test-cove-present` job** checks Cove out at each version on the workflow's version axis and
  runs the suite against each one. It is where to read whether the tests passed in CI.
- **The `windows-build-test` job** in `lint.yml` checks Cove out at the highest declared floor and runs
  the suite on Windows. It is the only leg that executes the Windows-gated cases.
- **The `build` job** builds and publishes the extension with `-p:CoveSourceMode=none` and runs no
  tests. What it proves is narrower and worth having: the shipped assembly compiles against the
  published Cove packages alone, which is the boundary Cove documents for an extension.
- **The `csharp-format` job** in `lint.yml` builds the whole solution in `source` mode with warnings as
  errors, so this project is inside the analyzer and format gates.
- **The containerized end-to-end job** drives the real rename and relocate flow against the Cove app
  image. It exercises the shipped extension rather than this project, and it is the required safety
  gate.

For how this tier sits against the others, see the repo's Testing guide at
`website/docs/contributing/testing.md`.
