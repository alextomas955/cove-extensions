using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>One test server over one real relational library, with the outbound seam recorded.</summary>
/// <remarks>
/// The routes are the shipped ones, mapped by the shipped extension: a test calling a handler method
/// directly would agree with a route mounted at the wrong pattern, bound to a body the browser cannot
/// send, or reachable by a caller the declaration excludes.
/// <para>
/// One recorder for the whole outbound surface, so an ordered <see cref="RecordingWhisparrClient.Verbs"/>
/// list from any case covers every verb this product can issue rather than the ones one seam declares.
/// </para>
/// </remarks>
internal sealed class MonitorHost : IAsyncDisposable
{
    /// <summary>An identifier a studio is stored under in the newer generation's namespace.</summary>
    public const string StudioRemoteIdValue = "44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e";

    /// <summary>A second identifier in the same namespace, held by a performer rather than a studio.</summary>
    public const string PerformerRemoteIdValue = "9f0d6f27-1f3a-4a5f-8b21-6b2d3a5f9c10";

    /// <summary>The spelling the host stores a StashDB identity under, without a scheme.</summary>
    /// <remarks>
    /// Deliberately not the standard address this product prefers. The two name one source under the
    /// host's own rule, and a read comparing them as strings would answer that the entity carries no
    /// identity.
    /// </remarks>
    public const string StoredEndpoint = "stashdb.org/graphql";

    public const string StoredAddress = "http://whisparr-v3:6969";

    public const string StoredKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    /// <summary>Profiles as an instance offered them, in the order received and not in id order.</summary>
    public const string UnsortedProfiles = """[{"id":4,"name":"Any"},{"id":1,"name":"HD-1080p"}]""";

    public const string OneRootFolder = """[{"id":1,"path":"/config/library","accessible":true}]""";

    public const string AddedStudio =
        """{"id":1,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true}""";

    public const string AddedPerformer =
        """{"id":2,"foreignId":"9f0d6f27-1f3a-4a5f-8b21-6b2d3a5f9c10","monitored":true}""";

    private WebApplication _app = null!;
    private HttpClient? _http;
    private CoveContext _db = null!;
    private SqliteConnection _connection = null!;
    private int _seeded;

    public RecordingWhisparrClient Client { get; private set; } = null!;

    /// <summary>The folder source over this host's own library, as the routes resolve it.</summary>
    public IEntityFolderPort Folders { get; private set; } = null!;

    /// <summary>The scene-identity source over this host's own library, as the routes resolve it.</summary>
    public IEntitySceneIdentityPort SceneIdentities { get; private set; } = null!;

    /// <summary>The bytes that actually left, or null where this host stands the recorder instead.</summary>
    public BodyRecordingHandler? Bytes { get; private set; }

    /// <summary>The host job service the bulk route enqueues into.</summary>
    public RecordingJobService Jobs { get; } = new();

    public HttpClient Http { get; private set; } = null!;

    /// <summary>The id the shipped manifest declares, which every job type is prefixed with.</summary>
    public string ExtensionId { get; private set; } = null!;

    private string RouteBase { get; set; } = null!;

    /// <summary>
    /// Creates one host over one real library.
    /// </summary>
    /// <remarks>
    /// With <paramref name="bytes"/> supplied the SHIPPED client is stood over it instead of the
    /// recorder, so a case can read the request bodies that actually leave rather than the arguments
    /// a seam was handed. The two are different facts: what a call site supplied is not what the
    /// client composes from it, and the composed body is what an instance acts on.
    /// </remarks>
    public static async Task<MonitorHost> CreateAsync(
        FakePrincipalAccessor? principal = null,
        string? apiKey = StoredKey,
        WhisparrGeneration generation = WhisparrGeneration.V3,
        BodyRecordingHandler? bytes = null)
    {
        var host = new MonitorHost();
        (host._db, host._connection) = await CoveContextFactory.CreateSqliteContextAsync();

        // Every read answers 200 with an empty object unless a test queues something else, so a verb
        // no test named is still a recorded call rather than a throw.
        host.Client = new RecordingWhisparrClient(Json(200, "{}"))
            .Answering(nameof(IWhisparrClient.ReadQualityProfilesAsync), Json(200, UnsortedProfiles))
            .Answering(nameof(IWhisparrClient.ReadRootFoldersAsync), Json(200, OneRootFolder))
            .Answering(nameof(IWhisparrStudioActing.ReadStudioAsync), Json(404, ""))
            .Answering(nameof(IWhisparrStudioActing.AddMonitoredStudioAsync), Json(201, AddedStudio))
            .Answering(nameof(IWhisparrPerformerActing.ReadPerformerAsync), Json(404, ""))
            .Answering(
                nameof(IWhisparrPerformerActing.AddMonitoredPerformerAsync),
                Json(201, AddedPerformer));

        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions { SelectedGeneration = generation }.WithConnectionFor(
                generation, new WhisparrSyncGenerationConnection { Address = StoredAddress }),
            TestCt);

        var credentials = new RecordingCredentialPort();
        if (apiKey is not null)
        {
            credentials.Holding(generation, apiKey);
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWhisparrSyncBindingServices();
        builder.Services.AddRouting();

        // Both tiers, because the two routes declare different ones: the read is at the tier a caller
        // already needs to see the entity page, and the action at the configure tier.
        builder.Services.AddSingleton<ICurrentPrincipalAccessor>(
            principal ?? FakePrincipalAccessor.WithPermissions(
                Permissions.VideosRead, Permissions.ExtensionsConfigure));
        if (bytes is null)
        {
            builder.Services.AddSingleton<IWhisparrClient>(host.Client);
        }
        else
        {
            host.Bytes = bytes;
            host._http = new HttpClient(bytes);
            builder.Services.AddSingleton<IWhisparrClient>(
                new WhisparrClient(host._http, NullLogger.Instance));
        }

        builder.Services.AddSingleton<IJobService>(host.Jobs);
        builder.Services.AddSingleton<IEntityIdentityPort>(new EntityIdentityPort(host._db, options));
        host.Folders = new EntityFolderPort(host._db);
        builder.Services.AddSingleton(host.Folders);
        host.SceneIdentities = new EntitySceneIdentityPort(host._db, options);
        builder.Services.AddSingleton(host.SceneIdentities);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ICredentialPort>(credentials);

        host._app = builder.Build();
        var extension = WhisparrSyncFixture.Create();
        extension.MapEndpoints(host._app);
        host.ExtensionId = extension.Id;
        host.RouteBase = "/api/extensions/" + extension.Id;
        await host._app.StartAsync(TestCt);
        host.Http = host._app.GetTestClient();
        return host;
    }

    public static WhisparrResponse Json(int status, string body)
        => RecordingWhisparrClient.Json(status, body);

    public string RouteFor(string kind, int coveId, string verb)
        => string.Create(CultureInfo.InvariantCulture, $"{RouteBase}/entity/{kind}/{coveId}/{verb}");

    /// <summary>Seeds one studio, with an identity row when an endpoint is named.</summary>
    /// <remarks>
    /// The name is made unique per call. The host's own name key is unique, so a second studio seeded
    /// under one name fails the save rather than the assertion.
    /// </remarks>
    public async Task<int> SeedStudioAsync(string? endpoint, string? remoteId)
    {
        var name = "Studio " + (++_seeded).ToString(CultureInfo.InvariantCulture);
        var studio = new Studio { Name = name, NameKey = name.ToLowerInvariant() };
        _db.Add(studio);
        await _db.SaveChangesAsync(TestCt);

        if (endpoint is not null && remoteId is not null)
        {
            _db.Add(new StudioRemoteId
            {
                StudioId = studio.Id,
                Endpoint = endpoint,
                RemoteId = remoteId,
            });
            await _db.SaveChangesAsync(TestCt);
        }

        return studio.Id;
    }

    /// <summary>Adds one more identity row to a studio already seeded.</summary>
    /// <remarks>
    /// Exists so a case can hold two rows the host's same-source rule treats as one source, which is
    /// the shape a first-row pick would resolve silently.
    /// </remarks>
    public async Task AddStudioIdentityAsync(int studioId, string endpoint, string remoteId)
    {
        _db.Add(new StudioRemoteId
        {
            StudioId = studioId,
            Endpoint = endpoint,
            RemoteId = remoteId,
        });
        await _db.SaveChangesAsync(TestCt);
    }

    /// <summary>Seeds one performer, with an identity row when an endpoint is named.</summary>
    /// <remarks>
    /// Seeded through the same context as a studio, so a case can hold both kinds at once and a path
    /// reading the wrong identity table finds a row rather than nothing.
    /// </remarks>
    public async Task<int> SeedPerformerAsync(string? endpoint, string? remoteId)
    {
        var name = "Performer " + (++_seeded).ToString(CultureInfo.InvariantCulture);
        var performer = new Performer { Name = name, IdentityKey = name.ToLowerInvariant() };
        _db.Add(performer);
        await _db.SaveChangesAsync(TestCt);

        if (endpoint is not null && remoteId is not null)
        {
            _db.Add(new PerformerRemoteId
            {
                PerformerId = performer.Id,
                Endpoint = endpoint,
                RemoteId = remoteId,
            });
            await _db.SaveChangesAsync(TestCt);
        }

        return performer.Id;
    }

    /// <summary>Seeds one video file the studio <paramref name="studioId"/> names holds.</summary>
    /// <remarks>
    /// A folder already seeded at <paramref name="folderPath"/> is reused, so a case can put two of
    /// one entity's files in a single folder and see whether the folder is answered twice.
    /// </remarks>
    public Task SeedStudioFileAsync(int studioId, string folderPath)
        => SeedVideoFileAsync(folderPath, studioId, null);

    /// <summary>Seeds one video file linked to the performer <paramref name="performerId"/> names.</summary>
    /// <remarks>
    /// Linked through the join row rather than through the studio column: a performer's files reach
    /// them by a different table, so a port reading the studio column would answer nothing here.
    /// </remarks>
    public Task SeedPerformerFileAsync(int performerId, string folderPath)
        => SeedVideoFileAsync(folderPath, null, performerId);

    /// <summary>Seeds one scene the studio <paramref name="studioId"/> names holds.</summary>
    /// <remarks>
    /// A scene rather than a file: what the registration verb offers an instance is the identifier a
    /// video carries, and a video carries one whether or not the library holds a file for it.
    /// </remarks>
    public Task<int> SeedStudioSceneAsync(int studioId, string? endpoint, string? remoteId)
        => SeedSceneAsync(studioId, null, endpoint, remoteId);

    /// <summary>Seeds one scene linked to the performer <paramref name="performerId"/> names.</summary>
    /// <remarks>
    /// Linked through the join row rather than through the studio column, so a source reading the
    /// studio column answers nothing here.
    /// </remarks>
    public Task<int> SeedPerformerSceneAsync(int performerId, string? endpoint, string? remoteId)
        => SeedSceneAsync(null, performerId, endpoint, remoteId);

    /// <summary>Adds one more identity row to a scene already seeded.</summary>
    /// <remarks>
    /// Exists so a case can hold two rows the host's same-source rule treats as one source, which is
    /// the shape a database-side distinct on the raw pair cannot collapse.
    /// </remarks>
    public async Task AddSceneIdentityAsync(int videoId, string endpoint, string remoteId)
    {
        _db.Add(new VideoRemoteId
        {
            VideoId = videoId,
            Endpoint = endpoint,
            RemoteId = remoteId,
        });
        await _db.SaveChangesAsync(TestCt);
    }

    public Task<EntityMonitoringView> MonitorAsync(int studioId)
        => MonitorAsync("studio", studioId);

    public Task<EntityMonitoringView> MonitorAsync(string kind, int coveId)
        => MonitorRawAsync(kind, coveId, """{"scope":"futureScenes"}""");

    public Task<EntityMonitoringView> MonitorRawAsync(int studioId, string body)
        => MonitorRawAsync("studio", studioId, body);

    public Task<EntityMonitoringView> MonitorRawAsync(string kind, int coveId, string body)
        => ActRawAsync(kind, coveId, "monitor", body);

    public Task<EntityMonitoringView> UnmonitorAsync(string kind, int coveId)
        => ActRawAsync(kind, coveId, "unmonitor", "{}");

    public Task<EntityMonitoringView> ChangeScopeAsync(string kind, int coveId, string scope)
        => ActRawAsync(kind, coveId, "scope", $$"""{"scope":"{{scope}}"}""");

    /// <summary>Posts <paramref name="body"/> to one entity's <paramref name="verb"/> route.</summary>
    /// <remarks>
    /// The raw string is sent rather than a serialized record, so a case can carry members the
    /// request contract declares nothing for.
    /// </remarks>
    public async Task<EntityMonitoringView> ActRawAsync(
        string kind, int coveId, string verb, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var answered = await Http.PostAsync(RouteFor(kind, coveId, verb), content, TestCt);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<EntityMonitoringView>(TestCt))!;
    }

    /// <summary>The raw answer to the bulk route, given <paramref name="body"/> verbatim.</summary>
    /// <remarks>
    /// The raw string is sent rather than a serialized record, so a case can carry an id array of any
    /// length and members the request contract declares nothing for.
    /// </remarks>
    public async Task<HttpResponseMessage> PostBulkAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await Http.PostAsync(RouteBase + "/entities/bulk-monitor", content, TestCt);
    }

    /// <summary>The raw answer to one entity's reflect-owned route, which takes no body at all.</summary>
    public Task<HttpResponseMessage> ReflectOwnedAsync(string kind, int coveId)
        => Http.PostAsync(RouteFor(kind, coveId, "reflect-owned"), content: null, TestCt);

    /// <summary>The reflect-owned route's answer, read as the contract it declares.</summary>
    public async Task<ReflectOwnedEnqueued> ReflectOwnedViewAsync(string kind, int coveId)
    {
        var answered = await ReflectOwnedAsync(kind, coveId);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<ReflectOwnedEnqueued>(TestCt))!;
    }

    /// <summary>The raw answer to this extension's own job-status route.</summary>
    public Task<HttpResponseMessage> ReadJobStatusAsync(string jobId)
        => Http.GetAsync(RouteBase + "/job-status/" + jobId, TestCt);

    /// <summary>Runs the batch the last enqueue handed the host, reporting into <paramref name="progress"/>.</summary>
    public Task RunEnqueuedBatchAsync(RecordingJobProgress progress)
        => Jobs.RunLastAsync(progress, TestCt);

    /// <summary>The raw answer to one entity's <paramref name="verb"/> route.</summary>
    public async Task<HttpResponseMessage> PostRawAsync(
        string kind, int coveId, string verb, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await Http.PostAsync(RouteFor(kind, coveId, verb), content, TestCt);
    }

    public async Task<EntityMonitoringView> ReadMonitoringAsync(int studioId)
    {
        var answered = await Http.GetAsync(RouteFor("studio", studioId, "monitoring"), TestCt);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<EntityMonitoringView>(TestCt))!;
    }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        _http?.Dispose();
        await _app.StopAsync(TestCt);
        await _app.DisposeAsync();
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<int> SeedSceneAsync(
        int? studioId, int? performerId, string? endpoint, string? remoteId)
    {
        var title = "Scene " + (++_seeded).ToString(CultureInfo.InvariantCulture);
        var video = new Video { Title = title, StudioId = studioId };
        _db.Add(video);
        await _db.SaveChangesAsync(TestCt);

        if (performerId is { } linked)
        {
            _db.Add(new VideoPerformer { VideoId = video.Id, PerformerId = linked });
            await _db.SaveChangesAsync(TestCt);
        }

        if (endpoint is not null && remoteId is not null)
        {
            await AddSceneIdentityAsync(video.Id, endpoint, remoteId);
        }

        return video.Id;
    }

    private async Task SeedVideoFileAsync(string folderPath, int? studioId, int? performerId)
    {
        var basename = "scene " + (++_seeded).ToString(CultureInfo.InvariantCulture) + ".mp4";

        var folder = await _db.Set<Folder>().FirstOrDefaultAsync(row => row.Path == folderPath, TestCt);
        if (folder is null)
        {
            folder = new Folder { Path = folderPath };
            _db.Add(folder);
            await _db.SaveChangesAsync(TestCt);
        }

        var video = new Video { Title = basename, StudioId = studioId };
        _db.Add(video);
        await _db.SaveChangesAsync(TestCt);

        if (performerId is { } linked)
        {
            _db.Add(new VideoPerformer { VideoId = video.Id, PerformerId = linked });
            await _db.SaveChangesAsync(TestCt);
        }

        _db.Add(new VideoFile
        {
            Basename = basename,
            ParentFolderId = folder.Id,
            VideoId = video.Id,
        });
        await _db.SaveChangesAsync(TestCt);
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;
}
