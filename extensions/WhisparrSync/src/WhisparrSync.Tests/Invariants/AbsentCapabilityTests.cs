using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Scene;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Invariants;

// These tests state which requests the declared surface can express and which it cannot. They
// assert on the seam's declared members, the verb-class vocabulary and the declared routes, never
// on a log of calls: an empty log against a call nobody could place agrees with itself whatever the
// code does. For the same reason the sets below are transcribed by hand rather than gathered from
// the code they check.
public sealed class AbsentCapabilityTests
{
    // The routes this product composes itself, transcribed by hand from the seam's own constants.
    // The exclusions route string is also composed by the generated client for the two exclusion
    // writes, so it appears in both sets.
    private static readonly string[] DeclaredRoutes =
    [
        "api/v3/notification",
        "api/v3/studio",
        "api/v3/performer",
        "api/v3/exclusions",
        "api/v3/movie",
    ];

    // The routes the generated client composes on this product's behalf. That client declares an
    // operation for every route Whisparr serves, so a gathered set would name hundreds this product
    // never calls. Both generations serve the same route strings, and a recorded path carries no
    // generation, so one set covers the two.
    private static readonly string[] GeneratedRoutes =
    [
        "api/v3/command",
        "api/v3/config/mediamanagement",
        "api/v3/episode",
        "api/v3/episode/monitor",
        "api/v3/exclusions",
        "api/v3/filesystem",
        "api/v3/history",
        "api/v3/manualimport",
        "api/v3/movie",
        "api/v3/notification",
        "api/v3/notification/schema",
        "api/v3/performer",
        "api/v3/performer/editor",
        "api/v3/qualityprofile",
        "api/v3/rootfolder",
        "api/v3/seasonpass",
        "api/v3/series",
        "api/v3/series/editor",
        "api/v3/studio",
        "api/v3/studio/editor",
        "api/v3/system/status",
    ];

    // The acting members are named here rather than gathered, so a member that could add something
    // and was not written down fails. Keeping them off the read-and-configure interface lets a
    // reader of that interface hold it without holding an add.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryAddIsNonGrabbing)]
    public void EveryMemberThatCanAddToAnInstanceIsAnActingMember()
    {
        Assert.Equal(
            [
                nameof(IWhisparrPerformerActing.AddMonitoredPerformerAsync),
                nameof(IWhisparrStudioActing.AddMonitoredStudioAsync),
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                nameof(IWhisparrSceneExclusionActing.AddSceneExclusionAsync),
                nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync),
                nameof(IWhisparrEntityRelocationActing.MoveEntityFolderAsync),
                nameof(IWhisparrMissingSceneActing.RefreshCatalogueAsync),
                nameof(IWhisparrSiteRegistrationActing.RefreshSiteCatalogueAsync),
                nameof(IWhisparrSiteRegistrationActing.RegisterSiteAsync),
                nameof(IWhisparrSceneExclusionActing.RemoveSceneExclusionAsync),
                nameof(IWhisparrPerformerActing.SetPerformerMonitoredAsync),
                nameof(IWhisparrSceneMonitorActing.SetSceneMonitoredAsync),
                nameof(IWhisparrStudioActing.SetStudioMonitoredAsync),
                nameof(IWhisparrStudioActing.SetStudioScopeAsync),
            ],
            OutboundSeam.MembersOf(WhisparrVerbClass.Act));

        Assert.DoesNotContain(
            typeof(IWhisparrClient).GetMethods().Select(method => method.Name),
            name => OutboundSeam.VerbClassByMember[name] == WhisparrVerbClass.Act);
    }

    // Exact equality both times. A fifth verb class fails here rather than being classed by whoever
    // added it, and a route the client can issue that nobody transcribed fails here rather than
    // reaching an instance unnamed.
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

        Assert.Equal(DeclaredRoutes.Order().ToList(), RoutesDeclaredByTheSeam().Order().ToList());
    }

    // Driven rather than read off a constant, because the generated client composes the route and
    // only a request it made shows what it composed. The equality runs both directions: a member
    // reaching a route nobody wrote down fails, and so does a transcribed route no call drives.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OnlyAnExplicitSearchGrabs)]
    public async Task EveryRouteTheGeneratedClientSendsOnWasTranscribed()
    {
        var handler = BodyRecordingHandler.AnsweringByPath(AnswerFor);

        // v2's site paths send nothing at all for an identifier no site number is established for,
        // so the drive below reaches none of that generation's site routes without this.
        using var http = new HttpClient(handler);
        var instances = TestWhisparrClient.FactoryOver(
            http, handler, siteNumbers: TestSiteNumbers.Numbering("studio-1", 3372));

        await DriveEveryGeneratedRouteAsync(
            instances, TestWhisparrClient.TransportOver(http, handler));

        Assert.NotEmpty(handler.Requests);
        Assert.Equal(
            GeneratedRoutes.Order().ToList(),
            handler.Requests
                .Select(request => TranscribedRouteFor(request.Path))
                .Distinct()
                .Order()
                .ToList());
    }

    // The site-row read raises where its answer is not a list of rows, so that one route answers a
    // list. Empty is enough: the case is about which route was reached.
    private static string AnswerFor(string path)
        => path.EndsWith("/episode", StringComparison.Ordinal) ? "[]" : "{}";

    // A route naming one entity carries its identifier as a further segment, so the transcribed
    // route is a whole-segment prefix of what was sent. The longest match wins: several transcribed
    // routes are whole-segment prefixes of others, and a first match would fold them together. An
    // unmatched path maps to itself, so the equality names it.
    private static string TranscribedRouteFor(string path)
    {
        var sent = path.TrimStart('/');
        return GeneratedRoutes
            .Where(route => string.Equals(sent, route, StringComparison.Ordinal)
                || sent.StartsWith(route + "/", StringComparison.Ordinal))
            .OrderByDescending(route => route.Length)
            .FirstOrDefault() ?? sent;
    }

    // One call per generated operation this product names, driven through the seam rather than
    // through the generated client, so a member rewired to another operation is what fails. Both
    // instances are driven, because each declares the members its own generation holds.
    private static async Task DriveEveryGeneratedRouteAsync(
        WhisparrInstanceFactory instances, WhisparrTransport transport)
    {
        var address = new Uri("http://whisparr:6969");
        const string key = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
        var defaults = new AddDefaults(4, "/config/library");
        var ct = TestContext.Current.CancellationToken;

        var v3 = instances.Bound(new WhisparrBinding(WhisparrGeneration.V3, address, key));
        var v2 = instances.Bound(new WhisparrBinding(WhisparrGeneration.V2, address, key));

        // The status read runs before a generation is known, so it sits on the transport rather than
        // on either instance. Driven here all the same: its route is one this product sends on.
        await transport.ReadStatusAsync(address, key, ct);

        await v3.ReadNotificationSchemaAsync(ct);
        await v3.ListNotificationsAsync(ct);
        await v3.ReadRootFoldersAsync(ct);
        await v3.ReadQualityProfilesAsync(ct);
        await v3.ReadHistoryAsync(1, 10, ct);
        await v3.ReadCommandAsync(8123, ct);

        var v3Studio = (IWhisparrStudioActing)v3;
        var v3Scene = (IWhisparrSceneStatusReading)v3;
        await ((IWhisparrInstanceFilesystemReading)v3).ReadInstanceFolderAsync("/config/library/", ct);
        await v3Studio.ReadStudioAsync("studio-1", ct);
        await v3Studio.AddMonitoredStudioAsync("studio-1", MonitorScope.AllScenes, defaults, ct);
        await v3Studio.SetStudioMonitoredAsync(4, monitored: true, ct);
        await v3Scene.ReadEntityPresenceAsync(WhisparrEntityKind.Studio, "studio-1", ct);

        var v3Performer = (IWhisparrPerformerActing)v3;
        await v3Performer.ReadPerformerAsync("performer-1", ct);
        await v3Performer.AddMonitoredPerformerAsync("performer-1", defaults, ct);
        await v3Performer.SetPerformerMonitoredAsync(11, monitored: true, ct);
        await v3Scene.ReadEntityPresenceAsync(WhisparrEntityKind.Performer, "performer-1", ct);

        var v3Missing = (IWhisparrMissingSceneActing)v3;
        await v3Missing.AddSceneAsync("scene-1", defaults, ct);
        await v3Scene.ReadSceneByRemoteIdAsync("scene-1", ct);
        await v3Missing.RefreshCatalogueAsync(WhisparrEntityKind.Studio, 4, ct);
        await ((IWhisparrSceneMonitorActing)v3).SetSceneMonitoredAsync(41, monitored: true, ct);

        var v3Exclusions = (IWhisparrSceneExclusionActing)v3;
        await v3Exclusions.AddSceneExclusionAsync("scene-1", ct);
        await v3Exclusions.RemoveSceneExclusionAsync(12, ct);

        var v3Reflect = (IWhisparrReflectOwnedActing)v3;
        await v3Reflect.ReadHardlinkSettingAsync(ct);
        await v3Reflect.ListImportableFilesAsync("/config/library", ct);
        await v3Reflect.AttachOwnedFilesAsync(
            new JsonArray(new JsonObject { ["path"] = "/config/library/a.mp4" }), ct);

        await ((IWhisparrSearchGrabbing)v3).SearchMonitoredAsync(
            WhisparrEntityKind.Studio, [4], ct);

        // The scope change is driven on v2 only. v3 reads and replaces the resource through the
        // hand-composed date gate, whose route belongs to DeclaredRoutes.
        var v2Studio = (IWhisparrStudioActing)v2;
        await v2.ReadHistoryAsync(1, 10, ct);
        await v2Studio.ReadStudioAsync("studio-1", ct);
        await v2Studio.AddMonitoredStudioAsync("studio-1", MonitorScope.AllScenes, defaults, ct);
        await v2Studio.SetStudioMonitoredAsync(4, monitored: true, ct);
        await v2Studio.SetStudioScopeAsync(4, MonitorScope.AllScenes, ct);
        await ((IWhisparrSearchGrabbing)v2).SearchMonitoredAsync(WhisparrEntityKind.Studio, [4], ct);
        await ((IWhisparrSceneMonitorActing)v2).SetSceneMonitoredAsync(41, monitored: true, ct);
        await ((IWhisparrSiteSceneReading)v2).ReduceSiteSceneRowsAsync(1, [1363738], ct);
        await ((IWhisparrInstanceFilesystemReading)v2).ReadInstanceFolderAsync("/config/library/", ct);
    }

    private static IEnumerable<string> RoutesDeclaredByTheSeam()
        => OutboundSeamTypes.DeclaredLiterals()
            .Where(value => value.StartsWith("api/", StringComparison.Ordinal));

}
