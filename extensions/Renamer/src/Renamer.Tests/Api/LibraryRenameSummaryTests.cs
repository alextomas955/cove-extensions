using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Contracts;
using Renamer.Options;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

public sealed class LibraryRenameSummaryTests
{
    private static async Task<(global::Renamer.Renamer Ext, FakeStore Store)> NewExtensionAsync(SqliteConnection conn)
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

        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        // Without the filename fallback an untitled item is missing its required title, so the planner
        // skips it.
        await new OptionsStore(store).SaveAsync(
            new RenamerOptions { FilenameTemplate = "$title", FilenameAsTitle = false });
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(services.BuildServiceProvider());
        return (ext, store);
    }

    private static int StatusOf(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

    private static LibraryRenameSummaryView ViewOf(IResult result) =>
        Assert.IsType<LibraryRenameSummaryView>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    private static Task StoreAsync(FakeStore store, LibraryRenameSummary summary) =>
        store.SetAsync(
            global::Renamer.Renamer.LastLibraryRenameSummaryKey,
            JsonSerializer.Serialize(summary, PreviewContracts.PreviewResponseJsonOptions));

    [Fact]
    public async Task AWholeLibraryRun_ReportsWhatItRenamed_AndWhatThePlannerSkipped()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folder = dir.Root.Replace('\\', '/');
            (string Basename, string Title)[] videos = [("raw.mkv", "Alpha"), ("untitled.mkv", ""), ("Beta.mkv", "Beta")];
            await ExecutorTestSeed.SeedVideosAsync(db, videos.Length, i => (folder, videos[i].Basename, videos[i].Title));
            foreach (var (basename, _) in videos)
            {
                File.WriteAllText(Path.Combine(dir.Root, basename), "video-bytes");
            }

            var (ext, _) = await NewExtensionAsync(conn);
            await ext.RunRenamerLibraryJobAsync(
                FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite).Current,
                [RenamerFileKind.Video], new FakeJobProgress(), default);

            var view = ViewOf(await ext.LibraryRenameResultAsync(
                FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), default));

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
    public async Task TheReadback_SumsOnlyTheKindsTheCallerMayRead()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, store) = await NewExtensionAsync(conn);
            await StoreAsync(store, new LibraryRenameSummary(
                LibraryRenameSummary.CurrentSchemaVersion,
                CompletedAtUtcTicks: 42,
                [
                    new LibraryRenameKindTally(RenamerFileKind.Video, 5, 2, 1, StoppedForSpace: true),
                    new LibraryRenameKindTally(RenamerFileKind.Image, 30, 7, 0, StoppedForSpace: false),
                ]));

            var videoOnly = ViewOf(await ext.LibraryRenameResultAsync(
                FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), default));
            Assert.Equal((5, 2, 1), (videoOnly.Renamed, videoOnly.Skipped, videoOnly.Failed));
            Assert.Equal([RenamerFileKind.Video], videoOnly.StoppedForSpace);
            Assert.Equal([RenamerFileKind.Video], videoOnly.Kinds);

            var both = ViewOf(await ext.LibraryRenameResultAsync(
                FakePrincipalAccessor.WithPermissions(Permissions.VideosRead, Permissions.ImagesRead), default));
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
            var reader = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            Assert.Equal(404, StatusOf(await ext.LibraryRenameResultAsync(reader, default)));

            await store.SetAsync(global::Renamer.Renamer.LastLibraryRenameSummaryKey, "{not json");
            Assert.Equal(404, StatusOf(await ext.LibraryRenameResultAsync(reader, default)));

            await StoreAsync(store, new LibraryRenameSummary(99, 0, []));
            Assert.Equal(404, StatusOf(await ext.LibraryRenameResultAsync(reader, default)));
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
                FakePrincipalAccessor.WithPermissions(), default)));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
