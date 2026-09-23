using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Renamer.Contracts;
using Renamer.Options;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

public sealed class LibraryRenameSummaryTests
{
    private const string RunId = "run-under-test";

    // The catch that keeps a completed run completed when its counts cannot be stored.
    private const int SummaryNotStoredEvent = 1075;

    private static async Task<(global::Renamer.Renamer Ext, FakeStore Store)> NewExtensionAsync(
        SqliteConnection conn, Func<IExtensionStore, IExtensionStore>? wrap = null,
        ILogger<global::Renamer.Renamer>? log = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var contextOptions = new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn).Options;
            return new CoveContext(contextOptions, principalAccessor: null);
        });
        services.AddSingleton<IEventBus>(new CapturingEventBus());
        services.AddSingleton<IAuthorizationService>(new RecordingAuthorizationService());
        services.AddLibraryPaths();
        if (log is not null)
        {
            services.AddSingleton(log);
        }

        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        // Without the filename fallback an untitled item is missing its required title, so the planner
        // skips it.
        await new OptionsStore(store).SaveAsync(
            new RenamerOptions { FilenameTemplate = "$title", FilenameAsTitle = false });
        ((IStatefulExtension)ext).SetStore(wrap is null ? store : wrap(store));
        await ext.InitializeAsync(services.BuildServiceProvider());
        return (ext, store);
    }

    private static int StatusOf(IResult result) =>
        Assert.IsType<IStatusCodeHttpResult>(Unwrap(result), exactMatch: false).StatusCode ?? 0;

    private static LibraryRenameSummaryView ViewOf(IResult result) =>
        Assert.IsType<LibraryRenameSummaryView>(Assert.IsType<IValueHttpResult>(Unwrap(result), exactMatch: false).Value);

    private static FakePrincipalAccessor VideoReader => FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

    private static Task StoreAsync(FakeStore store, LibraryRenameSummary summary) =>
        store.SetAsync(
            global::Renamer.Renamer.LastLibraryRenameSummaryKey,
            JsonSerializer.Serialize(summary, PreviewContracts.PreviewResponseJsonOptions));

    private static async Task SeedThreeVideosAsync(DbContext db, TempDir dir)
    {
        string folder = dir.Root.Replace('\\', '/');
        (string Basename, string Title)[] videos = [("raw.mkv", "Alpha"), ("untitled.mkv", ""), ("Beta.mkv", "Beta")];
        await ExecutorTestSeed.SeedVideosAsync(db, videos.Length, i => (folder, videos[i].Basename, videos[i].Title));
        foreach (var (basename, _) in videos)
        {
            File.WriteAllText(Path.Combine(dir.Root, basename), "video-bytes");
        }
    }

    private static Task RunLibraryAsync(global::Renamer.Renamer ext) =>
        ext.RunRenamerLibraryJobAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite).Current,
            [RenamerFileKind.Video], new FakeJobProgress(), default, runId: RunId);

    [Fact]
    public async Task AWholeLibraryRun_ReportsWhatItRenamed_AndWhatThePlannerSkipped()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedThreeVideosAsync(db, dir);
            var (ext, _) = await NewExtensionAsync(conn);
            await RunLibraryAsync(ext);

            var view = ViewOf(await ext.LibraryRenameResultAsync(RunId, VideoReader, default));

            // Beta.mkv is already named correctly, so it is in none of the three counts.
            Assert.Equal(1, view.Renamed);
            Assert.Equal(1, view.Skipped);
            Assert.Equal(0, view.Failed);
            Assert.Empty(view.StoppedForSpace);
            Assert.Equal([RenamerFileKind.Video], view.Kinds);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARunThatCompletedAfterTheCallers_ReadsAsNotFound_NotAsTheCallersCounts()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, store) = await NewExtensionAsync(conn);
            await StoreAsync(store, new LibraryRenameSummary(
                LibraryRenameSummary.CurrentSchemaVersion, "a-later-run", 0,
                [new LibraryRenameKindTally(RenamerFileKind.Video, 9, 0, 0, StoppedForSpace: false)]));

            Assert.Equal(404, StatusOf(await ext.LibraryRenameResultAsync(RunId, VideoReader, default)));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASummaryThatCannotBeStored_LeavesTheRunCompleted_AndLogsOnce()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedThreeVideosAsync(db, dir);
            var log = new CapturingLogger<global::Renamer.Renamer>();
            var (ext, _) = await NewExtensionAsync(conn, inner => new FailingSummaryStore(inner), log);

            await RunLibraryAsync(ext);

            Assert.True(File.Exists(Path.Combine(dir.Root, "Alpha.mkv")), "the rename did not happen");
            Assert.Single(log.Entries, e => e.EventId == SummaryNotStoredEvent && e.Error is InvalidOperationException);
            Assert.Equal(404, StatusOf(await ext.LibraryRenameResultAsync(RunId, VideoReader, default)));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheReadback_SumsOnlyTheKindsTheCallerMayRead()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, store) = await NewExtensionAsync(conn);
            await StoreAsync(store, new LibraryRenameSummary(
                LibraryRenameSummary.CurrentSchemaVersion,
                RunId,
                42,
                [
                    new LibraryRenameKindTally(RenamerFileKind.Video, 5, 2, 1, StoppedForSpace: true),
                    new LibraryRenameKindTally(RenamerFileKind.Image, 30, 7, 0, StoppedForSpace: false),
                ]));

            var videoOnly = ViewOf(await ext.LibraryRenameResultAsync(RunId, VideoReader, default));
            Assert.Equal((5, 2, 1), (videoOnly.Renamed, videoOnly.Skipped, videoOnly.Failed));
            Assert.Equal([RenamerFileKind.Video], videoOnly.StoppedForSpace);
            Assert.Equal([RenamerFileKind.Video], videoOnly.Kinds);

            var both = ViewOf(await ext.LibraryRenameResultAsync(
                RunId, FakePrincipalAccessor.WithPermissions(Permissions.VideosRead, Permissions.ImagesRead), default));
            Assert.Equal((35, 9, 1), (both.Renamed, both.Skipped, both.Failed));
            Assert.Equal(42, both.CompletedAtUtcTicks);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task NoRunYet_AnUnreadableBlob_AndAnUnknownSchema_AllRead_AsNotFound()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, store) = await NewExtensionAsync(conn);

            Assert.Equal(404, StatusOf(await ext.LibraryRenameResultAsync(RunId, VideoReader, default)));

            await store.SetAsync(global::Renamer.Renamer.LastLibraryRenameSummaryKey, "{not json");
            Assert.Equal(404, StatusOf(await ext.LibraryRenameResultAsync(RunId, VideoReader, default)));

            await StoreAsync(store, new LibraryRenameSummary(99, RunId, 0, []));
            Assert.Equal(404, StatusOf(await ext.LibraryRenameResultAsync(RunId, VideoReader, default)));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ACallerHoldingNoReadPermission_IsRefused()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, _) = await NewExtensionAsync(conn);
            Assert.Equal(403, StatusOf(await ext.LibraryRenameResultAsync(
                RunId, FakePrincipalAccessor.WithPermissions(), default)));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public void TheEnqueue_ReturnsARunId_BesideTheJobId()
    {
        var ext = RenamerFixture.CreateWithStore();
        var accepted = Assert.IsType<IValueHttpResult>(
            Unwrap(ext.RenamerLibraryEnqueue(
                FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite), new RecordingJobService())),
            exactMatch: false);

        var enqueued = Assert.IsType<LibraryRenameEnqueued>(accepted.Value);
        Assert.Equal("job-123", enqueued.JobId);
        Assert.NotEqual("", enqueued.RunId);
    }

    private sealed class FailingSummaryStore(IExtensionStore inner) : IExtensionStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => inner.GetAsync(key, ct);

        public Task SetAsync(string key, string value, CancellationToken ct = default) =>
            key == global::Renamer.Renamer.LastLibraryRenameSummaryKey
                ? throw new InvalidOperationException("the summary write failed")
                : inner.SetAsync(key, value, ct);

        public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
            inner.GetAllAsync(ct);
    }
}
