# Renamer.Tests

xUnit test suite for the Renamer extension. Tests mirror the source layers
(`Engine/ · Planner/ · Execution/{…}/ · Options/ · Jobs/ · Api/`); the only directories outside that
mirror are the test-only `TestSupport/` fakes and the `Api/TransportSmoke` in-process transport smoke.

## Tier taxonomy

Every test class carries exactly one class-level `[Trait("Tier", "Lx")]`. The tier is a _design_
fact — what a test depends on — kept independent of the _build_ fact (whether the file is
Compile-Removed on the cove-absent leg).

| Tier   | What it exercises                                                                                                                                                                           | Dependencies                                       |
| ------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------- |
| **L0** | Pure logic over the `Engine/`/`Planner/` and `Fake*` doubles (`FakeRenamerDataPort`, `FakeStore`) — tokenizing, templating, routing, path-confinement, plan purity                          | none — runs anywhere, no host, no disk, no network |
| **L1** | Host-double integration: a real `CoveContext` on SQLite-in-memory / EF-InMemory, local disk moves (`TempDir`, cross-volume, canonical-guard, undo/rollback), the auto-renamer event harness | in-process only                                    |
| **L2** | In-process endpoint: the `NewExtension` harness invoking permission-gated minimal-API handlers (preview / scan-library / undo / list-entities), plus the `TestServer` transport smoke       | in-process minimal API                             |
| **L3** | Live-instance / containerized end-to-end                                                                                                                                                    | **permanently empty here** — see below             |

Run a single tier in isolation:

```sh
dotnet test --project extensions/Renamer/src/Renamer.Tests -- --filter-trait "Tier=L0"
```

**The L3 row is a slot this suite never fills, and that is the design rather than a gap.**
Containerized end-to-end is the Playwright harness at [`tests/e2e/`](../../../../tests/e2e), which
boots a real Cove instance in Docker — something an xUnit process here has no way to stand up. That
suite is the L3 gate; a behavior needing L3 gets an e2e spec, never a fourth trait value. Asking this project for that tier selects nothing.

Some L0/L1 path cases assert semantics only the Windows filesystem has — DOS-device, UNC and
extended-length prefixes, 8.3 short names, junction and symlink canonicalization. None of them is
expected to _fail_ anywhere. Each calls `Assert.SkipUnless`, naming the facility it needs, so off that filesystem it skips visibly with a reason and the run stays green: the Linux
cove-present leg reports zero failures. The symlink case skips the same way when symlink creation
lacks the OS privilege, which is a permission fact rather than a filesystem one. Where a case mixes a
filesystem-independent invariant with a Windows-specific message, split it: the fail-closed
canonical-path guard asserts the rejection everywhere and gates only the reason wording, so a real
regression cannot hide behind a skip.

The reverse mistake — gating a case on an OS when the invariant is universal — is the more dangerous
one, because it hides a defect instead of merely under-running. A case-only rename (`movie.mkv` →
`Movie.mkv`) must land as a clean rename on every filesystem, so it runs un-gated even though two
different mechanisms reach that outcome: where the volume folds case, the case-variant target exists
and `PathOps.PathsEqual` recognizes it as the source's own slot; where the volume is case-sensitive,
it does not exist and the collision path is never entered. `PathOps` is the seam that carries the
distinction, and it folds case on both case-folding defaults. Gating that case on Windows is exactly
what hid the macOS defect it was written to catch. Ask what the **filesystem** does, never which OS
is primary.

The mirror image also matters, and used to go unnoticed: `VolumeClassifierTests` carries Unix-only
cases that skip on Windows. They are compiled onto the bare leg and so execute on the Linux CI runner —
before that they were skipped on the only host that compiled them and Compile-Removed on the only host
that could run them, which is to say asserted nowhere.

## macOS: cross-volume tests

The cross-volume tests need a second filesystem. `SecondVolume` finds one by itself on Windows (a
`subst` drive) and on Linux (`/dev/shm`), but macOS has neither, so they skip until you supply one
through `COVE_TEST_SECOND_VOLUME`. A RAM disk is the cheapest way, and needs no `sudo`:

```sh
dev=$(hdiutil attach -nomount ram://2097152)      # 1 GiB
diskutil erasevolume APFS RenamerTestVol "$dev"
export COVE_TEST_SECOND_VOLUME=/Volumes/RenamerTestVol
dotnet test extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj
diskutil eject "$dev"                              # when done
```

The override takes precedence over the inferred arms on every OS, so it is also how you point a run
at a real second disk. Pointing it at a directory on the volume the temp tree already lives on
throws rather than falling back — otherwise every gated test would pass while quietly exercising the
same-volume path. The fixture deliberately does not run `hdiutil` itself: it is constructed per test,
so attaching and detaching a volume a dozen times would cost seconds each and leak a mounted image
whenever a run crashed.

## Coverage guard

`TierTraitCoverageTests` (this project) calls the shared `TierTraitGuard` reflection helper and fails
if any xUnit test class in the assembly lacks a class-level Tier trait. An untagged class would
silently drop out of a `--filter-trait "Tier=Lx"` selection, so presence is enforced by mechanism, not
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
- **Containerized e2e job** — the Playwright suite, standing up the Cove app image and driving the
  real rename/relocate flow. This is the L3 gate, covering behaviours neither C# leg can see.

The bare leg runs on Linux, Windows and macOS runners. That is how the filesystem-gated cases above
reach a host that satisfies them — the Windows-semantics cases execute on the Windows runner and skip
on the other two, the Unix-only ones the other way round — and it is why a skip census is read per
runner rather than compared across them. The macOS leg is bare permanently, not pending: the Cove
assemblies are extracted from a running Linux container, and a GitHub-hosted macOS runner has no
daemon that can run one.

A run that executes no test fails instead of passing green: the Microsoft Testing Platform requires at
least one test to run. That is the whole of "prove the suite ran", and it needs no maintained number,
so it cannot fall behind the suite.

Each leg used to compare its total against a hand-declared minimum on the `extensions/catalog.json`
entry. Those were removed: the number had to be raised by hand whenever tests were added, so it
silently fell behind and each gate weakened while still printing green. What they guarded is closed at
its source instead — a file can only leave a leg through an explicit per-file `Compile Remove` in
`Renamer.Tests.csproj`, and an incomplete extraction is refused by `AssertCoveLocation` plus the
extraction staleness guard rather than inferred afterwards from a count.
