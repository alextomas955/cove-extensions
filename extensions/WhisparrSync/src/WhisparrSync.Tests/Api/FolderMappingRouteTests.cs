using System.Net;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The two folder-mapping routes: what the read answers, and what a save does before it stores
/// anything.
/// </summary>
/// <remarks>
/// Driven over the shipped routes and the shipped addressing chain, with the instance's answers
/// supplied by a recording transport. The two cases that would be indistinguishable without a probe
/// are here on purpose: a path that exists as a directory holding nothing, and a path holding a file
/// of the wrong size. Both are paths a save taking the operator's word for it would store.
/// </remarks>
public sealed class FolderMappingRouteTests
{
    /// <summary>The library root as Cove has it, on a machine the instance does not share.</summary>
    private const string CoveRoot = "G:/Downloads/P";

    private const string Folder = CoveRoot + "/Blue Harbor";

    /// <summary>Where the operator says the instance holds that root.</summary>
    private const string Mapping = "/mnt/media";

    private const long SampleSize = 41;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>The read answers one line per root the instance could not see.</summary>
    [Fact]
    public async Task TheReadAnswersOneLinePerRootTheInstanceCouldNotSee()
    {
        await using var host = await MonitorHost.CreateAsync();
        await StoreAsync(host, stored => stored with
        {
            OutboundRefusals =
            [
                new OutboundRootRefusal
                {
                    Root = CoveRoot,
                    Refusal = FolderAgreementRefusal.NothingResolved,
                    PathsTried = [Mapping + "/Blue Harbor/scene.mp4"],
                },
            ],
            OutboundMappings =
                [new OutboundRootMapping { CoveRoot = CoveRoot, InstanceRoot = Mapping }],
        });

        var line = Assert.Single((await host.ReadFolderMappingsAsync()).Roots);

        Assert.Equal(CoveRoot, line.Root);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, line.Refusal);
        Assert.Equal([Mapping + "/Blue Harbor/scene.mp4"], line.PathsTried);
        Assert.Equal(Mapping, line.Mapping);
    }

    /// <summary>The read answers nothing at all where every root resolved.</summary>
    [Fact]
    public async Task TheReadAnswersNothingWhereEveryRootResolved()
    {
        await using var host = await MonitorHost.CreateAsync();

        Assert.Empty((await host.ReadFolderMappingsAsync()).Roots);
    }

    /// <summary>A mapping the instance holds the library's own file under is stored.</summary>
    [Fact]
    public async Task AMappingTheProbeResolvesIsStored()
    {
        var (host, _) = await ProbingHostAsync(Holding.TheSample);
        await using var driven = host;

        var saved = await host.SaveFolderMappingAsync(CoveRoot, Mapping);

        Assert.Equal(FolderMappingSaveOutcome.Stored, saved.Outcome);
        Assert.Null(saved.Refusal);
        Assert.Equal(
            Mapping,
            Assert.Single((await host.Options.LoadAsync(TestCt)).OutboundMappings).InstanceRoot);
    }

    /// <summary>
    /// A path that exists as a directory holding nothing stores nothing, and the answer names the
    /// path that was tried.
    /// </summary>
    /// <remarks>
    /// The case a save taking the operator's word for it cannot tell from a working mapping: the
    /// directory is there, and every folder handed under it would list nothing.
    /// </remarks>
    [Fact]
    public async Task ADirectoryHoldingNothingStoresNothingAndNamesThePathTried()
    {
        var (host, candidate) = await ProbingHostAsync(Holding.Nothing);
        await using var driven = host;

        var saved = await host.SaveFolderMappingAsync(CoveRoot, Mapping);

        Assert.Equal(FolderMappingSaveOutcome.Refused, saved.Outcome);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, saved.Refusal);
        Assert.Equal([candidate], saved.Tried);
        Assert.Empty((await host.Options.LoadAsync(TestCt)).OutboundMappings);
    }

    /// <summary>A path holding a file of the wrong size stores nothing.</summary>
    /// <remarks>
    /// The second case a probe is needed for: the name is right and the content is a different file,
    /// which is what a second library mounted at the same shape of path looks like.
    /// </remarks>
    [Fact]
    public async Task APathHoldingAFileOfAnotherLengthStoresNothing()
    {
        var (host, _) = await ProbingHostAsync(Holding.AnotherLength);
        await using var driven = host;

        var saved = await host.SaveFolderMappingAsync(CoveRoot, Mapping);

        Assert.Equal(FolderMappingSaveOutcome.Refused, saved.Outcome);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, saved.Refusal);
        Assert.Empty((await host.Options.LoadAsync(TestCt)).OutboundMappings);
    }

    /// <summary>A save for a path that is none of the host's library paths is refused.</summary>
    [Fact]
    public async Task ASaveForAPathThatIsNotALibraryRootIsRefused()
    {
        var (host, _) = await ProbingHostAsync(Holding.TheSample);
        await using var driven = host;

        var saved = await host.SaveFolderMappingAsync("H:/Elsewhere", Mapping);

        Assert.Equal(FolderMappingSaveOutcome.NotALibraryRoot, saved.Outcome);
        Assert.Empty((await host.Options.LoadAsync(TestCt)).OutboundMappings);
    }

    /// <summary>A blank path removes the mapping stored for that root.</summary>
    [Fact]
    public async Task ABlankPathRemovesTheStoredMapping()
    {
        var (host, _) = await ProbingHostAsync(Holding.TheSample);
        await using var driven = host;
        await host.SaveFolderMappingAsync(CoveRoot, Mapping);

        var removed = await host.SaveFolderMappingAsync(CoveRoot, "  ");

        Assert.Equal(FolderMappingSaveOutcome.Removed, removed.Outcome);
        Assert.Empty((await host.Options.LoadAsync(TestCt)).OutboundMappings);
    }

    /// <summary>A save that resolved clears the refusal stored for that root.</summary>
    /// <remarks>
    /// Without this the settings page would keep asking about a root the operator has just settled,
    /// until a run over it happened to write the entry away.
    /// </remarks>
    [Fact]
    public async Task ASaveThatResolvedClearsThatRootsRefusal()
    {
        var (host, _) = await ProbingHostAsync(Holding.TheSample);
        await using var driven = host;
        await StoreAsync(host, stored => stored with
        {
            OutboundRefusals =
            [
                new OutboundRootRefusal
                {
                    Root = CoveRoot,
                    Refusal = FolderAgreementRefusal.NothingResolved,
                },
            ],
        });

        await host.SaveFolderMappingAsync(CoveRoot, Mapping);

        Assert.Empty((await host.ReadFolderMappingsAsync()).Roots);
    }

    /// <summary>Both routes refuse a caller without the configure tier, and neither stores anything.</summary>
    /// <remarks>
    /// Paired with a caller who does hold the tier in the cases above. Without that control a 403
    /// could equally mean the handler is broken for everyone.
    /// </remarks>
    [Fact]
    public async Task BothRoutesRefuseACallerWithoutTheConfigureTier()
    {
        var (host, _) = await ProbingHostAsync(
            Holding.TheSample,
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));
        await using var driven = host;

        var read = await host.GetFolderMappingsAsync();
        var saved = await host.PutFolderMappingAsync(
            $$"""{"coveRoot":"{{CoveRoot}}","instancePath":"{{Mapping}}"}""");

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, saved.StatusCode);
        Assert.Empty((await host.Options.LoadAsync(TestCt)).OutboundMappings);
    }

    /// <summary>What the instance is standing at the mapped directory, in one of these cases.</summary>
    private enum Holding
    {
        TheSample,
        Nothing,
        AnotherLength,
    }

    /// <summary>The stored options folded through <paramref name="fold"/>.</summary>
    /// <remarks>
    /// Loaded and saved rather than written fresh, so the connection the host configured survives and
    /// the save route still resolves an instance.
    /// </remarks>
    private static async Task StoreAsync(
        MonitorHost host, Func<WhisparrSyncOptions, WhisparrSyncOptions> fold)
        => await host.Options.SaveAsync(
            fold(await host.Options.LoadAsync(TestCt)), TestCt);

    /// <summary>
    /// A host over one library root, one seeded file, and an instance answering
    /// <paramref name="holding"/> at the mapped directory, with the one path a probe under
    /// <see cref="Mapping"/> would ask about.
    /// </summary>
    private static async Task<(MonitorHost Host, string Candidate)> ProbingHostAsync(
        Holding holding, FakePrincipalAccessor? principal = null)
    {
        // The listing is composed from the file the library really seeded, so the probe is answered
        // about the path the product really asked about rather than one this case guessed.
        var listing = new[] { """{"parent":"/mnt/media/","directories":[],"files":[]}""" };

        var bytes = BodyRecordingHandler.AnsweringByPath(path => path switch
        {
            var route when route.EndsWith("/filesystem", StringComparison.Ordinal) => listing[0],
            var route when route.EndsWith("/rootfolder", StringComparison.Ordinal)
                => """[{"id":1,"path":"/data","accessible":true}]""",
            _ => "{}",
        });

        var host = await MonitorHost.CreateAsync(
            principal: principal,
            bytes: bytes,
            libraryConfig: new CoveConfiguration { CovePaths = [new CovePath { Path = CoveRoot }] });

        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var seeded = await host.SeedStudioFileAsync(studioId, Folder, SampleSize);
        var onInstance = seeded.Replace(CoveRoot, Mapping, StringComparison.Ordinal);

        if (holding != Holding.Nothing)
        {
            var size = holding == Holding.TheSample ? SampleSize : SampleSize + 1;
            listing[0] = $$"""
                {"parent":"{{Mapping}}/","directories":[],
                 "files":[{"path":"{{onInstance}}","size":{{size}}}]}
                """;
        }

        return (host, onInstance);
    }
}
