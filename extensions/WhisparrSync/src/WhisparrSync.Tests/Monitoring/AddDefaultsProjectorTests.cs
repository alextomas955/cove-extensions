using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.Invariants;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// The two stops taken before an add is composed, and the rule about which profile is taken.
/// </summary>
/// <remarks>
/// Half the cases drive the pure decision directly, because it is the whole rule and it needs no
/// environment. The other half drive the mapped route against the recording double, because a stop
/// is a claim about what left rather than about what a function returned.
/// <para>
/// Every emptiness assertion here is PAIRED, in this class, with a case that acts through the same
/// double. Read on its own an empty log agrees with itself whatever the code does: a double nothing
/// could ever have reached reports the same empty list as a refusal that worked.
/// </para>
/// <para>
/// What is asserted empty is the log filtered to the classes of work that CHANGE the instance. Both
/// stops happen after the instance has been read, so an assertion that nothing at all was sent would
/// be false for a reason that has nothing to do with the stop.
/// </para>
/// </remarks>
public sealed class AddDefaultsProjectorTests
{
    /// <summary>Two profiles in id order, so an id-ordering bug would go unseen against it.</summary>
    private const string SortedProfiles = """[{"id":1,"name":"HD-1080p"},{"id":4,"name":"Any"}]""";

    /// <summary>One profile whose id is the value the newer generation accepts and cannot use.</summary>
    private const string ZeroFirstProfile = """[{"id":0,"name":"Any"},{"id":4,"name":"HD-1080p"}]""";

    /// <summary>
    /// The profile a studio of its own already carries, offered first by the instance as well.
    /// </summary>
    /// <remarks>
    /// The name is what makes the case legible; nothing reads it. The point is that one id is taken
    /// once, so a second decision about whose profile wins has no place to live.
    /// </remarks>
    private const string StudiosOwnProfileOffered =
        """[{"id":7,"name":"Studio Default"},{"id":1,"name":"HD-1080p"}]""";

    private const string EmptyList = "[]";

    /// <summary>An instance offering no profile at all composes nothing.</summary>
    [Fact]
    public void AnEmptyProfileListRefusesAndComposesNothing()
    {
        var resolved = AddDefaultsProjector.From(EmptyList, MonitorHost.OneRootFolder);

        Assert.Equal(MonitorRefusalKind.NoQualityProfile, resolved.Refusal);
        Assert.Null(resolved.Defaults);
    }

    /// <summary>An instance offering no library root composes nothing.</summary>
    /// <remarks>
    /// A fresh instance is exactly this case, and the newer generation's add then answers a conflict
    /// carrying a full stack trace, so this stop is the difference between a sentence and that.
    /// </remarks>
    [Fact]
    public void AnEmptyRootFolderListRefusesAndComposesNothing()
    {
        var resolved = AddDefaultsProjector.From(MonitorHost.UnsortedProfiles, EmptyList);

        Assert.Equal(MonitorRefusalKind.NoRootFolder, resolved.Refusal);
        Assert.Null(resolved.Defaults);
    }

    /// <summary>Both lists empty answers the profile stop, which is the earlier one.</summary>
    /// <remarks>
    /// The order between the two stops is a decision rather than an accident: a user reads one
    /// sentence, and it names the value the composition needs first.
    /// </remarks>
    [Fact]
    public void BothListsEmptyAnswersTheProfileStopBecauseAProfileIsTheEarlierOne()
    {
        var resolved = AddDefaultsProjector.From(EmptyList, EmptyList);

        Assert.Equal(MonitorRefusalKind.NoQualityProfile, resolved.Refusal);
        Assert.Null(resolved.Defaults);
    }

    /// <summary>
    /// The profile taken is the first element AS RECEIVED, and not the lowest id and not the first
    /// alphabetically.
    /// </summary>
    /// <remarks>
    /// The list is ordered so that the three answers differ: taken as received it is 4, by id it is
    /// 1, and by name it is 1 as well ("Any" sorts before "HD-1080p" only in the sorted control). A
    /// sort anywhere on the path changes the answer and is reported here.
    /// </remarks>
    [Fact]
    public void TheProfileTakenIsTheFirstOfferedAndNotTheLowestIdAndNotTheFirstByName()
    {
        var asReceived = AddDefaultsProjector.From(
            MonitorHost.UnsortedProfiles, MonitorHost.OneRootFolder);

        Assert.Equal(MonitorRefusalKind.None, asReceived.Refusal);
        Assert.Equal(4, asReceived.Defaults?.QualityProfileId);

        // The same two profiles offered the other way round answer the other id, which is what makes
        // the assertion above about the ORDER rather than about the pair.
        var reversed = AddDefaultsProjector.From(SortedProfiles, MonitorHost.OneRootFolder);

        Assert.Equal(1, reversed.Defaults?.QualityProfileId);
    }

    /// <summary>
    /// A first-offered profile that is also the studio's own yields exactly that one id.
    /// </summary>
    /// <remarks>
    /// There is one decision here and not two. A scene a refresh creates inherits its studio's own
    /// profile, so nothing this product does chooses a profile for the catalogue, and a branch for
    /// the case where the two coincide would be a branch for a case that does not exist.
    /// </remarks>
    [Fact]
    public void AFirstProfileThatIsAlsoTheStudiosOwnYieldsExactlyThatOneIdAndNoAmbiguity()
    {
        var resolved = AddDefaultsProjector.From(
            StudiosOwnProfileOffered, MonitorHost.OneRootFolder);

        Assert.Equal(MonitorRefusalKind.None, resolved.Refusal);
        Assert.Equal(7, resolved.Defaults?.QualityProfileId);
        Assert.Equal("/config/library", resolved.Defaults?.RootFolderPath);
    }

    /// <summary>
    /// No composition ever carries a zero profile id, whatever the instance offered.
    /// </summary>
    /// <remarks>
    /// Stated as an invariant over the shapes rather than as one example, because the value is
    /// accepted and echoed back by the newer generation: an entity stored with it monitors and can
    /// never acquire anything, so there is no answer from the instance that reveals the mistake.
    /// </remarks>
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

    /// <summary>A body that is not a readable array of objects is the same stop as an empty one.</summary>
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

    /// <summary>The same leniency, and the same stop, for the root-folder list.</summary>
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

    /// <summary>
    /// The control the two emptiness assertions below rest on: this double DOES record what acted.
    /// </summary>
    /// <remarks>
    /// The add's own arguments are read back rather than the fact that a call happened. A count would
    /// hold for a call carrying the wrong profile just as well.
    /// </remarks>
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

    /// <summary>An instance offering no profile refuses with nothing acted upon.</summary>
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

    /// <summary>An instance offering no library root refuses with nothing acted upon.</summary>
    /// <remarks>
    /// The profile list is left as the fixture offers it, so the case reached is the root-folder stop
    /// rather than the earlier one.
    /// </remarks>
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

    /// <summary>
    /// An entity the instance already holds is never read for defaults at all.
    /// </summary>
    /// <remarks>
    /// The rule that such an entity keeps its own profile and root is enforced by not reading them,
    /// rather than by composing them and remembering not to send them. Read off the ordered verb log,
    /// so a read issued and discarded is a failure here.
    /// </remarks>
    [Fact]
    public async Task AnEntityTheInstanceAlreadyHoldsIsNeverReadForDefaults()
    {
        await using var host = await MonitorHost.CreateAsync();
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

    /// <summary>
    /// Every request the path issued that changes what the instance holds.
    /// </summary>
    /// <remarks>
    /// The class comes from the transcribed seam table rather than from the member name, so a verb
    /// added to the seam is classified by whoever wrote it down and not by a spelling rule here.
    /// </remarks>
    private static IEnumerable<ActingCall> Acts(MonitorHost host)
        => host.Client.Acting.Where(call =>
            OutboundSeam.VerbClassByMember.GetValueOrDefault(call.Verb) != WhisparrVerbClass.Read);
}
