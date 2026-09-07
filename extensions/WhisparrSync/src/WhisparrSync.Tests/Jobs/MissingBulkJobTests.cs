using System.Reflection;
using Cove.Core.Auth;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

/// <summary>
/// What a bulk marking run carries across the host's parameter map, what it reports, and the
/// principal its reads run under.
/// </summary>
/// <remarks>
/// The decode cases are the ones the host can really produce. It hands a job whatever map was stored
/// with it, and a decode that threw inside the runner would be a faulted job rather than an answer.
/// </remarks>
public sealed class MissingBulkJobTests
{
    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public void ARunRoundTripsItsEntityAndItsTickedScenes()
    {
        var decoded = MissingBulkJob.Decode(
            MissingBulkJob.Encode(WhisparrEntityKind.Performer, 41, [FirstScene, SecondScene]));

        Assert.Equal(WhisparrEntityKind.Performer, decoded.Kind);
        Assert.Equal(41, decoded.CoveId);
        Assert.Equal([FirstScene, SecondScene], decoded.ProviderSceneIds);
    }

    /// <summary>
    /// A map nothing can be read out of answers no kind rather than the first one declared.
    /// </summary>
    /// <remarks>
    /// A run that defaulted to a kind would mark scenes under an entity nobody named. Each shape the
    /// host can hand over is driven on its own, so one covering case cannot stand for the rest.
    /// </remarks>
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
            MissingBulkJob.Decode(MissingBulkJob.Encode(WhisparrEntityKind.Studio, 4, [FirstScene]))
                .Kind);
    }

    /// <summary>An identifier list nothing can be read out of answers no identifiers.</summary>
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

    /// <summary>The run record reports counts and lists nothing.</summary>
    /// <remarks>
    /// A member holding identifiers would grow with the selection, and the one line a reader sees is
    /// a sentence rather than a list. Read off the declared members rather than off an instance, so a
    /// collection member added later fails here whatever a run happened to put in it.
    /// </remarks>
    [Fact]
    public void TheRunRecordReportsThreeCountsAndListsNothing()
    {
        var members = typeof(MissingBulkJob).Assembly
            .GetType("WhisparrSync.Jobs.MissingBulkRun")!
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(member => member.Name != "EqualityContract")
            .ToList();

        Assert.Equal(3, members.Count(member => member.PropertyType == typeof(int)));
        Assert.DoesNotContain(
            members,
            member => member.PropertyType != typeof(int)
                && member.PropertyType.IsAssignableTo(typeof(System.Collections.IEnumerable)));
    }

    /// <summary>The run's own reads happen as System.</summary>
    /// <remarks>
    /// A background run carries no principal of its own, and Cove's per-principal query filters
    /// answer an anonymous reader with zero rows and no error. The principal is read inside the run's
    /// own body, which is the only place the elevation can be observed.
    /// </remarks>
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
                MissingBulkJob.Encode(WhisparrEntityKind.Studio, 4, [FirstScene])),
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
}
