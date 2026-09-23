# Renamer's C# tests

Renamer's backend suite is one xUnit project. Building it needs a Cove source checkout
(`CoveSourceMode=source`). Without one the build stops with an error that says how to point it at a
checkout.

The checkout is needed for `CoveContext`, which lives in `Cove.Data` and is on no package feed.
`CoveContextFactory` builds a real `CoveContext` and returns it as `DbContext`, the type the host
supplies at runtime. The tests need Cove's EF model, including the `(ParentFolderId, Basename)` unique
index, and Cove's `SaveChangesAsync` overrides, which derive every touched file's `Path`. In a new
test, take the context as `DbContext` and name `CoveContext` only where you construct one.

The folders mirror `../Renamer/`. `TestSupport/` and the folders named for what they exercise
(`Concurrency/`, `Preview/`, `Wire/`) have no source counterpart.

## Platform skips

Tests that depend on platform path semantics skip with a stated reason off that platform:

- `OperatingSystem.IsWindows()` gates case-insensitive paths, drive and UNC roots, mandatory locking,
  and backslash folder paths.
- `SecondVolume.IsAvailable` gates the cross-volume tests. Linux uses `/dev/shm` and Windows maps a
  `subst` drive.

On macOS, set `COVE_TEST_SECOND_VOLUME` to a directory on another filesystem, or the cross-volume
tests skip. A disk image works:

```sh
hdiutil create -size 200m -fs APFS -volname covetest2v /tmp/covetest2v.dmg
hdiutil attach /tmp/covetest2v.dmg
mkdir -p /Volumes/covetest2v/t
COVE_TEST_SECOND_VOLUME=/Volumes/covetest2v/t dotnet test --project extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj
```

A drive letter is machine-global. One cross-volume test unmaps its own drive mid-test, so every class
that creates a `SecondVolume` joins the `SubstDriveScope` collection, which runs them one at a time.

Which CI legs run this project: `website/docs/contributing/testing.md`.
