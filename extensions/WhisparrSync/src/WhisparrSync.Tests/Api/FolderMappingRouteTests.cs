using System.Net;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Api;

public sealed class FolderMappingRouteTests
{
    // A Windows library root and a Linux instance path: the instance does not share Cove's machine.
    private const string CoveRoot = "G:/Downloads/P";

    private const string Folder = CoveRoot + "/Blue Harbor";

    private const string Mapping = "/mnt/media";

    private const long SampleSize = 41;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheReadAnswersOneLinePerRootTheInstanceCouldNotSee()
    {
        await using var host = await MonitorHost.CreateAsync();
        await StoreAsync(host, stored => stored.WithInstance(
            outboundRefusals:
            [
                new OutboundRootRefusal
                {
                    Root = CoveRoot,
                    Refusal = FolderAgreementRefusal.NothingResolved,
                    PathsTried = [Mapping + "/Blue Harbor/scene.mp4"],
                },
            ],
            outboundMappings:
                [new OutboundRootMapping { CoveRoot = CoveRoot, InstanceRoot = Mapping }]));

        var line = Assert.Single((await host.ReadFolderMappingsAsync()).Roots);

        Assert.Equal(CoveRoot, line.Root);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, line.Refusal);
        Assert.Equal([Mapping + "/Blue Harbor/scene.mp4"], line.PathsTried);
        Assert.Equal(Mapping, line.Mapping);
    }

    [Fact]
    public async Task TheReadAnswersNothingWhereNothingIsStored()
    {
        await using var host = await MonitorHost.CreateAsync();

        Assert.Empty((await host.ReadFolderMappingsAsync()).Roots);
    }

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
            Assert.Single((await host.Options.LoadAsync(TestCt)).Instance().OutboundMappings).InstanceRoot);
    }

    // A save that took the operator's word for it cannot tell this from a working mapping: the
    // directory is there and lists nothing.
    [Fact]
    public async Task ADirectoryHoldingNothingStoresNothingAndNamesThePathTried()
    {
        var (host, candidate) = await ProbingHostAsync(Holding.Nothing);
        await using var driven = host;

        var saved = await host.SaveFolderMappingAsync(CoveRoot, Mapping);

        Assert.Equal(FolderMappingSaveOutcome.Refused, saved.Outcome);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, saved.Refusal);
        Assert.Equal([candidate], saved.Tried);
        Assert.Empty((await host.Options.LoadAsync(TestCt)).Instance().OutboundMappings);
    }

    // The other case a probe is needed for: the name is right and the content is a different file,
    // which is what a second library mounted at the same shape of path looks like.
    [Fact]
    public async Task APathHoldingAFileOfAnotherLengthStoresNothing()
    {
        var (host, _) = await ProbingHostAsync(Holding.AnotherLength);
        await using var driven = host;

        var saved = await host.SaveFolderMappingAsync(CoveRoot, Mapping);

        Assert.Equal(FolderMappingSaveOutcome.Refused, saved.Outcome);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, saved.Refusal);
        Assert.Empty((await host.Options.LoadAsync(TestCt)).Instance().OutboundMappings);
    }

    [Fact]
    public async Task ASaveForAPathThatIsNotALibraryRootIsRefused()
    {
        var (host, _) = await ProbingHostAsync(Holding.TheSample);
        await using var driven = host;

        var saved = await host.SaveFolderMappingAsync("H:/Elsewhere", Mapping);

        Assert.Equal(FolderMappingSaveOutcome.NotALibraryRoot, saved.Outcome);
        Assert.Empty((await host.Options.LoadAsync(TestCt)).Instance().OutboundMappings);
    }

    [Fact]
    public async Task ABlankPathRemovesTheStoredMappingAndLeavesThatRootNoLine()
    {
        var (host, _) = await ProbingHostAsync(Holding.TheSample);
        await using var driven = host;
        await host.SaveFolderMappingAsync(CoveRoot, Mapping);

        var removed = await host.SaveFolderMappingAsync(CoveRoot, "  ");

        Assert.Equal(FolderMappingSaveOutcome.Removed, removed.Outcome);
        Assert.Empty((await host.Options.LoadAsync(TestCt)).Instance().OutboundMappings);
        Assert.Empty((await host.ReadFolderMappingsAsync()).Roots);
    }

    // The line stays after the refusal is settled, so the operator can still read and withdraw the
    // path. A root that vanished on resolving would leave no way back to it.
    [Fact]
    public async Task ASaveThatResolvedReadsAsOneLineCarryingThePathAndNoReason()
    {
        var (host, _) = await ProbingHostAsync(Holding.TheSample);
        await using var driven = host;
        await StoreAsync(host, stored => stored.WithInstance(
            outboundRefusals:
            [
                new OutboundRootRefusal
                {
                    Root = CoveRoot,
                    Refusal = FolderAgreementRefusal.NothingResolved,
                },
            ]));

        await host.SaveFolderMappingAsync(CoveRoot, Mapping);

        var line = Assert.Single((await host.ReadFolderMappingsAsync()).Roots);
        Assert.Equal(CoveRoot, line.Root);
        Assert.Equal(Mapping, line.Mapping);
        Assert.Null(line.Refusal);
        Assert.Empty(line.PathsTried);
    }

    // Both routes sit at the configure tier. The cases above drive the same routes with a caller
    // who holds it, so a 403 here is the gate and not a handler broken for everyone.
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
        Assert.Empty((await host.Options.LoadAsync(TestCt)).Instance().OutboundMappings);
    }

    // The probe runs against the generation that was selected when the save started. A switch
    // committed while it was in flight would otherwise land a spelling verified on one generation
    // in the other's settings, and clear that other generation's own refusal with it.
    [Fact]
    public async Task ASaveWhoseGenerationChangedUnderTheProbeStoresNothingAndSaysWhy()
    {
        await using var host = await MonitorHost.CreateAsync(
            folderAddressing: options => new SwitchingFolderAddresses(options),
            libraryConfig: new CoveConfiguration { CovePaths = [new CovePath { Path = CoveRoot }] });

        var saved = await host.SaveFolderMappingAsync(CoveRoot, Mapping);

        Assert.Equal(FolderMappingSaveOutcome.Refused, saved.Outcome);
        Assert.Equal(FolderAgreementRefusal.ConnectionChangedUnderTheProbe, saved.Refusal);
        var stored = await host.Options.LoadAsync(TestCt);
        Assert.Empty(stored.InstanceSettingsOrEmptyFor(WhisparrGeneration.V2).OutboundMappings);
        Assert.Empty(stored.InstanceSettingsOrEmptyFor(WhisparrGeneration.V3).OutboundMappings);
    }

    // The control for the case above: the same route over the same stub, with nothing switching.
    [Fact]
    public async Task ASaveWhoseGenerationDidNotChangeStoresTheProbedSpelling()
    {
        await using var host = await MonitorHost.CreateAsync(
            folderAddressing: _ => new SwitchingFolderAddresses(null),
            libraryConfig: new CoveConfiguration { CovePaths = [new CovePath { Path = CoveRoot }] });

        var saved = await host.SaveFolderMappingAsync(CoveRoot, Mapping);

        Assert.Equal(FolderMappingSaveOutcome.Stored, saved.Outcome);
        Assert.Equal(
            Mapping,
            Assert.Single((await host.Options.LoadAsync(TestCt)).Instance().OutboundMappings)
                .InstanceRoot);
    }

    // Answers the supplied path as agreed, and where it holds an options store, selects the other
    // generation first, as a settings save committed during the probe does.
    private sealed class SwitchingFolderAddresses(OptionsStore? switching) : IFolderAddressPort
    {
        public Task<AddressedFolder> AddressAsync(
            FolderAddressTarget target, string folder, CancellationToken ct)
            => throw new NotSupportedException();

        public async Task<AddressedFolder> AddressAsync(
            FolderAddressTarget target, string coveRoot, string supplied, CancellationToken ct)
        {
            if (switching is { } options)
            {
                var stored = await options.LoadAsync(ct);
                await options.SaveAsync(
                    stored with { SelectedGeneration = WhisparrGeneration.V2 }, ct);
            }

            return new AddressedFolder(supplied, null, coveRoot, [supplied + "/scene.mp4"]);
        }

        public Task<AddressedFolder> AgreedRootAsync(
            FolderAddressTarget target, string coveRoot, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private enum Holding
    {
        TheSample,
        Nothing,
        AnotherLength,
    }

    // Loaded and saved rather than written fresh, so the connection the host configured survives
    // and the save route still resolves an instance.
    private static async Task StoreAsync(
        MonitorHost host, Func<WhisparrSyncOptions, WhisparrSyncOptions> fold)
        => await host.Options.SaveAsync(
            fold(await host.Options.LoadAsync(TestCt)), TestCt);

    private static async Task<(MonitorHost Host, string Candidate)> ProbingHostAsync(
        Holding holding, FakePrincipalAccessor? principal = null)
    {
        // The listing is composed from the file the library seeded, so the probe is answered about
        // the path the product asked about rather than one this case guessed.
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
