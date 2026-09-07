using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// That the slice partition actually visits every scene once, proven where an off-by-one can hide: against a
/// real SQLite <c>CoveContext</c> through the real <c>CoveLibraryPort</c>, and at the outbound boundary by the
/// adds a sliced run actually issues.
/// </summary>
/// <remarks>
/// The half-open range is a claim about a SQL comparison, so a fake port cannot prove it — a fake would agree
/// with whatever predicate the test author had in mind rather than with the one that ships. The seeded library
/// is deliberately GAPPY, because contiguous ids make a boundary error invisible: with no gaps, an upper bound
/// that is off by one still lands on a row the next slice also claims, and the visited SET stays complete.
/// </remarks>
[Trait("Tier", "L1")]
public sealed class SyncSliceCoverageTests
{
    private const string StashDbEndpoint = "https://stashdb.org/graphql";

    private static WhisparrOptions Options() => new()
    {
        BaseUrl = "http://stored.local:6969",
        ApiKey = "STORED-KEY",
        SelectedVersion = "v3",
        StashDbEndpoint = StashDbEndpoint,
    };

    private static CoveLibraryPort PortOver(DbContext db)
        => new(db, StashDbEndpoint, "https://theporndb.net/graphql");

    // Seeds a library whose ids carry gaps, by inserting a run and deleting a scattered subset of it — the
    // shape deletions leave behind. Every third row is left WITHOUT a connected id, so eligibility and
    // coverage are separable: a slice must visit those rows and decline to register them.
    private static async Task<(IReadOnlyList<int> AllIds, IReadOnlyList<int> EligibleIds)> SeedGappyAsync(
        DbContext db, int inserted)
    {
        for (var i = 0; i < inserted; i++)
        {
            var video = new Video { Title = $"Scene {i}" };
            if (i % 3 != 2)
            {
                video.RemoteIds.Add(new VideoRemoteId { Endpoint = StashDbEndpoint, RemoteId = $"stash-{i:D5}" });
            }

            db.Set<Video>().Add(video);
        }

        await db.SaveChangesAsync();

        var seeded = await db.Set<Video>().AsNoTracking().OrderBy(v => v.Id).Select(v => v.Id).ToListAsync();
        var doomed = seeded.Where((_, index) => index % 7 == 3).ToList();
        db.Set<Video>().RemoveRange(db.Set<Video>().Where(v => doomed.Contains(v.Id)));
        await db.SaveChangesAsync();

        var remaining = await db.Set<Video>()
            .AsNoTracking().Include(v => v.RemoteIds).OrderBy(v => v.Id)
            .ToListAsync();
        return (
            [.. remaining.Select(v => v.Id)],
            [.. remaining.Where(v => v.RemoteIds.Count > 0).Select(v => v.Id)]);
    }

    // Exactly what the job does between the count and the plan.
    private static async Task<IReadOnlyList<Ext.SyncSlice>> PlanFromPortAsync(CoveLibraryPort library)
    {
        var count = await library.CountVideosAsync();
        var wanted = Ext.SceneCutOrdinals(count);
        var cutIds = new List<int>();
        var ordinal = 0;
        await foreach (var id in library.StreamVideoIdsAsync())
        {
            ordinal++;
            if (cutIds.Count < wanted.Count && ordinal == wanted[cutIds.Count])
            {
                cutIds.Add(id);
            }
        }

        return Ext.PlanSceneSlices(count, cutIds);
    }

    [Fact]
    public async Task Every_seeded_scene_is_visited_exactly_once_across_the_slices()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (allIds, _) = await SeedGappyAsync(db, 3_000);
            var library = PortOver(db);
            var slices = await PlanFromPortAsync(library);
            Assert.True(slices.Count > 2, $"expected several slices, planned {slices.Count}");

            var visited = new List<int>();
            foreach (var slice in slices)
            {
                await foreach (var video in library.StreamVideosInRangeAsync(slice.AfterCoveId, slice.UpToCoveId))
                {
                    visited.Add(video.CoveId);
                }
            }

            // The two assertions fail differently on purpose: a GAP shows up as a set that is missing an id, a
            // DOUBLE VISIT as a count that exceeds the distinct count. One assertion alone would let the other
            // defect through.
            Assert.Equal([.. allIds], [.. visited.Order()]);
            Assert.Equal(visited.Distinct().Count(), visited.Count);

            // The ids sitting ON the boundaries are the ones an off-by-one moves; each is visited once.
            foreach (var boundary in slices.Select(s => s.UpToCoveId).Where(id => id != int.MaxValue))
            {
                Assert.Equal(1, visited.Count(id => id == boundary));
            }
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_range_read_projects_a_scene_identically_to_the_whole_library_stream()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedGappyAsync(db, 600);
            var library = PortOver(db);

            var whole = new List<CoveVideo>();
            await foreach (var video in library.StreamAllVideosAsync())
            {
                whole.Add(video);
            }

            var ranged = new List<CoveVideo>();
            foreach (var slice in await PlanFromPortAsync(library))
            {
                await foreach (var video in library.StreamVideosInRangeAsync(slice.AfterCoveId, slice.UpToCoveId))
                {
                    ranged.Add(video);
                }
            }

            // Compared field by field rather than by record equality: CoveVideo's members are collections, whose
            // equality is by REFERENCE, so two identically-projected scenes are never equal as records.
            Assert.Equal([.. whole.Select(Canonical)], [.. ranged.Select(Canonical)]);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_sliced_run_issues_one_non_grabbing_add_per_eligible_scene_and_none_twice()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, eligibleIds) = await SeedGappyAsync(db, 900);
            var library = PortOver(db);
            var handler = SliceAddHandler();
            var client = new WhisparrClient(new HttpClient(handler));
            var options = Options();
            var actions = SceneActionsFactory.Build(client, options, library, SceneActionsFactory.MemoizedPort(client));
            var tally = new Ext.SyncTally();

            var slices = await PlanFromPortAsync(library);
            foreach (var slice in slices)
            {
                await Ext.DispatchSceneSliceAsync(
                    slice, useTpdb: false, sceneCount: 900, actions, library, new RecordingJobUnit(), tally, default);
            }

            // A coverage claim proven only against an internal list is a claim about the list. This one is
            // proven by what actually left the process.
            var addedIds = handler.Requests
                .Where(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Body!)
                .ToList();
            Assert.Equal(eligibleIds.Count, addedIds.Count);
            Assert.Equal(addedIds.Count, addedIds.Distinct(StringComparer.Ordinal).Count());
            Assert.All(addedIds, body => Assert.Contains("\"searchForMovie\":false", body, StringComparison.OrdinalIgnoreCase));

            // The scenes carrying no connected id were visited and declined, not silently dropped from the pass.
            Assert.Equal(eligibleIds.Count, tally.Succeeded);
            Assert.Equal(900 - eligibleIds.Count - (900 - await library.CountVideosAsync()), tally.Skipped);
            Assert.Equal(0, tally.Failed);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_slice_reports_at_most_once_per_streamed_page()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedGappyAsync(db, 2_400);
            var library = PortOver(db);
            var handler = SliceAddHandler();
            var client = new WhisparrClient(new HttpClient(handler));
            var actions = SceneActionsFactory.Build(client, Options(), library, SceneActionsFactory.MemoizedPort(client));
            var scenes = await library.CountVideosAsync();
            var jobUnit = new RecordingJobUnit();

            // One slice over the whole library, so the report count is about the range read's paging rather
            // than about how the library happened to be divided.
            await Ext.DispatchSceneSliceAsync(
                new Ext.SyncSlice(0, int.MaxValue, 1, scenes), useTpdb: false, scenes, actions, library, jobUnit,
                new Ext.SyncTally(), default);

            // Report re-counts the job's whole unit dictionary under a host-wide lock, so a future reader who
            // "improves" this to per-scene reporting reintroduces the cost the slicing exists to remove — and
            // stalls every other job on the instance while it does. The ceiling is one report per page plus the
            // closing one.
            var pages = ((scenes + CoveLibraryPort.StreamPageSize) - 1) / CoveLibraryPort.StreamPageSize;
            Assert.True(
                jobUnit.Reports.Count <= pages + 1,
                $"{jobUnit.Reports.Count} reports for {scenes} scenes exceeds the {pages + 1} allowed");
            Assert.NotEmpty(jobUnit.Reports);
            Assert.All(jobUnit.Reports, r => Assert.StartsWith("Scene ", r.Message!, StringComparison.Ordinal));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true, false, BatchUnitOutcomeName.Failed)]
    [InlineData(false, true, BatchUnitOutcomeName.Succeeded)]
    [InlineData(false, false, BatchUnitOutcomeName.Skipped)]
    public async Task A_slice_folds_its_scenes_into_one_outcome(bool anyFailure, bool anyEligible, BatchUnitOutcomeName expected)
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var video = new Video { Title = $"Scene {i}" };
                if (anyEligible || anyFailure)
                {
                    video.RemoteIds.Add(new VideoRemoteId { Endpoint = StashDbEndpoint, RemoteId = $"stash-{i:D3}" });
                }

                db.Set<Video>().Add(video);
            }

            await db.SaveChangesAsync();

            var library = PortOver(db);
            var handler = anyFailure ? FailingAddHandler() : SliceAddHandler();
            var client = new WhisparrClient(new HttpClient(handler));
            var actions = SceneActionsFactory.Build(client, Options(), library, SceneActionsFactory.MemoizedPort(client));
            var tally = new Ext.SyncTally();

            var outcome = await Ext.DispatchSceneSliceAsync(
                new Ext.SyncSlice(0, int.MaxValue, 1, 4), useTpdb: false, 4, actions, library, new RecordingJobUnit(),
                tally, default);

            Assert.Equal(expected.ToString(), outcome.ToString());
            if (anyFailure)
            {
                // A failure must not cut the pass short: every scene in the range was still attempted, so the
                // slice cannot silently leave part of its range unvisited.
                Assert.Equal(4, tally.Total);
            }
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_range_holding_no_scene_folds_to_skipped_and_reaches_no_wire()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            db.Set<Video>().Add(new Video { Title = "Only scene" });
            await db.SaveChangesAsync();

            var library = PortOver(db);
            var handler = SliceAddHandler();
            var client = new WhisparrClient(new HttpClient(handler));
            var actions = SceneActionsFactory.Build(client, Options(), library, SceneActionsFactory.MemoizedPort(client));

            var outcome = await Ext.DispatchSceneSliceAsync(
                new Ext.SyncSlice(9_000, 9_500, 1, 1), useTpdb: false, 1, actions, library, new RecordingJobUnit(),
                new Ext.SyncTally(), default);

            Assert.Equal("Skipped", outcome.ToString());
            Assert.Empty(handler.Requests);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>Mirrors <c>BatchUnitOutcome</c>, which is not visible to a theory's inline data.</summary>
    public enum BatchUnitOutcomeName
    {
        Succeeded,
        Failed,
        Skipped,
    }

    // Every field the run reads off a projected scene, flattened so the comparison is structural.
    private static string Canonical(CoveVideo video) => string.Join(
        '|',
        video.CoveId,
        video.Title,
        video.Date,
        string.Join(',', video.StashIds),
        string.Join(',', video.TpdbIds),
        string.Join(',', video.FilePaths),
        string.Join(',', video.Fingerprints.Select(f => $"{f.Type}={f.Value}")));

    private static FakeHttpMessageHandler SliceAddHandler()
        => FakeHttpMessageHandler.Json("[]").Also(request => AddContext(request)
            ?? (request.Method == HttpMethod.Post
                && request.Url.EndsWith("/api/v3/movie", StringComparison.OrdinalIgnoreCase)
                    ? Json(System.Net.HttpStatusCode.Created, "{\"id\":1,\"title\":\"added\"}")
                    : null));

    private static FakeHttpMessageHandler FailingAddHandler()
        => FakeHttpMessageHandler.Json("[]").Also(request => AddContext(request)
            ?? (request.Method == HttpMethod.Post
                && request.Url.EndsWith("/api/v3/movie", StringComparison.OrdinalIgnoreCase)
                    ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                    : null));

    private static HttpResponseMessage? AddContext(CapturedRequest request)
    {
        if (request.Url.Contains("/api/v3/rootfolder", StringComparison.OrdinalIgnoreCase))
        {
            return Json(System.Net.HttpStatusCode.OK, "[{\"id\":1,\"path\":\"/data/media\"}]");
        }

        if (request.Url.Contains("/api/v3/qualityprofile", StringComparison.OrdinalIgnoreCase))
        {
            return Json(System.Net.HttpStatusCode.OK, "[{\"id\":1,\"name\":\"Any\"}]");
        }

        return request.Method == HttpMethod.Post && request.Url.Contains("/api/v3/tag", StringComparison.OrdinalIgnoreCase)
            ? Json(System.Net.HttpStatusCode.OK, "{\"id\":7,\"label\":\"cove-sync\"}")
            : null;
    }

    private static HttpResponseMessage Json(System.Net.HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    // Captures every per-unit report so the throttle is a counted fact rather than an inference.
    private sealed class RecordingJobUnit : IJobUnit
    {
        public List<(double Progress, string? Message)> Reports { get; } = [];

        public JobUnitOutcome? Outcome { get; private set; }

        public void Report(double progress, string? message = null) => Reports.Add((progress, message));

        public void Complete(JobUnitOutcome outcome, string? message = null) => Outcome ??= outcome;

        public void Dispose()
        {
        }
    }
}
