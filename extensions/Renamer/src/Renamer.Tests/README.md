# Renamer.Tests

xUnit test suite for the Renamer extension. Tests mirror the source layers
(`Engine/ · Planner/ · Execution/{…}/ · Options/ · Jobs/ · Api/`); the only directories outside that
mirror are the test-only `TestSupport/` fakes and the `Api/TransportSmoke` in-process transport smoke.

Some path tests assert Windows path semantics (DOS-device, UNC and extended-length prefixes,
8.3 short names, symlink/junction canonicalization). Each calls `Assert.SkipUnless`, so off Windows it
skips with a reason rather than failing, and it runs on the Windows CI leg. The symlink case gates on
the OS privilege symlink creation needs, so it can skip on Windows too.

## The two CI legs

- **Bare-CI (cove-absent) leg** — a compile / pure **SMOKE**. With no `../cove` checkout on disk,
  every test that references a Cove _source_ type (`CoveContext`, `CovePrincipal`, host entities, …)
  is Compile-Removed by the `.csproj` (leaving the pure core), and the project compiles and
  runs against the NuGet `Cove.Plugins`. This leg proves the extension still builds and the pure tests
  pass without the host; it is **not** the safety gate.
- **Containerized e2e job** — the **required safety gate**. It stands up the Cove app image and drives
  the real rename/relocate flow, covering the behaviours the bare leg cannot see.
