using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.Invariants;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// Every emptiness assertion here is paired with a case that acts through the same double: read on
// its own, an empty log agrees with itself whatever the code does. What is asserted empty is the
// log filtered to the verbs that change the instance, because both stops happen after the instance
// has been read.
public sealed class AddDefaultsProjectorTests
{
    // In id order, so it answers the same id whether the code takes the first offered or the lowest.
    private const string SortedProfiles = """[{"id":1,"name":"HD-1080p"},{"id":4,"name":"Any"}]""";

    private const string ZeroFirstProfile = """[{"id":0,"name":"Any"},{"id":4,"name":"HD-1080p"}]""";

    // The profile the studio already carries is also the one the instance offers first, so the two
    // candidates coincide.
    private const string StudiosOwnProfileOffered =
        """[{"id":7,"name":"Studio Default"},{"id":1,"name":"HD-1080p"}]""";

    private const string EmptyList = "[]";

    private const string SceneOnTheProvider = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    [Fact]
    public void AnEmptyProfileListRefusesAndComposesNothing()
    {
        var resolved = AddDefaultsProjector.From(EmptyList, MonitorHost.OneRootFolder);

        Assert.Equal(MonitorRefusalKind.NoQualityProfile, resolved.Refusal);
        Assert.Null(resolved.Defaults);
    }

    // A fresh instance is exactly this case, and v3's add then answers a conflict carrying a full
    // stack trace.
    [Fact]
    public void AnEmptyRootFolderListRefusesAndComposesNothing()
    {
        var resolved = AddDefaultsProjector.From(MonitorHost.UnsortedProfiles, EmptyList);

        Assert.Equal(MonitorRefusalKind.NoRootFolder, resolved.Refusal);
        Assert.Null(resolved.Defaults);
    }

    // The order between the two stops is a decision: the user reads one sentence, and it names the
    // value the composition needs first.
    [Fact]
    public void BothListsEmptyAnswersTheProfileStopBecauseAProfileIsTheEarlierOne()
    {
        var resolved = AddDefaultsProjector.From(EmptyList, EmptyList);

        Assert.Equal(MonitorRefusalKind.NoQualityProfile, resolved.Refusal);
        Assert.Null(resolved.Defaults);
    }

    // The fixture is ordered so the candidate answers differ: first as received is 4, lowest id is
    // 1, first by name is 1. A sort anywhere on the path changes the answer.
    [Fact]
    public void TheProfileTakenIsTheFirstOfferedAndNotTheLowestIdAndNotTheFirstByName()
    {
        var asReceived = AddDefaultsProjector.From(
            MonitorHost.UnsortedProfiles, MonitorHost.OneRootFolder);

        Assert.Equal(MonitorRefusalKind.None, asReceived.Refusal);
        Assert.Equal(4, asReceived.Defaults?.QualityProfileId);

        // The same two profiles offered the other way round answer the other id, which makes the
        // assertion above about the order rather than about the pair.
        var reversed = AddDefaultsProjector.From(SortedProfiles, MonitorHost.OneRootFolder);

        Assert.Equal(1, reversed.Defaults?.QualityProfileId);
    }

    // A scene a refresh creates inherits its studio's own profile, so nothing here chooses a
    // profile for the catalogue and the two candidates never have to be reconciled.
    [Fact]
    public void AFirstProfileThatIsAlsoTheStudiosOwnYieldsExactlyThatOneIdAndNoAmbiguity()
    {
        var resolved = AddDefaultsProjector.From(
            StudiosOwnProfileOffered, MonitorHost.OneRootFolder);

        Assert.Equal(MonitorRefusalKind.None, resolved.Refusal);
        Assert.Equal(7, resolved.Defaults?.QualityProfileId);
        Assert.Equal("/config/library", resolved.Defaults?.RootFolderPath);
    }

    // v3 accepts a zero profile id and echoes it back. An entity stored with it monitors and can
    // never acquire anything, so no answer from the instance reveals the mistake.
    [Theory]
    [InlineData(ZeroFirstProfile)]
    [InlineData("""[{"id":0,"name":"Any"}]""")]
    [InlineData("""[{"id":-1,"name":"Any"}]""")]
    [InlineData("""[{"name":"Any"}]""")]
    [InlineData("""[{"id":"4","name":"Any"}]""")]
    public void NoCompositionEverCarriesAProfileIdBelowOne(string offered)
    {
        var resolved = AddDefaultsProjector.From(offered, MonitorHost.OneRootFolder);

        Assert.Equal(MonitorRefusalKind.NoQualityProfile, resolved.Refusal);
        Assert.Null(resolved.Defaults);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"profiles":[{"id":4}]}""")]
    [InlineData("[4]")]
    public void AnUnreadableProfileListIsTheSameStopAsAnEmptyOne(string? offered)
    {
        Assert.Equal(
            MonitorRefusalKind.NoQualityProfile,
            AddDefaultsProjector.From(offered, MonitorHost.OneRootFolder).Refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""[{"path":""}]""")]
    [InlineData("""[{"path":"   "}]""")]
    [InlineData("""[{"id":1,"accessible":true}]""")]
    public void AnUnreadableRootFolderListIsTheSameStopAsAnEmptyOne(string? offered)
    {
        Assert.Equal(
            MonitorRefusalKind.NoRootFolder,
            AddDefaultsProjector.From(MonitorHost.UnsortedProfiles, offered).Refusal);
    }

    // The control the two emptiness assertions below rest on: this double does record what acted.
    // The add's own arguments are read back, because a count would hold for a call carrying the
    // wrong profile.
    [Fact]
    public async Task APathThatDoesActRecordsTheProfileAndRootItSent()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        var add = Assert.Single(Acts(host));
        Assert.Equal(nameof(IWhisparrStudioActing.AddMonitoredStudioAsync), add.Verb);
        Assert.Equal(4, add.Defaults?.QualityProfileId);
        Assert.Equal("/config/library", add.Defaults?.RootFolderPath);
    }

    [Fact]
    public async Task AnInstanceOfferingNoProfileRefusesWithNothingActedUpon()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrClient.ReadQualityProfilesAsync), MonitorHost.Json(200, EmptyList));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NoQualityProfile, view.Refusal);
        Assert.False(view.Monitored);
        Assert.Empty(Acts(host));
    }

    // The profile list is left as the fixture offers it, so the stop reached is the root-folder one
    // rather than the earlier one.
    [Fact]
    public async Task AnInstanceOfferingNoRootFolderRefusesWithNothingActedUpon()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrClient.ReadRootFoldersAsync), MonitorHost.Json(200, EmptyList));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NoRootFolder, view.Refusal);
        Assert.False(view.Monitored);
        Assert.Empty(Acts(host));
    }

    // Covered per route, because a stop the entity path takes says nothing about a route that
    // composes its add somewhere else. The scene route refuses in its own vocabulary.
    [Theory]
    [InlineData(
        nameof(IWhisparrClient.ReadQualityProfilesAsync),
        SceneRefusalKind.InstanceOffersNoQualityProfile)]
    [InlineData(
        nameof(IWhisparrClient.ReadRootFoldersAsync),
        SceneRefusalKind.InstanceOffersNoRootFolder)]
    public async Task ASceneAddTakesTheSameTwoStopsAndActsOnNothing(
        string offeringNothing, SceneRefusalKind refusal)
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(
                nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
                MonitorHost.Json(200, "[]"))
            .Answering(offeringNothing, MonitorHost.Json(200, EmptyList));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var coveId = await host.SeedStudioSceneAsync(
            studioId, MonitorHost.StoredEndpoint, SceneOnTheProvider);

        var result = await host.SceneActionAsync(coveId, "add");

        Assert.Equal(refusal, result.Refusal);
        Assert.Empty(Acts(host));
    }

    // The projector declares one member answering one of two refusals, so a third stop would have
    // no member to be read off. Nothing reads the instance's indexer list.
    [Fact]
    public void TheProjectorAnswersTheseTwoRefusalsAndNoOther()
    {
        var refusals = new[] { EmptyList, MonitorHost.OneRootFolder }
            .SelectMany(offered => new[]
            {
                AddDefaultsProjector.From(offered, EmptyList).Refusal,
                AddDefaultsProjector.From(EmptyList, offered).Refusal,
                AddDefaultsProjector.From(offered, MonitorHost.OneRootFolder).Refusal,
            })
            .Where(refusal => refusal != MonitorRefusalKind.None)
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(
            [MonitorRefusalKind.NoQualityProfile, MonitorRefusalKind.NoRootFolder],
            refusals);
    }

    // Such an entity keeps its own profile and root because the defaults are never read, not
    // because they are composed and dropped. A read issued and discarded fails here.
    [Fact]
    public async Task AnEntityTheInstanceAlreadyHoldsIsNeverReadForDefaults()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrCore.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(200, """{"id":1,"foreignId":"x","monitored":false}"""),
            MonitorHost.Json(200, """{"id":1,"foreignId":"x","monitored":true}"""));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await host.MonitorAsync(studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.DoesNotContain(nameof(IWhisparrClient.ReadQualityProfilesAsync), host.Client.Verbs);
        Assert.DoesNotContain(nameof(IWhisparrClient.ReadRootFoldersAsync), host.Client.Verbs);
    }

    // The class comes from the transcribed seam table rather than from the member name, so a verb
    // added to the seam is classified where it was written down and not by a spelling rule here.
    private static IEnumerable<ActingCall> Acts(MonitorHost host)
        => host.Client.Acting.Where(call =>
            OutboundSeam.VerbClassByMember.GetValueOrDefault(call.Verb) != WhisparrVerbClass.Read);

    // A hard link cannot cross a filesystem. An instance reaching two of the library's volumes mounts
    // each under its own leading segment, so a folder is registered on the root sharing that segment
    // rather than on whichever root the instance happened to list first.
    [Fact]
    public void AFolderIsRegisteredOnTheRootThatReachesItsOwnVolume()
    {
        string[] roots = ["/i/cove-dev/whisparr/library", "/g/tmp/whisparr-library"];

        Assert.Equal(
            "/g/tmp/whisparr-library",
            AddDefaultsProjector.RootReachingFrom("/g/Downloads/P/videos/Studio", roots));
        Assert.Equal(
            "/i/cove-dev/whisparr/library",
            AddDefaultsProjector.RootReachingFrom("/i/Downloads/P/videos/Studio", roots));
    }

    // A folder on a volume no declared root sits on answers none, so nothing is registered where a
    // link would copy the file instead.
    [Fact]
    public void AFolderNoRootReachesAnswersNone()
    {
        Assert.Null(
            AddDefaultsProjector.RootReachingFrom(
                "/elsewhere/videos", ["/i/cove-dev/whisparr/library"]));
    }
}
