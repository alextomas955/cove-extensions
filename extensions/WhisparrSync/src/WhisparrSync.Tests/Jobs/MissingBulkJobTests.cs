using Cove.Core.Auth;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

// The decode cases are the maps the host can really hand a job. A decode that threw inside the
// runner would be a faulted job rather than an answer.
public sealed class MissingBulkJobTests
{
    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public void ARunRoundTripsItsEntityAndItsTickedScenes()
    {
        var decoded = MissingBulkJob.Decode(
            MissingBulkJob.Encode(WhisparrEntityKind.Performer, 41, [FirstScene, SecondScene], MissingBulkVerb.Monitor));

        Assert.Equal(WhisparrEntityKind.Performer, decoded.Kind);
        Assert.Equal(41, decoded.CoveId);
        Assert.Equal([FirstScene, SecondScene], decoded.ProviderSceneIds);
    }

    // A run that defaulted to a kind would mark scenes under an entity nobody named. Each map shape
    // the host can hand over is driven on its own.
    [Fact]
    public void AMapNothingCanBeReadOutOfNamesNoKind()
    {
        Assert.Null(MissingBulkJob.Decode(null).Kind);
        Assert.Null(MissingBulkJob.Decode(new Dictionary<string, string>(StringComparer.Ordinal)).Kind);
        Assert.Null(
            MissingBulkJob.Decode(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = " ",
                }).Kind);
        Assert.Null(
            MissingBulkJob.Decode(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = "episode",
                }).Kind);

        // The discriminating control: the same reader on a map that DOES name a kind answers it, so
        // the four answers above are about those maps rather than about the reader.
        Assert.Equal(
            WhisparrEntityKind.Studio,
            MissingBulkJob.Decode(MissingBulkJob.Encode(WhisparrEntityKind.Studio, 4, [FirstScene], MissingBulkVerb.Monitor))
                .Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    public void AnIdentifierListNothingCanBeReadOutOfNamesNoScenes(string raw)
        => Assert.Empty(
            MissingBulkJob.Decode(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = "studio",
                    ["coveId"] = "4",
                    ["providerSceneIds"] = raw,
                }).ProviderSceneIds);

    // A background run carries no principal of its own, and Cove's per-principal query filters
    // answer an anonymous reader with zero rows and no error. The principal is read inside the run's
    // own body, the only place the elevation can be observed.
    [Fact]
    public async Task TheRunElevatesItsOwnScopeToSystem()
    {
        var principals = new FakePrincipalAccessor();
        var services = new ServiceCollection()
            .AddSingleton<ICurrentPrincipalAccessor>(principals)
            .BuildServiceProvider();

        PrincipalKind? seen = null;
        var run = await MissingBulkJob.RunAsync(
            MissingBulkJob.Decode(
                MissingBulkJob.Encode(WhisparrEntityKind.Studio, 4, [FirstScene], MissingBulkVerb.Monitor)),
            services.GetRequiredService<IServiceScopeFactory>(),
            (IServiceProvider scoped, CancellationToken _) =>
            {
                seen = scoped.GetRequiredService<ICurrentPrincipalAccessor>().Current?.Kind;
                return Task.FromResult<Func<string, CancellationToken, Task<WhisparrResponse?>>?>(
                    null);
            },
            TestCt);

        Assert.NotNull(run);
        Assert.Equal(PrincipalKind.System, seen);

        // Restored after the run, so the elevation is the run's own span rather than the process's.
        Assert.Null(principals.Current);
    }

    // Reported as monitoring either way, a reader who unmonitored a page would read that the run
    // had done the opposite of what they asked for.
    [Theory]
    [InlineData(
        MissingBulkRunOutcome.EverythingRefused, 0, 0, 3, MissingBulkVerb.Monitor,
        "Nothing was monitored: Whisparr refused all 3.")]
    [InlineData(
        MissingBulkRunOutcome.EverythingRefused, 0, 0, 3, MissingBulkVerb.Unmonitor,
        "Nothing was unmonitored: Whisparr refused all 3.")]
    [InlineData(
        MissingBulkRunOutcome.Completed, 2, 1, 0, MissingBulkVerb.Monitor,
        "2 monitored, 1 already monitored, 0 refused.")]
    [InlineData(
        MissingBulkRunOutcome.Completed, 2, 1, 0, MissingBulkVerb.Unmonitor,
        "2 unmonitored, 1 already unmonitored, 0 refused.")]
    internal void TheRunsOneLineCountsTheOutcomeAndNamesTheVerbItCarriedOut(
        MissingBulkRunOutcome outcome,
        int marked,
        int alreadyThere,
        int refused,
        MissingBulkVerb verb,
        string expected)
        => Assert.Equal(
            expected,
            MissingBulkJob.SummaryOf(
                new MissingBulkRun(outcome, marked, alreadyThere, refused), verb));
}
