# Renamer.Tests

xUnit test suite for the Renamer extension. Tests mirror the source layers, so a folder here has a
counterpart in `../Renamer/`. Read the folder list off the two projects rather than from a list here.
The directories with no source counterpart are the test-only `TestSupport/` fakes plus the groups named
for what they exercise instead of a layer, and `Api/TransportSmokeTests.cs` is the in-process transport
smoke.

Some tests assert platform path semantics and gate with `Assert.SkipUnless`, so off the platform they
skip with a stated reason rather than failing. Two distinct gates are in play, and it is worth knowing
which one you hit: an `OperatingSystem.IsWindows()` gate for Windows-only semantics (case-insensitive
paths, drive and UNC root classification, mandatory locking, backslash folder paths), and a
`SecondVolume.IsAvailable` gate for the cross-volume tests, which need a real second mount and skip
with the reason that helper reports.

**Do not read the Windows CI leg as coverage for those.** That leg builds with
`-p:CoveSourceMode=none`, and in that mode the `.csproj` Compile-Removes whole directories — including
the ones holding most of the Windows-gated path tests. So they compile and run on a Windows dev box
with a `../cove` checkout, and mostly do not exist on the Windows CI leg at all. Check which set you
actually ran rather than assuming; see the repo's Testing guide for how.

## The CI legs that see this suite

`build.yml` aggregates several jobs into one required status check. Read its `needs:` list for the
current set rather than a list here; what matters for this project is which leg runs which tests.

- **The cove-absent leg** — a compile / pure **SMOKE**. With no `../cove` checkout on disk, every test
  that references a Cove _source_ type (`CoveContext`, `CovePrincipal`, host entities, …) is
  Compile-Removed by the `.csproj`, leaving the pure core, which compiles and runs against the NuGet
  `Cove.Plugins`. It proves the extension still builds and the pure tests pass without the host. It is
  **not** the safety gate, and a green here says nothing about the removed directories.
- **The cove-present leg** — the only job that actually runs the tests needing a real `CoveContext`.
  If you want to know whether this project's host-dependent tests passed in CI, this is the leg to
  read; the end-to-end job never compiles them.
- **The containerized end-to-end job** — drives the real rename and relocate flow against the Cove app
  image, covering behaviour no unit tier can see. It exercises the shipped extension, not this test
  project.
- **The Windows leg** builds in the cove-absent mode, so the Windows-gated path tests above are mostly
  not compiled there. See the note at the top of this file.
