using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Addressing;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Jobs;
using WhisparrSync.Library;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// The routes are the shipped ones, mapped by the shipped extension: a test calling a handler method
// directly would agree with a route mounted at the wrong pattern, bound to a body the browser cannot
// send, or reachable by a caller the declaration excludes.
// One recorder for the whole outbound surface, so an ordered Verbs list from any case covers every
// verb this product can issue rather than the ones one seam declares.
internal sealed class MonitorHost : IAsyncDisposable
{
    public const string StudioRemoteIdValue = "44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e";

    public const string PerformerRemoteIdValue = "9f0d6f27-1f3a-4a5f-8b21-6b2d3a5f9c10";

    // Deliberately not the standard address this product prefers. The two name one source under the
    // host's own rule, and a read comparing them as strings would answer that the entity carries no
    // identity.
    public const string StoredEndpoint = "stashdb.org/graphql";

    public const string StoredAddress = "http://whisparr-v3:6969";

    public const string StoredKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    // Non-zero, because a folder probe matches a candidate on its size as well as its path and a
    // zero would make every case agree with every file.
    public const long SeededFileSize = 41;

    // In the order an instance offers them, which is not id order.
    public const string UnsortedProfiles = """[{"id":4,"name":"Any"},{"id":1,"name":"HD-1080p"}]""";

    public const string OneRootFolder = """[{"id":1,"path":"/config/library","accessible":true}]""";

    public const string AddedStudio =
        """{"id":1,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true}""";

    public const string AddedPerformer =
        """{"id":2,"foreignId":"9f0d6f27-1f3a-4a5f-8b21-6b2d3a5f9c10","monitored":true}""";

    private readonly OptionsWriteGate _writeGate = new();
    private WebApplication _app = null!;
    private HttpClient? _http;
    private DbContext _db = null!;
    private SqliteConnection _connection = null!;
    private int _seeded;

    public RecordingWhisparrCore Client { get; private set; } = null!;

    public OptionsStore Options { get; private set; } = null!;

    // Exposed for the kinds this product resolves an identity for but mounts no action route on, so
    // a case can read the resolution itself rather than a refusal a route happens to answer.
    public IEntityIdentityPort Identities { get; private set; } = null!;

    public IEntityFolderPort Folders { get; private set; } = null!;

    // One composition asks about every configured root once, so this is what tells a root composed
    // once for a run apart from one composed again for every scene in it.
    public List<string> RootCounts { get; } = [];

    public ISampleFilePort SampleFiles { get; private set; } = null!;

    public IEntitySceneIdentityPort SceneIdentities { get; private set; } = null!;

    public ILibraryCardIdentityPort CardIdentities { get; private set; } = null!;

    public ILibrarySceneIdentityPort LibraryScenes { get; private set; } = null!;

    // Null where this host stands the recorder instead.
    public BodyRecordingHandler? Bytes { get; private set; }

    public RecordingJobService Jobs { get; } = new();

    public HttpClient Http { get; private set; } = null!;

    // Every job type is prefixed with this id.
    public string ExtensionId { get; private set; } = null!;

    private string RouteBase { get; set; } = null!;

    private string FolderMappingsRoute => RouteBase + "/addressing/folder-mappings";

    // Escapes the backslashes a Windows path carries.
    private static string Quoted(string value) => JsonSerializer.Serialize(value);

    // With bytes supplied the shipped client is stood over it instead of the recorder, so a case can
    // read the request bodies that actually leave rather than the arguments a seam was handed. The
    // two are different facts: what a call site supplied is not what the client composes from it, and
    // the composed body is what an instance acts on.
    public static async Task<MonitorHost> CreateAsync(
        FakePrincipalAccessor? principal = null,
        string? apiKey = StoredKey,
        WhisparrGeneration generation = WhisparrGeneration.V3,
        BodyRecordingHandler? bytes = null,
        MonitorScope defaultScope = MonitorScope.FutureScenes,
        IProviderCatalogue? catalogue = null,
        CoveConfiguration? metadataConfig = null,
        CoveConfiguration? libraryConfig = null,
        ISiteNumberPort? siteNumbers = null)
    {
        var host = new MonitorHost();
        (host._db, host._connection) = await CoveContextFactory.CreateSqliteContextAsync();

        // Each entity read answers twice: not held, then held and monitored. That is one instance
        // acting on the add between the two reads, and it is what the monitor path's own read-back
        // then classifies the outcome from. A single answer would describe an instance that took the
        // add and never held the entity, which is the refused case rather than the ordinary one.
        host.Client = Recorder(generation)
            .Answering(nameof(IWhisparrClient.ReadQualityProfilesAsync), Json(200, UnsortedProfiles))
            .Answering(nameof(IWhisparrClient.ReadRootFoldersAsync), Json(200, OneRootFolder))
            .Answering(
                nameof(IWhisparrStudioActing.ReadStudioAsync), Json(404, ""), Json(200, AddedStudio))
            .Answering(nameof(IWhisparrStudioActing.AddMonitoredStudioAsync), Json(201, AddedStudio))
            .Answering(
                nameof(IWhisparrPerformerActing.ReadPerformerAsync),
                Json(404, ""),
                Json(200, AddedPerformer))
            .Answering(
                nameof(IWhisparrPerformerActing.AddMonitoredPerformerAsync),
                Json(201, AddedPerformer));

        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions
            {
                SelectedGeneration = generation,
                DefaultMonitorScope = defaultScope,
            }.WithConnectionFor(
                generation, new WhisparrSyncGenerationConnection { Address = StoredAddress }),
            TestCt);

        // The address goes in beside the key, which is what a save writes. A row holding the key
        // alone is a row no save produces.
        var credentials = new RecordingCredentialPort();
        if (apiKey is not null)
        {
            credentials.Holding(generation, StoredAddress, apiKey);
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
            builder.Services.AddSingleton<IWhisparrInstanceFactory>(
                new FixedInstanceFactory(host.Client));
        }
        else
        {
            host.Bytes = bytes;
            host._http = new HttpClient(bytes);
            builder.Services.AddSingleton<IWhisparrInstanceFactory>(
                TestWhisparrClient.FactoryOver(host._http, bytes, siteNumbers: siteNumbers));
        }

        builder.Services.AddSingleton<IJobService>(host.Jobs);
        host.Identities = new EntityIdentityPort(host._db, options);
        builder.Services.AddSingleton(host.Identities);
        builder.Services.AddSingleton(new LibraryStatusPort(host.Identities, NullLogger.Instance));
        host.CardIdentities = new LibraryCardIdentityPort(host._db, options);
        builder.Services.AddSingleton(host.CardIdentities);
        host.Folders = new CountRecordingFolders(new EntityFolderPort(host._db), host.RootCounts);
        host.SampleFiles = new SampleFilePort(host._db);
        builder.Services.AddSingleton(host.Folders);
        builder.Services.AddSingleton(host.SampleFiles);

        // The whole outbound addressing chain, stood over the same database and the same client the
        // routes use, so a case can drive a folder all the way to the request that leaves.
        var libraryPort = new CoveLibraryPort(
            host._db, null, null, libraryConfig, NullLogger.Instance);
        builder.Services.AddSingleton<ICoveLibraryPort>(libraryPort);
        builder.Services.AddSingleton<IReportedRootPort>(
            resolved => new ReportedRootPort(
                resolved.GetRequiredService<IWhisparrInstanceFactory>(),
                credentials,
                new ReportedRootCache(TimeProvider.System),
                NullLogger.Instance));
        // With no library configuration the shipped chain would answer that every folder sits under
        // no root, which is true and is not what a case about the folder loop is asking. Those cases
        // run over an instance whose spelling is the library's own instead; a case whose subject IS
        // the addressing names a configuration and gets the shipped chain.
        if (libraryConfig is null)
        {
            builder.Services.AddSingleton<IFolderAddressPort>(new PassThroughFolderAddresses());
        }
        else
        {
            builder.Services.AddSingleton<IFolderAddressPort>(
                resolved => new FolderAddressPort(
                    host.SampleFiles,
                    libraryPort,
                    resolved.GetRequiredService<IReportedRootPort>(),
                    options,
                    new FolderAgreementCache(TimeProvider.System),
                    NullLogger.Instance));
        }
        host.SceneIdentities = new EntitySceneIdentityPort(host._db, options);
        builder.Services.AddSingleton(host.SceneIdentities);
        host.LibraryScenes = new LibrarySceneIdentityPort(host._db, options);
        builder.Services.AddSingleton(host.LibraryScenes);
        builder.Services.AddSingleton(new SyncPreviewCache(TimeProvider.System));

        // The catalogue tab's add takes it, and a route parameter the container cannot resolve is
        // bound from the request body instead, which answers a route input guard something else.
        builder.Services.AddSingleton(new InstanceCatalogueCache(TimeProvider.System));
        host.Options = options;
        builder.Services.AddSingleton(options);

        // A real gate rather than the registration-only null: a run writes back what it established
        // about each library root through it, so a case driving a run reaches it.
        builder.Services.AddSingleton(host._writeGate);
        builder.Services.AddSingleton<ICredentialPort>(credentials);

        // The catalogue routes take these, and minimal-API binding resolves a handler's services
        // before the handler runs, so a route-input case never reaches its own guard without them.
        // Built over no host configuration by default, which is the stated refusal rather than a
        // throw. A case whose subject is a page the catalogue really answers names a source instead.
        builder.Services.AddSingleton(new ProviderEndpointPort(metadataConfig));
        // A case whose subject is a route reaching no provider passes one that throws on every
        // member, so a reach is a failure rather than an answer nobody looked at.
        var provider = catalogue ?? new InertProviderCatalogue();
        // Registered as the source as well as handed to the page planner, because a library run
        // resolves it out of its own elevated scope rather than taking it as an argument.
        var catalogues = TestProviderCatalogues.Naming(provider);
        builder.Services.AddSingleton(catalogues);
        builder.Services.AddSingleton(
            new MissingPagePlanner(
                new MissingIdentityResolver(
                    host.Identities, catalogues, new EntityNamePort(host._db)),
                catalogues,
                new OwnedScenePort(host._db),
                new InstanceCatalogueCache(TimeProvider.System)));

        // The bundles the route lambdas take. Built from the same instances registered above, so a
        // case that seeds one reaches it through either shape.
        builder.Services.AddSingleton(
            services => new WhisparrAccess(
                options,
                credentials,
                services.GetRequiredService<IWhisparrInstanceFactory>(),
                NullLogger.Instance));
        builder.Services.AddSingleton(
            services => new BackgroundWork(
                services.GetRequiredService<IJobService>(),
                services.GetRequiredService<IServiceScopeFactory>()));
        builder.Services.AddSingleton(new OptionsWriting(options, host._writeGate));

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
        => RecordingWhisparrCore.Json(status, body);

    // A case naming no generation drives v3, which most do. The recorder declares exactly the roles
    // that generation's instance declares, so a v2 case cannot reach a role v2 does not hold.
    private static RecordingWhisparrCore Recorder(WhisparrGeneration generation)
        => generation == WhisparrGeneration.V2
            ? new RecordingWhisparrV2Client(Json(200, "{}")) { RequireConfiguredResponses = true }
            : new RecordingWhisparrV3Client(Json(200, "{}")) { RequireConfiguredResponses = true };

    public string RouteFor(string kind, int coveId, string verb)
        => string.Create(CultureInfo.InvariantCulture, $"{RouteBase}/entity/{kind}/{coveId}/{verb}");

    public string ConnectionOfferRoute => RouteBase + "/connection/offer";

    public string SceneRouteFor(int coveId, string verb)
        => string.Create(CultureInfo.InvariantCulture, $"{RouteBase}/scene/{coveId}/{verb}");

    public Task<HttpResponseMessage> PostSceneAsync(int coveId, string verb)
        => Http.PostAsync(SceneRouteFor(coveId, verb), content: null, TestCt);

    public async Task<SceneActionResult> SceneActionAsync(int coveId, string verb)
    {
        var answered = await PostSceneAsync(coveId, verb);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<SceneActionResult>(TestCt))!;
    }

    public async Task<SceneDetailView> SceneDetailAsync(int coveId)
    {
        var answered = await Http.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"{RouteBase}/scene/{coveId}"), TestCt);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<SceneDetailView>(TestCt))!;
    }

    // The name is made unique per call. The host's own name key is unique, so a second studio seeded
    // under one name fails the save rather than the assertion.
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

    // Exists so a case can hold two rows the host's same-source rule treats as one source, which is
    // the shape a first-row pick would resolve silently.
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

    // Seeded through the same context as a studio, so a case can hold both kinds at once and a path
    // reading the wrong identity table finds a row rather than nothing.
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

    public async Task<int> SeedTagAsync(string? endpoint, string? remoteId)
    {
        var name = "Tag " + (++_seeded).ToString(CultureInfo.InvariantCulture);
        var tag = new Tag { Name = name, NamespaceKey = name.ToLowerInvariant() };
        _db.Add(tag);
        await _db.SaveChangesAsync(TestCt);

        if (endpoint is not null && remoteId is not null)
        {
            await AddTagIdentityAsync(tag.Id, endpoint, remoteId);
        }

        return tag.Id;
    }

    public async Task AddTagIdentityAsync(int tagId, string endpoint, string remoteId)
    {
        _db.Add(new TagRemoteId
        {
            TagId = tagId,
            Endpoint = endpoint,
            RemoteId = remoteId,
        });
        await _db.SaveChangesAsync(TestCt);
    }

    // A folder already seeded at the given path is reused, so a case can put two of one entity's
    // files in a single folder and see whether the folder is answered twice.
    public Task<string> SeedStudioFileAsync(int studioId, string folderPath, long size = 0)
        => SeedVideoFileAsync(folderPath, studioId, null, size);

    // Linked through the join row rather than through the studio column: a performer's files reach
    // them by a different table, so a port reading the studio column would answer nothing here.
    public Task<string> SeedPerformerFileAsync(int performerId, string folderPath, long size = 0)
        => SeedVideoFileAsync(folderPath, null, performerId, size);

    // A scene rather than a file: what the registration verb offers an instance is the identifier a
    // video carries, and a video carries one whether or not the library holds a file for it.
    public Task<int> SeedStudioSceneAsync(int studioId, string? endpoint, string? remoteId)
        => SeedSceneAsync(studioId, null, endpoint, remoteId);

    // Linked through the join row rather than through the studio column, so a source reading the
    // studio column answers nothing here.
    public Task<int> SeedPerformerSceneAsync(int performerId, string? endpoint, string? remoteId)
        => SeedSceneAsync(null, performerId, endpoint, remoteId);

    // Exists so a case can hold two rows the host's same-source rule treats as one source, which is
    // the shape a database-side distinct on the raw pair cannot collapse.
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

    // The raw string is sent rather than a serialized record, so a case can carry members the
    // request contract declares nothing for.
    public async Task<EntityMonitoringView> ActRawAsync(
        string kind, int coveId, string verb, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var answered = await Http.PostAsync(RouteFor(kind, coveId, verb), content, TestCt);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<EntityMonitoringView>(TestCt))!;
    }

    // The raw string is sent rather than a serialized record, so a case can carry an id array of any
    // length and members the request contract declares nothing for.
    public async Task<HttpResponseMessage> PostBulkAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await Http.PostAsync(RouteBase + "/entities/bulk-monitor", content, TestCt);
    }

    // Sent as given, so a case can name a selection type the route answers for nothing, an absent
    // member, and an id array of any length.
    public async Task<HttpResponseMessage> PostSceneBatchAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await Http.PostAsync(RouteBase + "/scenes/batch", content, TestCt);
    }

    // Both the kind segment and the body are sent as given, so a case can name a kind the route
    // answers for nothing and an id array of any length.
    public async Task<HttpResponseMessage> PostLibraryStatusAsync(string kind, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await Http.PostAsync($"{RouteBase}/library/{kind}/status", content, TestCt);
    }

    public Task<HttpResponseMessage> ReflectOwnedAsync(string kind, int coveId)
        => Http.PostAsync(RouteFor(kind, coveId, "reflect-owned"), content: null, TestCt);

    public async Task<ReflectOwnedEnqueued> ReflectOwnedViewAsync(string kind, int coveId)
    {
        var answered = await ReflectOwnedAsync(kind, coveId);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<ReflectOwnedEnqueued>(TestCt))!;
    }

    public Task<HttpResponseMessage> AddAllMissingAsync(string kind, int coveId)
        => Http.PostAsync(RouteFor(kind, coveId, "add-all-missing"), content: null, TestCt);

    public async Task<AddAllMissingEnqueued> AddAllMissingViewAsync(string kind, int coveId)
    {
        var answered = await AddAllMissingAsync(kind, coveId);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<AddAllMissingEnqueued>(TestCt))!;
    }

    public async Task<FolderAgreementView> ReadFolderMappingsAsync()
    {
        var answered = await Http.GetAsync(FolderMappingsRoute, TestCt);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<FolderAgreementView>(TestCt))!;
    }

    public Task<HttpResponseMessage> GetFolderMappingsAsync() => Http.GetAsync(FolderMappingsRoute, TestCt);

    // The raw string is sent rather than a serialized record, so a case can carry an absent member
    // and members the request contract declares nothing for.
    public async Task<HttpResponseMessage> PutFolderMappingAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await Http.PutAsync(FolderMappingsRoute, content, TestCt);
    }

    public async Task<FolderMappingSaveResult> SaveFolderMappingAsync(string coveRoot, string path)
    {
        var answered = await PutFolderMappingAsync(
            $$"""{"coveRoot":{{Quoted(coveRoot)}},"instancePath":{{Quoted(path)}}}""");
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<FolderMappingSaveResult>(TestCt))!;
    }

    public Task<HttpResponseMessage> ReadJobStatusAsync(string jobId)
        => Http.GetAsync(RouteBase + "/job-status/" + jobId, TestCt);

    public Task RunEnqueuedBatchAsync(RecordingJobProgress progress)
        => Jobs.RunLastAsync(progress, TestCt);

    // Exists for a case that asserts the status before the body. The helpers that post and read in
    // one call throw on a failure status, which is a throw where the status itself is the subject.
    public static async Task<EntityMonitoringView> ReadViewAsync(HttpResponseMessage answered)
    {
        ArgumentNullException.ThrowIfNull(answered);
        return (await answered.Content.ReadFromJsonAsync<EntityMonitoringView>(TestCt))!;
    }

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
        _writeGate.Dispose();
        await _app.StopAsync(TestCt);
        await _app.DisposeAsync();
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
        Assert.Empty(Client.UnexpectedCalls);
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

    // One video across several folders, which the per-file helpers cannot express: each of those
    // seeds a video of its own, so a case about one video's files would be about several.
    //
    // Saved once for the whole set. A save per row makes the context rescan every entity it already
    // tracks, so a case seeding a few hundred files pays for that walk once per file.
    public async Task<int> SeedVideoWithFilesAsync(int studioId, params string[] folderPaths)
    {
        ArgumentNullException.ThrowIfNull(folderPaths);

        var videoId = await SeedSceneAsync(studioId, null, null, null);
        foreach (var folderPath in folderPaths)
        {
            _db.Add(await SceneFileAsync(videoId, folderPath));
        }

        await _db.SaveChangesAsync(TestCt);
        return videoId;
    }

    public async Task<string> SeedSceneFileAsync(int videoId, string folderPath)
    {
        var file = await SceneFileAsync(videoId, folderPath);
        _db.Add(file);
        await _db.SaveChangesAsync(TestCt);
        return file.Path;
    }

    private async Task<VideoFile> SceneFileAsync(int videoId, string folderPath)
    {
        var folder = await FolderAtAsync(folderPath);
        return new VideoFile
        {
            Basename = "scene " + (++_seeded).ToString(CultureInfo.InvariantCulture) + ".mp4",
            ParentFolderId = folder.Id,
            VideoId = videoId,
            Size = SeededFileSize,
        };
    }

    private async Task<Folder> FolderAtAsync(string folderPath)
    {
        var folder = await _db.Set<Folder>()
            .FirstOrDefaultAsync(row => row.Path == folderPath, TestCt);
        if (folder is null)
        {
            folder = new Folder { Path = folderPath };
            _db.Add(folder);
            await _db.SaveChangesAsync(TestCt);
        }

        return folder;
    }

    // A file per folder, or several, seeded in two saves rather than four per file. A save per row
    // makes the context rescan every entity it already tracks, so a case seeding a few hundred
    // files pays for that walk once per row.
    public async Task SeedStudioFilesAsync(
        int studioId, IEnumerable<string> folderPaths, int perFolder = 1)
    {
        ArgumentNullException.ThrowIfNull(folderPaths);

        var folders = new List<(Folder Folder, Video Video)>();
        foreach (var folderPath in folderPaths)
        {
            for (var at = 0; at < perFolder; at++)
            {
                var folder = await FolderAtAsync(folderPath);
                var video = new Video
                {
                    Title = "scene " + (++_seeded).ToString(CultureInfo.InvariantCulture) + ".mp4",
                    StudioId = studioId,
                };
                _db.Add(video);
                folders.Add((folder, video));
            }
        }

        await _db.SaveChangesAsync(TestCt);

        foreach (var (folder, video) in folders)
        {
            _db.Add(new VideoFile
            {
                Basename = video.Title!,
                ParentFolderId = folder.Id,
                VideoId = video.Id,
                Size = SeededFileSize,
            });
        }

        await _db.SaveChangesAsync(TestCt);
    }

    private async Task<string> SeedVideoFileAsync(
        string folderPath, int? studioId, int? performerId, long size)
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

        var file = new VideoFile
        {
            Basename = basename,
            ParentFolderId = folder.Id,
            VideoId = video.Id,
            Size = size,
        };
        _db.Add(file);
        await _db.SaveChangesAsync(TestCt);
        return file.Path;
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private sealed class CountRecordingFolders(IEntityFolderPort held, List<string> counts)
        : IEntityFolderPort
    {
        public IAsyncEnumerable<string> FoldersFor(
            WhisparrEntityKind kind, int coveId, CancellationToken ct)
            => held.FoldersFor(kind, coveId, ct);

        public Task<int> FilesUnderAsync(
            WhisparrEntityKind kind, int coveId, string coveRoot, CancellationToken ct)
        {
            counts.Add(coveRoot);
            return held.FilesUnderAsync(kind, coveId, coveRoot, ct);
        }

        public Task<int> VideoFilesUnderAsync(int videoId, string coveRoot, CancellationToken ct)
        {
            counts.Add(coveRoot);
            return held.VideoFilesUnderAsync(videoId, coveRoot, ct);
        }
    }
}

// For a case whose subject is the folder loop rather than the agreement. It answers every folder
// unchanged, which is what a Cove and a Whisparr sharing one mount really agree on.
internal sealed class PassThroughFolderAddresses : IFolderAddressPort
{
    public Task<AddressedFolder> AddressAsync(
        FolderAddressTarget target, string folder, CancellationToken ct)
        => Task.FromResult(new AddressedFolder(folder, null, "/config/library", []));

    // Raises: a case whose subject is a supplied mapping names a library configuration and gets the
    // shipped chain, so reaching this one would be a case reading an answer nobody probed for.
    public Task<AddressedFolder> AddressAsync(
        FolderAddressTarget target, string coveRoot, string supplied, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<AddressedFolder> AgreedRootAsync(
        FolderAddressTarget target, string coveRoot, CancellationToken ct)
        => Task.FromResult(new AddressedFolder(coveRoot, null, coveRoot, []));
}

internal sealed class InertProviderCatalogue : IProviderCatalogue
{
    public IReadOnlyList<ProviderSortOption> Sorts { get; } = [];

    public string DefaultSort => string.Empty;

    public string? SceneAddress(string providerSceneId) => null;

    public ProviderCapabilitySet Capabilities { get; } = ProviderCapabilities.ForStashDb(new object());

    public Task<ProviderCatalogueAnswer> ReadPageAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
        => Task.FromResult(
            ProviderCatalogueAnswer.Answered(
                new ProviderCataloguePage([], 0, SizeIsLowerBound: false, 1, 1, 0)));

    public Task<int?> ReadCatalogueSizeAsync(ProviderCatalogueRequest request, CancellationToken ct)
        => Task.FromResult<int?>(null);

    public Task<ProviderIdentityLookup> LookUpByNameAsync(
        WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
        => Task.FromResult(ProviderIdentityLookup.Unmatched);

    public Task<int?> ResolveNumericSceneIdAsync(string providerSceneId, CancellationToken ct)
        => Task.FromResult<int?>(null);

    public Task<ProviderSiteNumber> ResolveNumericSiteIdAsync(
        string providerSiteId, CancellationToken ct)
        => Task.FromResult(ProviderSiteNumber.NotReached);

    public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
        WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderFacetMenu>>([]);

    public Task<ProviderFacetSearch> SearchFacetValuesAsync(
        WhisparrEntityKind kind,
        string providerEntityId,
        string facetKey,
        string fragment,
        CancellationToken ct)
        => Task.FromResult(ProviderFacetSearch.NotReached);
}
