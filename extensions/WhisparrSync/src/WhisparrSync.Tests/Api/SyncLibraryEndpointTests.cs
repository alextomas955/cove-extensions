using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;
using IJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The <c>/sync-library</c> handler's stored-configuration refusal. This is the highest-blast-radius route in the
/// extension — one click carries one stored quality profile across a whole-library create fan-out — so the refusal
/// must happen in the handler, before the job exists at all, and the counting job service is what proves it.
/// The pure fan-out planner is covered by the sibling <see cref="SyncLibraryJobTests"/>.
/// </summary>
[Trait("Tier", "L2")]
public sealed class SyncLibraryEndpointTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";

    private static async Task<FakeStore> StoreWith(string baseUrl = StoredBaseUrl)
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options",
            $"{{\"BaseUrl\":\"{baseUrl}\",\"ApiKey\":\"{StoredKey}\",\"SelectedVersion\":\"v3\"}}");
        return store;
    }

    // An extension holding a job service, so an enqueue is observable. Without one the handler answers
    // JOB_SERVICE_UNAVAILABLE, which would mask whether the configuration guard ran at all.
    private static async Task<(Ext Ext, CountingJobService Jobs)> NewExtensionWithJobsAsync(
        FakeStore store, DbContext? db = null, FakeHttpMessageHandler? handler = null)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store);
        var jobs = new CountingJobService();
        var services = new ServiceCollection();
        services.AddSingleton<IJobService>(jobs);
        if (db is not null)
        {
            // The job opens its own scope and resolves both from it, so a run that actually reads the library
            // and reaches Whisparr needs each registered rather than falling back to the empty port.
            services.AddSingleton(db);
            services.AddSingleton(new WhisparrClient(new HttpClient(handler!)));
        }

        await ext.InitializeAsync(services.BuildServiceProvider());
        return (ext, jobs);
    }

    private static Ext NewExtensionWithoutJobs(FakeStore store)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store);
        return ext;
    }

    private static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWith()
    {
        var handler = FakeHttpMessageHandler.Json("[]");
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string ResponseJson(IResult result)
        => JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static FakePrincipalAccessor Configure()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncLibrary_WithBlankStoredAddress_Refuses400_WithTheAddressKey_BeforeEnqueue(bool alsoMonitor)
    {
        var (ext, jobs) = await NewExtensionWithJobsAsync(await StoreWith(baseUrl: ""));
        var (client, handler) = ClientWith();

        var result = await ext.SyncLibraryAsync(
            new SyncLibraryRequest(alsoMonitor, Scope: null), client, default);

        Assert.Equal(400, StatusOf(result));
        var json = ResponseJson(result);
        Assert.Contains("CONFIG_INCOMPLETE", json, StringComparison.Ordinal);
        Assert.Contains("baseUrl", json, StringComparison.Ordinal);
        // The load-bearing assertion: the job never existed, so the Job Drawer never shows a run that was doomed.
        Assert.Equal(0, jobs.EnqueueCount);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SyncLibrary_WithNoJobService_AndIncompleteConfiguration_GetsTheConfigurationOutcome()
    {
        // Both conditions hold; the user is told the actionable one. A missing job service is not something they
        // can fix, so the guard sits above that check.
        var ext = NewExtensionWithoutJobs(await StoreWith(baseUrl: ""));
        var (client, _) = ClientWith();

        var result = await ext.SyncLibraryAsync(
            new SyncLibraryRequest(AlsoMonitor: true, Scope: null), client, default);

        var json = ResponseJson(result);
        Assert.Contains("CONFIG_INCOMPLETE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("JOB_SERVICE_UNAVAILABLE", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncLibrary_WithCompleteConfiguration_EnqueuesTheJob()
    {
        var (ext, jobs) = await NewExtensionWithJobsAsync(await StoreWith());
        var (client, _) = ClientWith();

        var result = await ext.SyncLibraryAsync(
            new SyncLibraryRequest(AlsoMonitor: true, Scope: null), client, default);

        Assert.DoesNotContain("CONFIG_INCOMPLETE", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(1, jobs.EnqueueCount);
    }

    [Fact]
    public async Task SyncLibrary_RunsSliceUnits_PreRegisteringEveryIdTheBatchThenStarts()
    {
        const int eligible = 5;
        const int idLess = 2;
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            for (var i = 0; i < eligible; i++)
            {
                db.Set<Video>().Add(new Video
                {
                    Title = $"Scene {i}",
                    RemoteIds = { new VideoRemoteId { Endpoint = StashDbEndpoint, RemoteId = $"{StashUuidPrefix}{i:D2}" } },
                });
            }

            for (var i = 0; i < idLess; i++)
            {
                db.Set<Video>().Add(new Video { Title = $"No id {i}" });
            }

            await db.SaveChangesAsync();

            var handler = AddPathHandler();
            var (ext, jobs) = await NewExtensionWithJobsAsync(await StoreWith(), db, handler);

            await ext.SyncLibraryAsync(new SyncLibraryRequest(AlsoMonitor: false, Scope: null), new WhisparrClient(new HttpClient(handler)), default);
            Assert.Equal(1, jobs.EnqueueCount);

            var progress = new RecordingJobProgress();
            await jobs.Work!(progress, default);

            // The whole library is one slice at this size, so the run planned exactly one unit.
            var half = progress.Started.Count / 2;
            Assert.Equal(1, half);

            // Pre-registration writes every planned id, then the batch's own StartUnit writes the same ids in
            // the same order. If the two factories ever disagreed the sequences would differ here — and in the
            // drawer the denominator would double and the bar would cap at half.
            Assert.Equal(progress.Started.Take(half), progress.Started.Skip(half));
            Assert.StartsWith("scenes:1-", progress.Started[0], StringComparison.Ordinal);

            // One non-grabbing add per ELIGIBLE scene: the two carrying no connected id reach no wire at all.
            var adds = handler.Requests
                .Where(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Equal(eligible, adds.Count);
            Assert.All(adds, a => Assert.Contains("\"searchForMovie\":false", a.Body, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(eligible, adds.Select(a => a.Body).Distinct(StringComparer.Ordinal).Count());

            // No grab verb anywhere in the pass.
            Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private const string StashDbEndpoint = "https://stashdb.org/graphql";
    private const string StashUuidPrefix = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2";

    // Answers the add path's context reads and the per-scene create; everything else falls through to an empty
    // array, which is what an unconfigured Whisparr sub-resource looks like.
    private static FakeHttpMessageHandler AddPathHandler()
        => FakeHttpMessageHandler.Json("[]").Also(request =>
        {
            if (request.Method == HttpMethod.Post
                && request.Url.EndsWith("/api/v3/movie", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("{\"id\":1,\"title\":\"added\"}");
            }

            if (request.Url.Contains("/api/v3/rootfolder", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("[{\"id\":1,\"path\":\"/data/media\"}]");
            }

            if (request.Url.Contains("/api/v3/qualityprofile", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("[{\"id\":1,\"name\":\"Any\"}]");
            }

            if (request.Method == HttpMethod.Post && request.Url.Contains("/api/v3/tag", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("{\"id\":7,\"label\":\"cove-sync\"}");
            }

            return null;
        });

    private static HttpResponseMessage JsonResponse(string body)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    // Records every StartUnit id in call order, so pre-registration and the batch's own registration are two
    // halves of one sequence rather than an inference.
    private sealed class RecordingJobProgress : IJobProgress
    {
        private readonly List<string> _started = [];

        public List<string> Started => _started;

        public void Report(double progress, string? subTask = null)
        {
        }

        public IJobUnit StartUnit(string unitId, string? label = null)
        {
            _started.Add(unitId);
            return new NullJobUnit(unitId, label);
        }
    }

    // Counts enqueues so "the job was never created" is an assertion rather than an inference, and keeps the
    // work callback so a test can run the job the handler enqueued.
    private sealed class CountingJobService : IJobService
    {
        public int EnqueueCount { get; private set; }

        public Func<IJobProgress, CancellationToken, Task>? Work { get; private set; }

        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            EnqueueCount++;
            Work = work;
            return "fake-job-id";
        }

        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public JobInfo? GetJob(string jobId) => null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }
}
