# WhisparrSync.Tests

xUnit test suite for the Whisparr Sync extension. Tests mirror the source slices
(`Adapters/ · Ingest/ · Matching/ · Monitor/ · Push/ · SceneStatus/ · State/ · …`); the only
directories outside that mirror are the test-only `TestSupport/` fakes and the `Api/` endpoint group
(whose only subdirectories are `Auth/` and `Batch/`). The in-process transport smoke is a single file,
`Api/TransportSmokeTests.cs` — there is no `Api/TransportSmoke` directory.

## Tier taxonomy

Every test class carries exactly one class-level `[Trait("Tier", "Lx")]`. The tier is a *design*
fact — what a test depends on — kept independent of the *build* fact (whether the file is
Compile-Removed on the cove-absent leg).

| Tier | What it exercises | Dependencies |
| ---- | ----------------- | ------------ |
| **L0** | Pure logic and wire/adapter behaviour over `Fake*` doubles (`FakeHttpMessageHandler`, `FakeStore`, `FakeCoveLibraryPort`) | none — runs anywhere, no host, no disk, no network |
| **L1** | Host-double integration: a real `CoveContext` on SQLite-in-memory (`CoveContextFactory`), a `CovePrincipal`/`FakePrincipalAccessor`, `FakeScanService`, or local disk | in-process only |
| **L2** | In-process endpoint: the `NewExtension` harness invoking permission-gated minimal-API handlers, plus the `TestServer` transport smoke | in-process minimal API |
| **L3** | Live-instance / containerized e2e: `Adapters/V2LiveE2ETests` — the only L3-tagged class in the assembly — whose `[SkippableFact]`s are gated on `WHISPARR_V2_E2E_URL` + `WHISPARR_V2_E2E_KEY` (the `WHISPARR_V2_URL`/`WHISPARR_V2_KEY` aliases are also honored), so they skip with a visible reason without a reachable v2. `Adapters/V2OutwardParityTests` is **not** here — it is `L0`, faked end to end, and reaches no live Whisparr | a live Whisparr / the containerized e2e harness |

Run a single tier in isolation:

```sh
dotnet test extensions/WhisparrSync/src/WhisparrSync.Tests --filter "Tier=L0"
```

## Coverage guard

`TierTraitCoverageTests` (this project) calls the shared `TierTraitGuard` reflection helper and fails
if any xUnit test class in the assembly lacks a class-level Tier trait. An untagged class would
silently drop out of a `--filter "Tier=Lx"` selection, so presence is enforced by mechanism, not
trusted.

## The two CI legs

- **Bare-CI (cove-absent) leg** — a compile / pure **SMOKE**. With no `../cove` checkout on disk,
  every test that references a Cove *source* type (`CoveContext`, `CovePrincipal`, `IScanService`, …)
  is Compile-Removed by the `.csproj` (leaving L0 and the host-free local-disk cases), and the project
  compiles and runs against the NuGet `Cove.Plugins`. This leg proves the extension still builds and
  the pure tier passes without the host; it is **not** the safety gate.
- **Containerized e2e job** — the **required safety gate**. It stands up the Cove app image plus
  Whisparr and drives the real transport, so the behaviours the bare leg cannot see (the DB principal
  path, the live wire) are covered where it counts.
