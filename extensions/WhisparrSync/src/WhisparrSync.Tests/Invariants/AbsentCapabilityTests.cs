using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Providers;
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

    // The seam's configuring half is the callback registration and nothing else. The types that can
    // reach an instance are named because a holder nobody wrote down is a call site nothing
    // constrains. The registry's own entry type is named too, since it holds the provider a call is
    // made through.
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
                nameof(ConnectionTester),
                typeof(GeneratedClientRegistry<Whisparr3Target>).Name,
                nameof(InstanceSiteNumberPort),
                "Lease",
                nameof(ProviderCatalogueChoice),
                "Registration",
                nameof(StashDbCatalogue),
                nameof(ThePornDbCatalogue),
                nameof(Whisparr2Apis),
                nameof(Whisparr2Gateway),
                nameof(Whisparr3Apis),
                nameof(Whisparr3Gateway),
                nameof(WhisparrInstanceFactory),
                nameof(WhisparrTransport),
                nameof(WhisparrV2Instance),
                nameof(WhisparrV3Instance),
            ],
            TypesHoldingAnHttpClient().Order().ToList());

        // Each catalogue's own surface: every request it composes is the one read verb, so no member
        // takes a verb and none takes a route or a query key from a caller. Both are asserted, since
        // the point of the list above is that a holder of a client is a call site of its own.
        Assert.Empty(MembersTakingAVerbOrARouteOn(typeof(StashDbCatalogue)));
        Assert.Empty(MembersTakingAVerbOrARouteOn(typeof(ThePornDbCatalogue)));
    }

    // Every place in this extension that names the library run's job type. The route handler is the
    // only one that enqueues; the in-flight derivation reads the host's job list for a run already
    // started.
    private static readonly string[] NamingTheLibraryRun =
    [
        "WhisparrSync.EnqueueSyncRunAsync",
        "WhisparrSync.SyncRunInFlight",
    ];

    // Enumerated off the compiled call sites rather than driven, because a run nobody placed leaves
    // an empty log either way. The job id is a const folded into every use site, so a second
    // enqueue anywhere in this extension appears here as a third method whichever surface added it.
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryMutationIsOriginTagged)]
    public void NothingButTheRunRouteCanStartALibraryRun()
        => Assert.Equal(
            NamingTheLibraryRun.Order(StringComparer.Ordinal).ToList(),
            MembersNaming(global::WhisparrSync.Jobs.SyncLibraryJob.JobId)
                .Order(StringComparer.Ordinal)
                .ToList());

    // Scans each body for the string-load opcode and resolves the token after it against the
    // declaring module. A token resolving to something else, or to nothing, is skipped, so a byte
    // that only looks like the opcode contributes nothing. Async bodies and lambdas are reported
    // under the member a reader wrote, so the assertion is about source rather than compiler
    // output.
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

    private static IEnumerable<string> RoutesDeclaredByTheSeam()
        => OutboundSeamTypes.DeclaredLiterals()
            .Where(value => value.StartsWith("api/", StringComparison.Ordinal));

    // Reachable members only: a private helper taking one of the type's own constants is not a call
    // site a caller reaches, and including one would make this fire on correct code. A parameter
    // named query is not evidence, because the provider request is a GraphQL document and query is
    // that document's own field name.
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
