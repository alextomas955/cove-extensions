using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Invariants;

/// <summary>
/// The safety invariants that hold over what this product can express at all.
/// </summary>
/// <remarks>
/// Nothing here drives an implementation. Every test states which requests the declared surface can
/// express and which it cannot, so a pass never says that a correct request was exercised.
/// <para>
/// Asserted on the seam's declared member set, on the verb-class vocabulary and on the routes the
/// client declares, rather than on a log of calls that were never made: an empty log against a call
/// nobody could place agrees with itself whatever the code does.
/// </para>
/// </remarks>
public sealed class AbsentCapabilityTests
{
    /// <summary>
    /// Every route the outbound client composes itself, transcribed by hand from its own constants.
    /// </summary>
    /// <remarks>
    /// The set is the claim. The command route <c>api/v3/command</c> is declared here, because every
    /// instance-side action an instance takes is issued through it, so its presence is not by itself
    /// evidence of anything: the claim is that exactly one member of the whole seam can send a
    /// grabbing command name and only the separately obtained role declares that member, which
    /// <see cref="SafetyInvariantTests.ExactlyOneSeamMemberGrabsAndOnlyTheGrabbingRoleDeclaresIt"/>
    /// asserts. That no body off a monitoring path names one of those commands is
    /// <see cref="SafetyInvariantTests.NoBodyOffAMonitoringPathCanNameAGrabbingCommand"/>.
    /// <para>
    /// The newer generation's routes are composed by the generated client and are not literals on
    /// this type, so they are transcribed in <see cref="GeneratedRoutes"/> and asserted against the
    /// operations this product calls rather than against a constant.
    /// </para>
    /// </remarks>
    private static readonly string[] DeclaredRoutes =
    [
        "api/v3/history",
        "api/v3/notification",
        "api/v3/studio",
        "api/v3/movie",
        "api/v3/performer",
        "api/v3/series",
        "api/v3/series/lookup",
        "api/v3/series/editor",
        "api/v3/seasonpass",
        "api/v3/command",
        "api/v3/exclusions",
    ];

    /// <summary>
    /// Every route the generated client composes on this product's behalf, and the operation that
    /// composes it, transcribed by hand.
    /// </summary>
    /// <remarks>
    /// Transcribed rather than gathered, for the reason <see cref="DeclaredRoutes"/> is: the generated
    /// client declares an operation for every route Whisparr serves, so a set gathered from it would
    /// name hundreds this product never calls and would agree with itself whichever ones it did.
    /// <see cref="TheGeneratedClientDeclaresEveryOperationThisProductNames"/> is what refuses a name
    /// the generated client does not declare.
    /// </remarks>
    private static readonly (string Api, string Operation, string Route)[] GeneratedRoutes =
    [
        ("ISystemApi", "GetSystemStatusAsync", "api/v3/system/status"),
        ("INotificationApi", "ListNotificationAsync", "api/v3/notification"),
        ("INotificationApi", "ListNotificationSchemaAsync", "api/v3/notification/schema"),
        ("IRootFolderApi", "ListRootFolderAsync", "api/v3/rootfolder"),
        ("IQualityProfileApi", "ListQualityProfileAsync", "api/v3/qualityprofile"),
        ("IHistoryApi", "GetHistoryAsync", "api/v3/history"),
        ("IStudioApi", "GetStudioByStudioForeignIdAsync", "api/v3/studio"),
        ("IStudioApi", "CreateStudioAsync", "api/v3/studio"),
        ("IStudioEditorApi", "PutStudioEditorAsync", "api/v3/studio/editor"),
        ("IPerformerApi", "GetPerformerByPerformerForeignIdAsync", "api/v3/performer"),
        ("IPerformerApi", "CreatePerformerAsync", "api/v3/performer"),
        ("IPerformerEditorApi", "PutPerformerEditorAsync", "api/v3/performer/editor"),
        ("IMovieApi", "CreateMovieAsync", "api/v3/movie"),
        ("IManualImportApi", "ListManualImportAsync", "api/v3/manualimport"),
        ("IMediaManagementConfigApi", "GetMediaManagementConfigAsync", "api/v3/config/mediamanagement"),
        ("CommandApi", "SendCommandAsync", "api/v3/command"),
    ];

    /// <summary>
    /// Every member that can add to an instance is an acting member, and none is on the read seam.
    /// </summary>
    /// <remarks>
    /// The acting members are named here rather than gathered, so a member that could add something
    /// and was not written down fails this test. Keeping them off the read-and-configure interface is
    /// what lets a reader of that interface hold it without holding an add.
    /// <para>
    /// The behavioural half — that every composed add body carries both of its generation's
    /// acquisition-suppressing flags, present and false, over every generation, kind and scope the
    /// registered capabilities allow — is
    /// <see cref="SafetyInvariantTests.EveryAddThisProductCanComposeSuppressesAcquisitionWhereItsResourceDeclaresIt"/>.
    /// This test is the type-level half: it says which members can add, not what they send.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryAddIsNonGrabbing)]
    public void EveryMemberThatCanAddToAnInstanceIsAnActingMember()
    {
        Assert.Equal(
            [
                nameof(IWhisparrPerformerActing.AddMonitoredPerformerAsync),
                nameof(IWhisparrStudioActing.AddMonitoredStudioAsync),
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync),
                nameof(IWhisparrMissingSceneActing.RefreshCatalogueAsync),
                nameof(IWhisparrPerformerActing.SetPerformerMonitoredAsync),
                nameof(IWhisparrStudioActing.SetStudioMonitoredAsync),
                nameof(IWhisparrStudioActing.SetStudioScopeAsync),
            ],
            OutboundSeam.MembersOf(WhisparrVerbClass.Act));

        Assert.DoesNotContain(
            typeof(IWhisparrClient).GetMethods().Select(method => method.Name),
            name => OutboundSeam.VerbClassByMember[name] == WhisparrVerbClass.Act);
    }

    /// <summary>
    /// The verb-class vocabulary and the declared route set are exactly what was written down.
    /// </summary>
    /// <remarks>
    /// Exact equality both times. A fifth verb class added later fails here rather than being classed
    /// by whoever added it, and a route the client can issue that nobody transcribed fails here rather
    /// than reaching an instance unnamed.
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OnlyAnExplicitSearchGrabs)]
    public void TheVerbClassVocabularyAndTheDeclaredRoutesAreExactlyTheTranscribedSets()
    {
        Assert.Equal(
            [
                WhisparrVerbClass.Read,
                WhisparrVerbClass.Configure,
                WhisparrVerbClass.Act,
                WhisparrVerbClass.Grab,
            ],
            Enum.GetValues<WhisparrVerbClass>());

        Assert.Equal(DeclaredRoutes.Order().ToList(), RoutesDeclaredByTheClient().Order().ToList());
    }

    /// <summary>
    /// The generated client declares every operation this product names, so an upgrade that renames
    /// or drops one fails here.
    /// </summary>
    /// <remarks>
    /// Reflected over the generated assembly rather than compiled against, because the claim is about
    /// the transcribed set: a name in <see cref="GeneratedRoutes"/> that the generated client does not
    /// declare would otherwise be a route nobody can reach and a line nobody removed.
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OnlyAnExplicitSearchGrabs)]
    public void TheGeneratedClientDeclaresEveryOperationThisProductNames()
    {
        var generated = typeof(Whisparr3.Net.Whisparr3Options).Assembly;

        Assert.All(
            GeneratedRoutes,
            named =>
            {
                var api = generated.GetType("Whisparr3.Net.Api." + named.Api);
                Assert.NotNull(api);
                Assert.Contains(
                    api.GetMethods(),
                    method => string.Equals(method.Name, named.Operation, StringComparison.Ordinal));
            });
    }

    /// <summary>
    /// Every route the generated client puts on the wire for this product is one that was transcribed.
    /// </summary>
    /// <remarks>
    /// Driven rather than read off a constant. The generated client composes the route, so the only
    /// honest source for what it composes is a request it made: a transcribed route compared against
    /// another transcription would agree with itself whatever the client sent.
    /// <para>
    /// Every seam member is driven, so a member reaching a route nobody wrote down fails here. The
    /// grabbing member is driven too, because the claim is about which routes exist and not about
    /// which of them acquires.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OnlyAnExplicitSearchGrabs)]
    public async Task EveryRouteTheGeneratedClientSendsOnWasTranscribed()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}");
        var client = TestWhisparrClient.Over(handler);

        await DriveEveryGeneratedRouteAsync(client);

        Assert.NotEmpty(handler.Requests);
        Assert.All(
            handler.Requests,
            request => Assert.True(
                WasTranscribed(request.Path),
                $"{request.Path} is a route no line of GeneratedRoutes names."));
    }

    // A route naming one entity carries its identifier as a further segment, so the transcribed route
    // is a whole-segment prefix of what was sent rather than the whole of it.
    private static bool WasTranscribed(string path)
    {
        var sent = path.TrimStart('/');
        return GeneratedRoutes.Any(route =>
            string.Equals(sent, route.Route, StringComparison.Ordinal)
            || sent.StartsWith(route.Route + "/", StringComparison.Ordinal));
    }

    // One call per generated operation this product names, driven through the seam rather than
    // through the generated client, so a member rewired to another operation is what fails.
    private static async Task DriveEveryGeneratedRouteAsync(WhisparrClient client)
    {
        var address = new Uri("http://whisparr:6969");
        const string key = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
        var defaults = new AddDefaults(4, "/config/library");
        var ct = TestContext.Current.CancellationToken;

        await client.ReadStatusAsync(address, key, ct);
        await client.ReadNotificationSchemaAsync(address, key, ct);
        await client.ListNotificationsAsync(address, key, ct);
        await client.ReadRootFoldersAsync(address, key, ct);
        await client.ReadQualityProfilesAsync(address, key, ct);
        await client.ReadHistoryAsync(address, key, WhisparrGeneration.V3, 1, 10, ct);

        await client.ReadStudioAsync(address, key, WhisparrGeneration.V3, "studio-1", ct);
        await client.AddMonitoredStudioAsync(
            address, key, WhisparrGeneration.V3, "studio-1", MonitorScope.AllScenes, defaults, ct);
        await client.SetStudioMonitoredAsync(
            address, key, WhisparrGeneration.V3, 4, monitored: true, ct);

        await client.ReadPerformerAsync(address, key, "performer-1", ct);
        await client.AddMonitoredPerformerAsync(address, key, "performer-1", defaults, ct);
        await client.SetPerformerMonitoredAsync(address, key, 11, monitored: true, ct);

        await client.AddSceneAsync(address, key, "scene-1", defaults, ct);
        await client.RefreshCatalogueAsync(address, key, WhisparrEntityKind.Studio, 4, ct);

        await client.ReadHardlinkSettingAsync(address, key, ct);
        await client.ListImportableFilesAsync(address, key, "/config/library", ct);
        await client.AttachOwnedFilesAsync(
            address, key, new JsonArray(new JsonObject { ["path"] = "/config/library/a.mp4" }), ct);

        await client.SearchMonitoredAsync(
            address, key, WhisparrGeneration.V3, WhisparrEntityKind.Studio, 4, ct);
    }

    /// <summary>
    /// Nothing outside the one seam can reach an instance at all.
    /// </summary>
    /// <remarks>
    /// Asserts absence. The seam's configuring half is the callback registration and nothing else.
    /// <para>
    /// Two types hold a client to make a request with: the instance client, and the metadata
    /// catalogue. The catalogue reads a third party rather than an instance, and it composes one verb
    /// on one route, so it declares no member through which a mutation could be expressed. That is
    /// asserted here rather than assumed, because a second holder of a client is otherwise a second
    /// call site nothing constrains.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryMutationIsOriginTagged)]
    public void TheProductDeclaresNoCapabilityToMutateAnythingOnAnInstance()
    {
        Assert.Equal(
            [
                nameof(IWhisparrClient.CreateNotificationAsync),
                nameof(IWhisparrClient.UpdateNotificationAsync),
            ],
            OutboundSeam.MembersOf(WhisparrVerbClass.Configure));

        Assert.Equal(
            [nameof(StashDbCatalogue), nameof(WhisparrClient)],
            TypesHoldingAnHttpClient().Order().ToList());

        // The catalogue's own surface: every request it composes is the one read verb, so no member
        // takes a verb and none takes a route or a query key from a caller.
        Assert.Empty(MembersTakingAVerbOrARouteOn(typeof(StashDbCatalogue)));
    }

    /// <summary>The relative routes the outbound client declares, read off its own constants.</summary>
    private static IEnumerable<string> RoutesDeclaredByTheClient()
        => typeof(WhisparrClient)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string?)field.GetRawConstantValue())
            .OfType<string>()
            .Where(value => value.StartsWith("api/", StringComparison.Ordinal));

    /// <summary>
    /// Every reachable member of <paramref name="type"/> letting a caller choose the verb or route.
    /// </summary>
    /// <remarks>
    /// Reachable members only: a private helper taking one of the type's own constants is not a call
    /// site a caller reaches, and including one would make this fire on correct code.
    /// <para>
    /// A parameter named <c>query</c> is not evidence here. This product's provider request is a
    /// GraphQL document, and <c>query</c> is that document's own field name rather than a URL query.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> MembersTakingAVerbOrARouteOn(Type type)
        => type
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic)
            .Where(method => !method.IsPrivate)
            .Where(method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(HttpMethod)
                || parameter.Name is "path" or "route" or "verb"))
            .Select(method => method.Name);

    /// <summary>Every type in this extension that holds something it could make a request with.</summary>
    private static IEnumerable<string> TypesHoldingAnHttpClient()
        => typeof(IWhisparrClient).Assembly
            .GetTypes()
            .Where(type => type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Any(field => typeof(HttpClient).IsAssignableFrom(field.FieldType)
                    || typeof(IHttpClientFactory).IsAssignableFrom(field.FieldType)))
            .Select(type => type.Name);
}
