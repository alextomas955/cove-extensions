using System.Data.Common;
using System.Text;
using Cove.Core.Auth;
using Cove.Data;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

/// <summary>
/// Dry-run preview: <c>PreviewAsync</c> runs the planner over the seeded entity and returns
/// <see cref="RenamerPlanItem"/>[] (old→new + status) with ZERO mutation — proven by reading back
/// each seeded file's Basename/Path unchanged after the call. The handler is exercised as a plain
/// method (no HTTP host) with a real SQLite <c>CoveContext</c>.
/// </summary>
[Trait("Tier", "L2")]
public sealed class PreviewEndpointTests
{
    private static async Task<global::Renamer.Renamer> BuildExtensionAsync()
    {
        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        // This test exercises preview wire-shape + zero mutation, not the default template; pin the
        // title-only template in the store so the seeded (height-less) video renders a stable
        // "Title.ext" name independent of the shipped default (which would append "[$resolution]").
        await new OptionsStore(store).SaveAsync(new RenamerOptions { FilenameTemplate = "$title" });
        ((Cove.Plugins.IStatefulExtension)ext).SetStore(store);
        // PreviewAsync uses Store (OptionsStore) but not _scopeFactory/_eventBus; no Initialize needed.
        return ext;
    }

    /// <summary>Executes a result against a real response body and returns what it wrote, as UTF-8 text.</summary>
    private static async Task<string> BodyOfAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;
        await result.ExecuteAsync(ctx);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    [Fact]
    public async Task PreviewAsync_WithVideosRead_ReturnsPlanItems_AndMutatesNothing()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Preview probes the source on disk, so the seeded row needs a matching on-disk file for
            // the item to classify as a real Renamer (a gone source would be SkipMissingSource).
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath, "raw one.mkv", "First Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw one.mkv"), "video-bytes");
            var (beforeName, beforePath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            var ext = await BuildExtensionAsync();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            var item = Assert.Single(ok.Value!.Items);
            Assert.Equal(fileId, item.FileId);
            Assert.EndsWith("raw one.mkv", item.OldFullPath);
            Assert.Equal("First Film.mkv", item.NewBasename);
            Assert.Equal(RenamerStatus.Renamer, item.Status);

            // WIRE-SHAPE regression (the bug live-browser verification caught): the response MUST
            // serialize as camelCase with `status` the camelCase STRING "renamer" — NOT PascalCase,
            // NOT the numeric 0. The UI's confirm summary reads it.status === "renamer" and it.fileId; a
            // numeric enum or PascalCase key reads as a non-renamer and the renamer silently never
            // fires. Read the bytes the result actually WRITES, not a re-serialization: the options are
            // the result's own and a test that names its own instance would agree with itself forever.
            var json = await BodyOfAsync(Unwrap(result));
            Assert.Contains("\"status\":\"renamer\"", json);
            Assert.Contains("\"fileId\":", json);
            Assert.DoesNotContain("\"status\":0", json);
            Assert.DoesNotContain("\"Status\":", json);

            // Zero mutation: the seeded row is byte-for-byte unchanged after the preview.
            var (afterName, afterPath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal(beforeName, afterName);
            Assert.Equal(beforePath, afterPath);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task PreviewAsync_CoversEveryFileOfAMultiFileEntity()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (folderId, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, "/library/films", "part1.mkv", "Two Part Film");
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "part2.mkv");

            var ext = await BuildExtensionAsync();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            // one plan item per physical file of the entity, never just the first file.
            Assert.Equal(2, ok.Value!.Items.Count);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Several selected entities each get their own plan item — the fan-out, not just the first id.
    /// </summary>
    /// <remarks>
    /// Both other cases here pass a single-element id array, and so does every e2e that drives
    /// "Rename selected" (one `selectCard` call, no multi-select helper existed). So the N&gt;1 fan-out
    /// of the user's actual selection was unexercised at every tier — the same shape as issue #108,
    /// where a bulk edit renamed nothing because only the one-item path had ever been walked. A preview
    /// that answered for the first id alone would have satisfied every prior assertion, and it is what
    /// the confirm dialog counts before anything touches disk.
    /// </remarks>
    [Fact]
    public async Task PreviewAsync_WithSeveralEntityIds_ReturnsAnItemForEveryOne()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var ids = await SeedVideosAsync(db, dir, count: 4);

            var ext = await BuildExtensionAsync();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [.. ids]), db, principal, default);

            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));

            // Every seeded entity is represented, and by its OWN computed name — asserting only the
            // count would pass on four copies of the first id's answer.
            Assert.Equal(ids.Count, ok.Value!.Items.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                Assert.Contains(ok.Value.Items, item => item.NewBasename == $"Film {i}.mkv");
                Assert.Contains(
                    ok.Value.Items,
                    item => item.OldFullPath.EndsWith($"raw {i}.mkv", StringComparison.Ordinal));
            }
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Seeds <paramref name="count"/> single-file videos ("Film {i}" / "raw {i}.mkv") and returns their
    /// entity ids in seeded order.
    /// </summary>
    /// <remarks>
    /// One folder for all of them: <c>folders.Path</c> is unique, so a second <c>SeedVideoAsync</c>
    /// against the same path violates it. Same shape ParallelBatchTests uses to build a multi-entity
    /// batch. Each file is also written to disk because preview probes the source, and a gone source
    /// classifies as SkipMissingSource instead of a real rename.
    /// </remarks>
    private static async Task<List<int>> SeedVideosAsync(CoveContext db, TempDir dir, int count)
    {
        string folderPath = dir.Root.Replace('\\', '/');
        var (folderId, firstId, _) = await ExecutorTestSeed.SeedVideoAsync(
            db, folderPath, "raw 0.mkv", "Film 0");
        File.WriteAllText(Path.Combine(dir.Root, "raw 0.mkv"), "bytes-0");

        List<int> ids = [firstId];
        for (int i = 1; i < count; i++)
        {
            var video = new Cove.Core.Entities.Video { Title = $"Film {i}", Organized = true };
            db.Set<Cove.Core.Entities.Video>().Add(video);
            await db.SaveChangesAsync();
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, video.Id, $"raw {i}.mkv");
            File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
            ids.Add(video.Id);
        }

        return ids;
    }

    /// <summary>Records the SQL of every executed reader command, so a test can recognise one query by its own text.</summary>
    private sealed class ReaderTextRecorder : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            Texts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Texts.Add(command.CommandText);
            return result;
        }
    }

    /// <summary>
    /// A preview of N entities loads each of them ONCE — the blast-radius file sizes come off the entity
    /// the planner already loaded, never off a second identical multi-<c>Include</c> read per id.
    /// </summary>
    /// <remarks>
    /// The handler builds its own <c>CoveRenamerDataPort</c> from the <c>DbContext</c> it is handed, so
    /// there is no port seam to count calls on (unlike the batch path's <c>PhaseALoadOnceTests</c>). The
    /// count is taken at the database instead, and the per-load command count is CALIBRATED from a real
    /// <c>LoadEntityAsync</c> in this same test rather than assumed to be one: how many reader commands
    /// EF renders an Include chain into is EF's decision, and hard-coding today's answer would make this
    /// pin fail on a provider change that is not the regression it exists to catch.
    /// </remarks>
    [Fact]
    public async Task PreviewAsync_LoadsEachSelectedEntityExactlyOnce()
    {
        using var dir = new TempDir();
        var recorder = new ReaderTextRecorder();
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(connection)
                .AddInterceptors(recorder)
                .Options;
            await using var db = new CoveContext(options, principalAccessor: null);
            await db.Database.EnsureCreatedAsync();

            var ids = await SeedVideosAsync(db, dir, count: 3);

            // Calibrate against the real load: whatever SQL texts ONE LoadEntityAsync issues are the
            // texts an entity load is recognised by below, and how many it issues is the per-load cost.
            var port = new CoveRenamerDataPort(db);
            recorder.Texts.Clear();
            await port.LoadEntityAsync(RenamerFileKind.Video, ids[0]);
            var loadTexts = recorder.Texts.ToHashSet(StringComparer.Ordinal);
            int perLoad = recorder.Texts.Count;
            Assert.True(perLoad > 0, "calibration issued no reader command — the count below would be vacuous");

            var ext = await BuildExtensionAsync();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            recorder.Texts.Clear();
            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [.. ids]), db, principal, default);

            // Name the outcome: a 403 or a 400 would issue no load at all and satisfy a bare count.
            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            Assert.Equal(ids.Count, ok.Value!.Items.Count);

            int loadCommands = recorder.Texts.Count(text => loadTexts.Contains(text));
            Assert.Equal(perLoad * ids.Count, loadCommands);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
