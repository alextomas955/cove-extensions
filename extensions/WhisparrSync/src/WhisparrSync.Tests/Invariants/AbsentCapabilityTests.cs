using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Providers;
using WhisparrSync.Scene;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Invariants;

// These tests state which requests the declared surface can express and which it cannot. They
// assert on the seam's declared members, the verb-class vocabulary and the declared routes, not on
// a log of calls: an empty log against a call nobody could place agrees with itself whatever the
// code does.
public sealed class AbsentCapabilityTests
{
    // The routes the outbound client composes itself, transcribed by hand from its own constants.
    // The exclusions route string is also composed by the generated client for the two exclusion
    // writes, so it appears in both sets.
    private static readonly string[] DeclaredRoutes =
    [
        "api/v3/notification",
        "api/v3/studio",
        "api/v3/exclusions",
    ];

    // The routes the generated client composes on this product's behalf, transcribed by hand. The
    // generated client declares an operation for every route Whisparr serves, so a gathered set
    // would name hundreds this product never calls and agree with itself whichever ones it did. Both
    // generations serve the same route strings, and a recorded path carries no generation, so one
    // set covers the two.
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
                nameof(IWhisparrSiteRegistrationActing.MoveSiteRootAsync),
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

        Assert.Equal(DeclaredRoutes.Order().ToList(), RoutesDeclaredByTheClient().Order().ToList());
    }

    // Driven rather than read off a constant, because the generated client composes the route and
    // only a request it made shows what it composed. The equality runs both directions: a member
    // reaching a route nobody wrote down fails, and so does a transcribed route no call drives. The
    // recorded path carries no generation, so this says nothing about which one issued a request.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OnlyAnExplicitSearchGrabs)]
    public async Task EveryRouteTheGeneratedClientSendsOnWasTranscribed()
    {
        var handler = BodyRecordingHandler.AnsweringByPath(AnswerFor);

        // v2's site paths send nothing at all for an identifier no site number is established for,
        // so the drive below reaches none of that generation's site routes without this.
        var client = TestWhisparrClient.Over(
            handler, siteNumbers: TestSiteNumbers.Numbering("studio-1", 3372));

        await DriveEveryGeneratedRouteAsync(client);

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

    // A route naming one entity carries its identifier as a further segment, so the transcribed route
    // is a whole-segment prefix of what was sent. The longest match wins: several transcribed routes
    // are whole-segment prefixes of other transcribed routes, and a first match would fold them
    // together. An unmatched path maps to itself, so the equality names it.
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
        await client.ReadCommandAsync(address, key, 8123, ct);
        await client.ReadInstanceFolderAsync(address, key, WhisparrGeneration.V3, "/config/library/", ct);

        await client.ReadStudioAsync(address, key, WhisparrGeneration.V3, "studio-1", ct);
        await client.AddMonitoredStudioAsync(
            address, key, WhisparrGeneration.V3, "studio-1", MonitorScope.AllScenes, defaults, ct);
        await client.SetStudioMonitoredAsync(
            address, key, WhisparrGeneration.V3, 4, monitored: true, ct);
        await client.ReadEntityPresenceAsync(address, key, WhisparrEntityKind.Studio, "studio-1", ct);

        await client.ReadPerformerAsync(address, key, "performer-1", ct);
        await client.AddMonitoredPerformerAsync(address, key, "performer-1", defaults, ct);
        await client.SetPerformerMonitoredAsync(address, key, 11, monitored: true, ct);
        await client.ReadEntityPresenceAsync(
            address, key, WhisparrEntityKind.Performer, "performer-1", ct);

        await client.AddSceneAsync(address, key, "scene-1", defaults, ct);
        await client.ReadSceneByRemoteIdAsync(address, key, "scene-1", ct);
        await client.RefreshCatalogueAsync(address, key, WhisparrEntityKind.Studio, 4, ct);
        await client.SetSceneMonitoredAsync(
            address, key, WhisparrGeneration.V3, 41, monitored: true, ct);
        await client.AddSceneExclusionAsync(address, key, "scene-1", ct);
        await client.RemoveSceneExclusionAsync(address, key, 12, ct);

        await client.ReadHardlinkSettingAsync(address, key, ct);
        await client.ListImportableFilesAsync(address, key, "/config/library", ct);
        await client.AttachOwnedFilesAsync(
            address, key, new JsonArray(new JsonObject { ["path"] = "/config/library/a.mp4" }), ct);

        await client.SearchMonitoredAsync(
            address, key, WhisparrGeneration.V3, WhisparrEntityKind.Studio, [4], ct);

        // The scope change is driven on v2 only. v3 reads and replaces the resource through the
        // hand-composed date gate, whose route belongs to DeclaredRoutes.
        await client.ReadHistoryAsync(address, key, WhisparrGeneration.V2, 1, 10, ct);
        await client.ReadStudioAsync(address, key, WhisparrGeneration.V2, "studio-1", ct);
        await client.AddMonitoredStudioAsync(
            address, key, WhisparrGeneration.V2, "studio-1", MonitorScope.AllScenes, defaults, ct);
        await client.SetStudioMonitoredAsync(
            address, key, WhisparrGeneration.V2, 4, monitored: true, ct);
        await client.SetStudioScopeAsync(
            address, key, WhisparrGeneration.V2, 4, MonitorScope.AllScenes, ct);
        await client.SearchMonitoredAsync(
            address, key, WhisparrGeneration.V2, WhisparrEntityKind.Studio, [4], ct);
        await client.SetSceneMonitoredAsync(
            address, key, WhisparrGeneration.V2, 41, monitored: true, ct);
        await client.ReduceSiteSceneRowsAsync(address, key, 1, [1363738], ct);
        await client.ReadInstanceFolderAsync(address, key, WhisparrGeneration.V2, "/config/library/", ct);
    }

    // The seam's configuring half is the callback registration and nothing else. The types that can
    // reach an instance are named rather than gathered, because a holder nobody wrote down is a call
    // site nothing constrains. The registry's own entry type is named too, since it holds the
    // provider a call is made through.
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
            [
                typeof(GeneratedClientRegistry<Whisparr3Target>).Name,
                nameof(InstanceSiteNumberPort),
                "Lease",
                "Registration",
                nameof(StashDbCatalogue),
                nameof(ThePornDbCatalogue),
                nameof(Whisparr2Apis),
                nameof(Whisparr2Gateway),
                nameof(Whisparr3Apis),
                nameof(Whisparr3Gateway),
                nameof(WhisparrClient),
            ],
            TypesHoldingAnHttpClient().Order().ToList());

        // Each catalogue's own surface: every request it composes is the one read verb, so no member
        // takes a verb and none takes a route or a query key from a caller. Both are asserted, since
        // the point of the list above is that a holder of a client is a call site of its own.
        Assert.Empty(MembersTakingAVerbOrARouteOn(typeof(StashDbCatalogue)));
        Assert.Empty(MembersTakingAVerbOrARouteOn(typeof(ThePornDbCatalogue)));
    }

    // Every place in this extension that names the library run's job type, transcribed by hand. The
    // route handler is the only one that enqueues; the in-flight derivation reads the host's job
    // list for a run already started. A gathered set would agree with itself however many places
    // acquired the ability to start a run.
    private static readonly string[] NamingTheLibraryRun =
    [
        "WhisparrSync.EnqueueSyncRunAsync",
        "WhisparrSync.SyncRunInFlight",
    ];

    // Enumerated off the compiled call sites rather than driven. A run nobody placed leaves an empty
    // log either way, so a driven absence agrees with itself whatever the code does. The job id is a
    // const folded into every use site, so a second enqueue anywhere in this extension appears here
    // as a third method whichever surface added it.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryMutationIsOriginTagged)]
    public void NothingButTheRunRouteCanStartALibraryRun()
        => Assert.Equal(
            NamingTheLibraryRun.Order(StringComparer.Ordinal).ToList(),
            MembersNaming(global::WhisparrSync.Jobs.SyncLibraryJob.JobId)
                .Order(StringComparer.Ordinal)
                .ToList());

    // Scans each body for the string-load opcode and resolves the token after it against the
    // declaring module. A token that resolves to something else, or to nothing, is skipped, so a
    // byte that only looks like the opcode contributes nothing. Async bodies and lambdas are
    // reported under the member a reader wrote, so the assertion is about source and not about
    // compiler output.
    private static IEnumerable<string> MembersNaming(string literal)
        => typeof(IWhisparrClient).Assembly
            .GetTypes()
            .SelectMany(type => type
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)))
            .Where(member => Loads(member, literal))
            .Select(WrittenName)
            .Distinct(StringComparer.Ordinal);

    private const byte LoadString = 0x72;

    private const int TokenWidth = 4;

    private static bool Loads(MethodBase member, string literal)
    {
        if (member.GetMethodBody()?.GetILAsByteArray() is not { } il || member.Module is not { } module)
        {
            return false;
        }

        for (var at = 0; at + TokenWidth < il.Length; at++)
        {
            if (il[at] != LoadString)
            {
                continue;
            }

            string? loaded;
            try
            {
                loaded = module.ResolveString(BitConverter.ToInt32(il, at + 1));
            }
#pragma warning disable CA1031 // A token this is not a string token raises, and says nothing either way.
            catch (Exception)
            {
                continue;
            }
#pragma warning restore CA1031

            if (string.Equals(loaded, literal, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string WrittenName(MethodBase member)
    {
        var owner = member.DeclaringType!;
        while (owner.DeclaringType is not null
            && owner.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            owner = owner.DeclaringType;
        }

        var written = EmittedFor(member.Name)
            ?? EmittedFor(member.DeclaringType!.Name)
            ?? member.Name;

        return $"{owner.Name}.{written}";
    }

    private static string? EmittedFor(string emitted)
    {
        var opened = emitted.IndexOf('<', StringComparison.Ordinal);
        var closed = emitted.IndexOf('>', StringComparison.Ordinal);
        return opened == 0 && closed > 1 ? emitted[1..closed] : null;
    }

    private static IEnumerable<string> RoutesDeclaredByTheClient()
        => typeof(WhisparrClient)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string?)field.GetRawConstantValue())
            .OfType<string>()
            .Where(value => value.StartsWith("api/", StringComparison.Ordinal));

    // Reachable members only: a private helper taking one of the type's own constants is not a call
    // site a caller reaches, and including one would make this fire on correct code. A parameter
    // named query is not evidence here, because the provider request is a GraphQL document and
    // query is that document's own field name rather than a URL query.
    private static IEnumerable<string> MembersTakingAVerbOrARouteOn(Type type)
        => type
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic)
            .Where(method => !method.IsPrivate)
            .Where(method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(HttpMethod)
                || parameter.Name is "path" or "route" or "verb"))
            .Select(method => method.Name);

    // Closed over what a type holds, not over one field type: a gateway holds a registration cache,
    // the cache holds a provider, and the provider hands out a client already bound to the stored
    // credential. A field's generic arguments count as held.
    private static IEnumerable<string> TypesHoldingAnHttpClient()
    {
        var declared = typeof(IWhisparrClient).Assembly
            .GetTypes()
            .Where(IsWritten)
            .ToList();
        var reaching = new HashSet<Type>();
        List<Type> added;

        do
        {
            added = declared
                .Where(type => !reaching.Contains(type))
                .Where(type => FieldsOf(type).Any(field => Reaches(field.FieldType, reaching)))
                .ToList();
            reaching.UnionWith(added);
        }
        while (added.Count > 0);

        return reaching.Select(type => type.Name);
    }

    // A closure, an iterator and an async state machine each hold whatever the member they were
    // emitted for captured, so the set would otherwise name a type nobody wrote and no edit can add
    // to. The nesting chain is walked because only the outermost emitted type carries the attribute.
    private static bool IsWritten(Type type)
        => !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            && (type.DeclaringType is null || IsWritten(type.DeclaringType));

    private static FieldInfo[] FieldsOf(Type type)
        => type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static bool Reaches(Type held, IReadOnlySet<Type> reaching)
        => typeof(HttpClient).IsAssignableFrom(held)
            || typeof(IHttpClientFactory).IsAssignableFrom(held)
            || typeof(IServiceProvider).IsAssignableFrom(held)
            || reaching.Contains(
                held.IsConstructedGenericType ? held.GetGenericTypeDefinition() : held)
            || held.GenericTypeArguments.Any(argument => Reaches(argument, reaching));
}
