using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Providers;
using WhisparrSync.Scene;
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
    /// The set is the claim: three requests stay hand-composed, and the reason each stays is stated
    /// where it is sent. The notification route carries a body built from the schema the instance
    /// answered with, the studio route carries the answer it just read with two members changed, and
    /// the exclusions route is read row by row so what it holds does not grow with the library. That
    /// last route string is also composed by the generated client for the two exclusion writes, so it
    /// is named in both sets.
    /// <para>
    /// Every other route is composed by a generated client and is no literal on this type, so those
    /// are transcribed in <see cref="GeneratedRoutes"/> and asserted against the operations this
    /// product calls. That two members of the whole seam can send a grabbing command name and each is
    /// declared on a separately obtained role of its own is
    /// <see cref="SafetyInvariantTests.TwoSeamMembersGrabAndEachIsDeclaredOnAGrabbingRoleOfItsOwn"/>,
    /// and that no body off a monitoring path names one of those commands is
    /// <see cref="SafetyInvariantTests.NoBodyOffAMonitoringPathCanNameAGrabbingCommand"/>.
    /// </para>
    /// </remarks>
    private static readonly string[] DeclaredRoutes =
    [
        "api/v3/notification",
        "api/v3/studio",
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
    /// <para>
    /// Each row names its own generation, because the two generations serve the same route strings and
    /// each declares its operations in an assembly of its own. The generation is what picks the
    /// assembly a row is reflected against. The routes are read as a union, since a driven request
    /// carries nothing saying which generation issued it.
    /// </para>
    /// </remarks>
    private static readonly (WhisparrGeneration Generation, string Api, string Operation, string Route)[]
        GeneratedRoutes =
    [
        (WhisparrGeneration.V3, "ISystemApi", "GetSystemStatusAsync", "api/v3/system/status"),
        (WhisparrGeneration.V3, "INotificationApi", "ListNotificationAsync", "api/v3/notification"),
        (WhisparrGeneration.V3, "INotificationApi", "ListNotificationSchemaAsync", "api/v3/notification/schema"),
        (WhisparrGeneration.V3, "IRootFolderApi", "ListRootFolderAsync", "api/v3/rootfolder"),
        (WhisparrGeneration.V3, "IQualityProfileApi", "ListQualityProfileAsync", "api/v3/qualityprofile"),
        (WhisparrGeneration.V3, "IHistoryApi", "GetHistoryAsync", "api/v3/history"),
        (WhisparrGeneration.V3, "IStudioApi", "GetStudioByStudioForeignIdAsync", "api/v3/studio"),
        (WhisparrGeneration.V3, "IStudioApi", "CreateStudioAsync", "api/v3/studio"),
        (WhisparrGeneration.V3, "IStudioEditorApi", "PutStudioEditorAsync", "api/v3/studio/editor"),
        (WhisparrGeneration.V3, "IPerformerApi", "GetPerformerByPerformerForeignIdAsync", "api/v3/performer"),
        (WhisparrGeneration.V3, "IPerformerApi", "CreatePerformerAsync", "api/v3/performer"),
        (WhisparrGeneration.V3, "IPerformerEditorApi", "PutPerformerEditorAsync", "api/v3/performer/editor"),
        (WhisparrGeneration.V3, "IMovieApi", "CreateMovieAsync", "api/v3/movie"),
        (WhisparrGeneration.V3, "IMovieApi", "ListMovieAsync", "api/v3/movie"),
        (WhisparrGeneration.V3, "IMovieApi", "PatchMovieByIdAsync", "api/v3/movie"),
        (WhisparrGeneration.V3, "IManualImportApi", "ListManualImportAsync", "api/v3/manualimport"),
        (WhisparrGeneration.V3, "IMediaManagementConfigApi", "GetMediaManagementConfigAsync", "api/v3/config/mediamanagement"),
        (WhisparrGeneration.V3, "CommandApi", "SendCommandAsync", "api/v3/command"),
        (WhisparrGeneration.V3, "ICommandApi", "GetCommandByIdAsync", "api/v3/command"),
        (WhisparrGeneration.V3, "IImportListExclusionApi", "CreateExclusionsAsync", "api/v3/exclusions"),
        (WhisparrGeneration.V3, "IImportListExclusionApi", "DeleteExclusionsAsync", "api/v3/exclusions"),
        (WhisparrGeneration.V3, "IFileSystemApi", "GetFileSystemAsync", "api/v3/filesystem"),
        (WhisparrGeneration.V2, "IHistoryApi", "GetHistoryAsync", "api/v3/history"),
        (WhisparrGeneration.V2, "ISeriesApi", "ListSeriesAsync", "api/v3/series"),
        (WhisparrGeneration.V2, "ISeriesApi", "CreateSeriesAsync", "api/v3/series"),
        (WhisparrGeneration.V2, "ISeriesEditorApi", "PutSeriesEditorAsync", "api/v3/series/editor"),
        (WhisparrGeneration.V2, "ISeasonPassApi", "CreateSeasonPassAsync", "api/v3/seasonpass"),
        (WhisparrGeneration.V2, "CommandApi", "SendCommandAsync", "api/v3/command"),
        (WhisparrGeneration.V2, "IEpisodeApi", "ListEpisodeAsync", "api/v3/episode"),
        (WhisparrGeneration.V2, "IEpisodeApi", "PutEpisodeMonitorAsync", "api/v3/episode/monitor"),
        (WhisparrGeneration.V2, "IFileSystemApi", "GetFileSystemAsync", "api/v3/filesystem"),
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
    /// Reflected over the generated assembly, because the claim is about the transcribed set: a name in
    /// <see cref="GeneratedRoutes"/> that the generated client does not declare would otherwise be a
    /// route nobody can reach and a line nobody removed.
    /// <para>
    /// Each row is asserted against the one assembly its own generation ships. An operation name only
    /// one generation declares would otherwise satisfy a row naming the other, and the check would then
    /// be about neither.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OnlyAnExplicitSearchGrabs)]
    public void TheGeneratedClientDeclaresEveryOperationThisProductNames()
    {
        Assert.All(
            GeneratedRoutes,
            named =>
            {
                var (generated, prefix) = GeneratedSurfaceOf(named.Generation);
                var api = generated.GetType(prefix + named.Api);
                Assert.NotNull(api);
                Assert.Contains(
                    api.GetMethods(),
                    method => string.Equals(method.Name, named.Operation, StringComparison.Ordinal));
            });
    }

    /// <summary>The assembly and namespace prefix <paramref name="generation"/> declares under.</summary>
    private static (Assembly Generated, string Prefix) GeneratedSurfaceOf(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => (typeof(Whisparr3.Net.Whisparr3Options).Assembly, "Whisparr3.Net.Api."),
            WhisparrGeneration.V2 => (typeof(Whisparr2.Net.Whisparr2Options).Assembly, "Whisparr2.Net.Api."),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    /// <summary>
    /// The routes the generated client puts on the wire for this product are exactly the transcribed
    /// set.
    /// </summary>
    /// <remarks>
    /// Driven rather than read off a constant. The generated client composes the route, so the only
    /// honest source for what it composes is a request it made: a transcribed route compared against
    /// another transcription would agree with itself whatever the client sent.
    /// <para>
    /// An equality in both directions. A member reaching a route nobody wrote down fails here, and so
    /// does a transcribed route no call drives. The grabbing member is driven too, because the claim is
    /// about which routes exist and not about which of them acquires.
    /// </para>
    /// <para>
    /// This says nothing about which generation issued a request. Both serve the same route strings,
    /// and the recorded path carries no generation.
    /// <see cref="TheGeneratedClientDeclaresEveryOperationThisProductNames"/> is what pins a row to the
    /// generation that declares it.
    /// </para>
    /// <para>
    /// Nothing hand-composed is driven. The notification create and update reach routes
    /// <see cref="GeneratedRoutes"/> does not name and belong to <see cref="DeclaredRoutes"/>. The
    /// exclusions route string is in both sets, because the read composes it by hand and the two
    /// writes reach it through the generated client.
    /// </para>
    /// </remarks>
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
            GeneratedRoutes.Select(route => route.Route).Distinct().Order().ToList(),
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
            .Select(route => route.Route)
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

        // The scope change is driven on v2 only. v3 reads and replaces
        // the resource through the hand-composed date gate, whose route belongs to DeclaredRoutes.
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

    /// <summary>
    /// Nothing outside the one seam can reach an instance at all.
    /// </summary>
    /// <remarks>
    /// Asserts absence. The seam's configuring half is the callback registration and nothing else.
    /// <para>
    /// The types that can reach an instance are named: the instance client, the two metadata
    /// catalogues, the site-number port, and the types each generation's gateway reaches its
    /// generated client through. A catalogue reads a third party rather than an instance, and it
    /// composes one verb on one route, so it declares no member through which a mutation could be
    /// expressed; the site-number port composes one read on the lookup route and is the same shape.
    /// The registry's own entry type is named too, since it holds the provider a call is made
    /// through. That is asserted here rather than assumed, because a holder nobody wrote down is a
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
            [
                typeof(GeneratedClientRegistry<Whisparr3Target>).Name,
                nameof(InstanceSiteNumberPort),
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

    /// <summary>
    /// Every place in this extension that can name the library run's job type, transcribed by hand
    /// with what each one does with it.
    /// </summary>
    /// <remarks>
    /// Two entries and no more. The route handler is the only one that enqueues, and the in-flight
    /// derivation reads the host's job list for a run already started. Transcribed rather than
    /// gathered, for the reason every registry in this group is: a gathered set agrees with itself
    /// however many places acquired the ability to start a run.
    /// </remarks>
    private static readonly string[] NamingTheLibraryRun =
    [
        "WhisparrSync.EnqueueSyncRunAsync",
        "WhisparrSync.SyncRunInFlight",
    ];

    /// <summary>
    /// Nothing but the run route can start a library run: no background pass, no timer, no schedule
    /// and nothing on the import path names the job type at all.
    /// </summary>
    /// <remarks>
    /// Enumerated off the compiled call sites rather than driven. An absence driven as a scenario
    /// agrees with itself whatever the code does, because a run nobody placed leaves an empty log
    /// either way; a call site the assembly declares is a fact that survives the scenario nobody
    /// wrote.
    /// <para>
    /// The type is a const folded into every use site, so a second enqueue anywhere in this
    /// extension appears here as a third method whichever surface added it. The background worker's
    /// own members, the import slice and every job registration are covered by that one equality
    /// rather than by a list naming them, which would leave whatever it did not name uncovered.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryMutationIsOriginTagged)]
    public void NothingButTheRunRouteCanStartALibraryRun()
        => Assert.Equal(
            NamingTheLibraryRun.Order(StringComparer.Ordinal).ToList(),
            MembersNaming(global::WhisparrSync.Jobs.SyncLibraryJob.JobId)
                .Order(StringComparer.Ordinal)
                .ToList());

    /// <summary>
    /// Every member this extension declares whose body loads <paramref name="literal"/>, named by the
    /// member a reader wrote rather than by the one the compiler emitted.
    /// </summary>
    /// <remarks>
    /// A body is scanned for the string-load opcode and the four-byte token after it is resolved
    /// against the declaring module. A token that resolves to something else, or to nothing, is not
    /// this literal and is skipped, so a byte that only looks like the opcode contributes nothing.
    /// <para>
    /// An async body and a lambda are compiled onto members named after the one they came from, and
    /// both spellings carry that name between angle brackets. Reporting the emitted name would make
    /// the assertion about compiler output rather than about which member a reader can see.
    /// </para>
    /// </remarks>
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

    /// <summary>The string-load opcode, and the width of the metadata token that follows it.</summary>
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

    /// <summary>The member a reader wrote, that <paramref name="member"/> was emitted for.</summary>
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

    /// <summary>The member name between the angle brackets of <paramref name="emitted"/>, or null.</summary>
    private static string? EmittedFor(string emitted)
    {
        var opened = emitted.IndexOf('<', StringComparison.Ordinal);
        var closed = emitted.IndexOf('>', StringComparison.Ordinal);
        return opened == 0 && closed > 1 ? emitted[1..closed] : null;
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
    /// <remarks>
    /// Closed over what a type holds and not over one field type: a gateway holds a registration
    /// cache, the cache holds a provider, and the provider hands out a client already bound to the
    /// stored credential. A holder one further indirection away is named here for that reason, and a
    /// field's generic arguments count as held.
    /// </remarks>
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
