# Renamer.Tests

xUnit test suite for the Renamer extension. Tests mirror the source layers
(`Engine/ · Planner/ · Execution/{…}/ · Options/ · Jobs/ · Api/`); the only directories outside that
mirror are the test-only `TestSupport/` fakes and the `Api/TransportSmoke` in-process transport smoke.

## Tier taxonomy

Every test class carries exactly one class-level `[Trait("Tier", "Lx")]`. The tier is a _design_
fact — what a test depends on — kept independent of the _build_ fact (whether the file is
Compile-Removed on the cove-absent leg).

| Tier   | What it exercises                                                                                                                                                                           | Dependencies                                              |
| ------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------- |
| **L0** | Pure logic over the `Engine/`/`Planner/` and `Fake*` doubles (`FakeRenamerDataPort`, `FakeStore`) — tokenizing, templating, routing, path-confinement, plan purity                          | none — runs anywhere, no host, no disk, no network        |
| **L1** | Host-double integration: a real `CoveContext` on SQLite-in-memory / EF-InMemory, local disk moves (`TempDir`, cross-volume, canonical-guard, undo/rollback), the auto-renamer event harness | in-process only                                           |
| **L2** | In-process endpoint: the `NewExtension` harness invoking permission-gated minimal-API handlers (preview / scan-library / undo / list-entities), plus the `TestServer` transport smoke       | in-process minimal API                                    |
| **L3** | Live-instance / containerized e2e                                                                                                                                                           | the containerized e2e harness (none live in this project) |

Run a single tier in isolation:

```sh
dotnet test extensions/Renamer/src/Renamer.Tests --filter "Tier=L0"
```

Some L0/L1 path tests assert Windows path semantics (DOS-device, UNC and extended-length prefixes,
8.3 short names, symlink/junction canonicalization) and are expected to fail on non-Windows hosts;
they pass on the Windows CI leg. The privilege-gated symlink case is a `[SkippableFact]` that skips
with a reason when symlink creation lacks the OS privilege. Where a case mixes a
platform-independent invariant with a Windows-specific message, split it: the fail-closed
canonical-path guard now asserts the rejection on both platforms and gates only the reason wording, so
a real regression cannot hide behind an expected non-Windows failure.

The mirror image also matters, and used to go unnoticed: `VolumeClassifierTests` carries Unix-only
cases that skip on Windows. They are compiled onto the bare leg and so execute on the Linux CI runner —
before that they were skipped on the only host that compiled them and Compile-Removed on the only host
that could run them, which is to say asserted nowhere.

## Coverage guard

`TierTraitCoverageTests` (this project) calls the shared `TierTraitGuard` reflection helper and fails
if any xUnit test class in the assembly lacks a class-level Tier trait. An untagged class would
silently drop out of a `--filter "Tier=Lx"` selection, so presence is enforced by mechanism, not
trusted.

## The CI legs

The two C# legs are split by **cove-dependence**, not by tier. Which leg a file lands on says nothing
about what tier it is.

- **Bare (cove-absent) leg** — every test that needs no Cove _source_ type, whatever its tier. With no
  Cove checkout and no extracted assemblies, the `.csproj` Compile-Removes the files that name
  `CoveContext` / `Cove.Data` (and, by convention, `Cove.Core`); the rest compile against the NuGet
  `Cove.Plugins` and run. Plenty of L1 work has no Cove dependency and belongs here — the canonical
  path guard, the cross-volume mover, the free-space preflight, locked files, sidecars, rollback. That
  is why removal is decided per file: deciding it per folder dropped those safety guards off this leg
  without saying so, and a leg that runs less than it could still reports green.
- **Cove-present leg** — adds back everything that needs `CoveContext`, compiling against the Cove
  assemblies extracted from the released `cove-app` image, so what it proves is a statement about the
  binaries users receive. This is where the C# safety tier gates a merge.
- **Containerized e2e job** — stands up the Cove app image and drives the real rename/relocate flow,
  covering behaviours neither C# leg can see.

A run that executes no test fails instead of passing green, through `TreatNoTestsAsError` in the
repo-root `.runsettings`. That is the whole of "prove the suite ran", and it needs no maintained
number, so it cannot fall behind the suite.

Each leg used to compare its total against a hand-declared minimum on the `extensions/catalog.json`
entry. Those were removed: the number had to be raised by hand whenever tests were added, so it
silently fell behind and each gate weakened while still printing green. What they guarded is closed at
its source instead — a file can only leave a leg through an explicit per-file `Compile Remove` in
`Renamer.Tests.csproj`, and an incomplete extraction is refused by `AssertCoveLocation` plus the
extraction staleness guard rather than inferred afterwards from a count.
