using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// Which identifier each scene card is known by, read from a real relational library.
/// </summary>
/// <remarks>
/// The namespace rule is the subject as much as the identifiers are. A row written under another
/// spelling of the same source names an identified scene, and a read comparing endpoint spellings as
/// strings would answer that an identified video carries none.
/// <para>
/// The conflicting case is the other half. A video whose matching rows name different scenes is
/// answered for by nothing, because which of them an outbound request would name depends on row
/// order.
/// </para>
/// </remarks>
public sealed class LibraryCardIdentityPortTests
{
    /// <summary>The standard spelling of the source the newer generation identifies against.</summary>
    /// <remarks>
    /// A different spelling from the one <see cref="MonitorHost.StoredEndpoint"/> stores, and
    /// deliberately: the two name one source under the host's own rule.
    /// </remarks>
    private const string StandardStashDbAddress = "https://stashdb.org/graphql";

    /// <summary>A spelling belonging to the OTHER generation's namespace.</summary>
    private const string OtherNamespaceEndpoint = "theporndb.net/graphql";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>A video carrying one matching row is known by that row's scene.</summary>
    [Fact]
    public async Task AVideoCarryingOneMatchingRowIsKnownByIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), MonitorHost.StoredEndpoint, FirstScene);

        Assert.Equal(
            [new LibraryCardIdentity(videoId, FirstScene)],
            await ResolveAsync(host, videoId));
    }

    /// <summary>
    /// A row written under a different spelling of the same source IS answered.
    /// </summary>
    /// <remarks>
    /// The host's own same-source rule decides it. Comparing the two as strings would leave a card
    /// silent for a reason that is not about the connected instance.
    /// </remarks>
    [Fact]
    public async Task ASpellingOfTheSameSourceIsAnsweredRatherThanComparedAsAString()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), StandardStashDbAddress, FirstScene);

        Assert.Equal(
            [new LibraryCardIdentity(videoId, FirstScene)],
            await ResolveAsync(host, videoId));
    }

    /// <summary>A video whose rows all name another source is known by nothing.</summary>
    [Fact]
    public async Task AVideoWhoseRowsNameAnotherSourceIsKnownByNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), OtherNamespaceEndpoint, FirstScene);

        Assert.Empty(await ResolveAsync(host, videoId));
    }

    /// <summary>A video carrying no identity row at all is known by nothing.</summary>
    [Fact]
    public async Task AVideoCarryingNoRowIsKnownByNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), null, null);

        Assert.Empty(await ResolveAsync(host, videoId));
    }

    /// <summary>
    /// A video carrying several matching rows that name different scenes is known by nothing.
    /// </summary>
    /// <remarks>
    /// Both rows match the source, so a read picking the first would offer whichever the database
    /// happened to return, and a card would then report a status about a scene nobody chose.
    /// </remarks>
    [Fact]
    public async Task AVideoNamingTwoDifferentScenesInOneNamespaceIsKnownByNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), MonitorHost.StoredEndpoint, FirstScene);
        await host.AddSceneIdentityAsync(videoId, StandardStashDbAddress, SecondScene);

        Assert.Empty(await ResolveAsync(host, videoId));
    }

    /// <summary>
    /// One page's answer names only the videos it asked about, in the order it asked.
    /// </summary>
    /// <remarks>
    /// The filter is the point. A read that materialized the library's own identity rows would answer
    /// the same identifiers and grow with the library.
    /// </remarks>
    [Fact]
    public async Task OnlyTheRequestedVideosAreAnsweredForAndInTheRequestedOrder()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var first = await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        var second = await host.SeedStudioSceneAsync(
            studioId, MonitorHost.StoredEndpoint, SecondScene);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, "unasked-for");

        Assert.Equal(
            [new LibraryCardIdentity(second, SecondScene), new LibraryCardIdentity(first, FirstScene)],
            await ResolveAsync(host, second, first));
    }

    private static async Task<IReadOnlyList<LibraryCardIdentity>> ResolveAsync(
        MonitorHost host, params int[] coveIds)
        => await host.CardIdentities.ResolveAsync(coveIds, WhisparrGeneration.V3, TestCt);
}
