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
with a reason when symlink creation lacks the OS privilege.

## Coverage guard

`TierTraitCoverageTests` (this project) calls the shared `TierTraitGuard` reflection helper and fails
if any xUnit test class in the assembly lacks a class-level Tier trait. An untagged class would
silently drop out of a `--filter "Tier=Lx"` selection, so presence is enforced by mechanism, not
trusted.

## The two CI legs

- **Bare-CI (cove-absent) leg** — a compile / pure **SMOKE**. With no `../cove` checkout on disk,
  every test that references a Cove _source_ type (`CoveContext`, `CovePrincipal`, host entities, …)
  is Compile-Removed by the `.csproj` (leaving the pure-core L0 tier), and the project compiles and
  runs against the NuGet `Cove.Plugins`. This leg proves the extension still builds and the pure tier
  passes without the host; it is **not** the safety gate.
- **Containerized e2e job** — the **required safety gate**. It stands up the Cove app image and drives
  the real rename/relocate flow, covering the behaviours the bare leg cannot see.
