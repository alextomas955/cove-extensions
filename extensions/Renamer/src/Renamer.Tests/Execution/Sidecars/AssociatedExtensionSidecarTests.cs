using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Sidecars;

public sealed class AssociatedExtensionSidecarTests
{
    private static RenamerPlan RenamerPlan(int videoId, int fileId, string folderPath, string oldBasename, string newBasename)
        => new(videoId, RenamerFileKind.Video,
        [
            new RenamerPlanItem(fileId, folderPath + "/" + oldBasename, folderPath + "/" + newBasename,
                RenamerStatus.Rename, newBasename, folderPath),
        ]);

    private static RenamerExecutor RealExecutor(DbContext db)
        => new(new CoveRenamerDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test");

    [Fact]
    public async Task SameStemListedExtension_MovesAndTracksNewStem()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "Film A");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.srt"), "subs");

            var options = new RenamerOptions { AssociatedExtensions = ["srt"] };
            var result = await RealExecutor(db).ExecuteAsync(
                RenamerPlan(videoId, fileId, folderPath, "clip.mkv", "Film A.mkv"), options, default);

            Assert.Single(result.Renamed);
            Assert.Empty(result.Failed);

            Assert.False(File.Exists(Path.Combine(dir.Root, "clip.srt")), "old sidecar name must be gone");
            string newSidecar = Path.Combine(dir.Root, "Film A.srt");
            Assert.True(File.Exists(newSidecar), "the listed-extension sidecar must move to the new stem");
            Assert.Equal("subs", File.ReadAllText(newSidecar));

            Assert.True(File.Exists(Path.Combine(dir.Root, "Film A.mkv")), "primary moved");
            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("Film A.mkv", basename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task DifferentStemNeighbor_WithListedExtension_IsNotMoved()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "Film A");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            string neighbor = Path.Combine(dir.Root, "other.srt");
            File.WriteAllText(neighbor, "unrelated");

            var options = new RenamerOptions { AssociatedExtensions = ["srt"] };
            var result = await RealExecutor(db).ExecuteAsync(
                RenamerPlan(videoId, fileId, folderPath, "clip.mkv", "Film A.mkv"), options, default);

            Assert.Single(result.Renamed);
            Assert.True(File.Exists(neighbor), "a different-stem neighbor must be left at its source");
            Assert.Equal("unrelated", File.ReadAllText(neighbor));
            Assert.False(File.Exists(Path.Combine(dir.Root, "Film A.srt")), "no sidecar should be created for it");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameStemUnlistedExtension_IsNotMoved()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "Film A");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            string nfo = Path.Combine(dir.Root, "clip.nfo");
            File.WriteAllText(nfo, "metadata");

            var options = new RenamerOptions { AssociatedExtensions = ["srt"] };
            var result = await RealExecutor(db).ExecuteAsync(
                RenamerPlan(videoId, fileId, folderPath, "clip.mkv", "Film A.mkv"), options, default);

            Assert.Single(result.Renamed);
            Assert.True(File.Exists(nfo), "a same-stem file with an unlisted extension must be left at its source");
            Assert.False(File.Exists(Path.Combine(dir.Root, "Film A.nfo")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExtensionContainingPathSeparators_IsRejected_NoSidecarEscapesTheFolder()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "Film A");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            // A file whose name literally matches the malformed extension's leaf - present only to
            // prove the probe never even forms a path that could reach it.
            string traversal = Path.Combine(dir.Root, "clip...mp4");
            File.WriteAllText(traversal, "decoy");

            // A separator/parent-traversal extension must be refused outright, so no sidecar move is
            // ever built from it and nothing can escape the folder.
            var options = new RenamerOptions { AssociatedExtensions = ["srt/../../elsewhere", "..mp4"] };
            var result = await RealExecutor(db).ExecuteAsync(
                RenamerPlan(videoId, fileId, folderPath, "clip.mkv", "Film A.mkv"), options, default);

            Assert.Single(result.Renamed);
            Assert.True(File.Exists(traversal), "a malformed extension must move nothing");
            Assert.True(File.Exists(Path.Combine(dir.Root, "Film A.mkv")), "primary still moves");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task EmptyList_MovesCaptionsOnly_NoOtherNeighborTouched()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "Film A");

            db.Set<VideoCaption>().Add(new VideoCaption { FileId = fileId, Filename = "clip.en.vtt", LanguageCode = "en", CaptionType = "vtt" });
            await db.SaveChangesAsync();

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.en.vtt"), "caption");
            string neighbor = Path.Combine(dir.Root, "clip.srt");
            File.WriteAllText(neighbor, "subs");

            var result = await RealExecutor(db).ExecuteAsync(
                RenamerPlan(videoId, fileId, folderPath, "clip.mkv", "Film A.mkv"), new RenamerOptions(), default);

            Assert.Single(result.Renamed);
            // The DB-tracked caption still moves and renames.
            Assert.True(File.Exists(Path.Combine(dir.Root, "Film A.en.vtt")), "caption still moves with an empty list");
            Assert.False(File.Exists(Path.Combine(dir.Root, "clip.en.vtt")));
            // A non-caption neighbor is untouched because the extension list is empty.
            Assert.True(File.Exists(neighbor), "no extension discovery with an empty list");
            Assert.Equal("subs", File.ReadAllText(neighbor));
            Assert.False(File.Exists(Path.Combine(dir.Root, "Film A.srt")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task PreExistingSidecarTarget_IsSkippedNotClobbered_PrimaryStillMoves()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "Film A");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.srt"), "mine");
            string occupied = Path.Combine(dir.Root, "Film A.srt");
            File.WriteAllText(occupied, "theirs"); // pre-existing target

            var options = new RenamerOptions { AssociatedExtensions = ["srt"] };
            var result = await RealExecutor(db).ExecuteAsync(
                RenamerPlan(videoId, fileId, folderPath, "clip.mkv", "Film A.mkv"), options, default);

            Assert.Single(result.Renamed);
            Assert.True(File.Exists(Path.Combine(dir.Root, "Film A.mkv")), "primary still moves");
            // The pre-existing target is left untouched, and the source sidecar remains.
            Assert.Equal("theirs", File.ReadAllText(occupied));
            Assert.True(File.Exists(Path.Combine(dir.Root, "clip.srt")), "skipped sidecar source must remain");
            Assert.Equal("mine", File.ReadAllText(Path.Combine(dir.Root, "clip.srt")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExtensionMatchAlreadyTrackedAsCaption_IsNotMovedTwice()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "Film A");

            // A stem-only caption whose source path equals what the extension probe would build.
            db.Set<VideoCaption>().Add(new VideoCaption { FileId = fileId, Filename = "clip.vtt", LanguageCode = "en", CaptionType = "vtt" });
            await db.SaveChangesAsync();

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.vtt"), "caption");

            var options = new RenamerOptions { AssociatedExtensions = ["vtt"] };
            var result = await RealExecutor(db).ExecuteAsync(
                RenamerPlan(videoId, fileId, folderPath, "clip.mkv", "Film A.mkv"), options, default);

            Assert.Single(result.Renamed);
            Assert.Empty(result.Failed);
            // The file lands at the new stem exactly once, with its content intact.
            string moved = Path.Combine(dir.Root, "Film A.vtt");
            Assert.True(File.Exists(moved), "the caption lands at the new stem");
            Assert.Equal("caption", File.ReadAllText(moved));
            Assert.False(File.Exists(Path.Combine(dir.Root, "clip.vtt")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task InPlaceSameStemRename_WithListedSidecar_NoSpuriousWarning_SidecarStaysInPlace()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "clip");

            string primary = Path.Combine(dir.Root, "clip.mkv");
            string sidecar = Path.Combine(dir.Root, "clip.srt");
            File.WriteAllText(primary, "video");
            File.WriteAllText(sidecar, "subs");

            var options = new RenamerOptions { AssociatedExtensions = ["srt"] };
            // Same basename in and out: the primary is a no-op move and the sidecar's source == target.
            var result = await RealExecutor(db).ExecuteAsync(
                RenamerPlan(videoId, fileId, folderPath, "clip.mkv", "clip.mkv"), options, default);

            // No item failed, and no bucket carries a skip-not-clobber sidecar warning for the
            // source-equals-target sidecar the discovery guard suppressed.
            Assert.Empty(result.Failed);
            var allReasons = result.Renamed.Concat(result.Skipped).Concat(result.Failed)
                .Select(r => r.Reason)
                .Where(r => r is not null);
            Assert.DoesNotContain(allReasons, r => r!.Contains("sidecar", StringComparison.OrdinalIgnoreCase));

            Assert.True(File.Exists(sidecar), "the sidecar stays in place");
            Assert.Equal("subs", File.ReadAllText(sidecar));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(".srt")]
    [InlineData("SRT")]
    public async Task ExtensionNormalization_MatchesRegardlessOfDotOrCase(string configured)
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "Film A");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.srt"), "subs");

            var options = new RenamerOptions { AssociatedExtensions = [configured] };
            var result = await RealExecutor(db).ExecuteAsync(
                RenamerPlan(videoId, fileId, folderPath, "clip.mkv", "Film A.mkv"), options, default);

            Assert.Single(result.Renamed);
            Assert.Equal(["Film A.mkv", "Film A.srt"], NamesOnDisk(dir.Root));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // The moved file keeps the extension casing it had on disk rather than the casing in the setting.
    // Names are read from the directory listing, because File.Exists answers either casing on a
    // case-insensitive volume.
    [Theory]
    [InlineData("srt")]
    [InlineData(".srt")]
    [InlineData("SRT")]
    public async Task ExtensionNormalization_MatchesUpperCaseOnDisk_AndKeepsItsCasing(string configured)
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "Film A");

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.SRT"), "subs");

            var options = new RenamerOptions { AssociatedExtensions = [configured] };
            var result = await RealExecutor(db).ExecuteAsync(
                RenamerPlan(videoId, fileId, folderPath, "clip.mkv", "Film A.mkv"), options, default);

            Assert.Single(result.Renamed);
            Assert.Equal(["Film A.SRT", "Film A.mkv"], NamesOnDisk(dir.Root));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private static string[] NamesOnDisk(string directory) =>
        [.. Directory.EnumerateFiles(directory).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];
}
