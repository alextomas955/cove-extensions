using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Addressing;

// The instance's answers are supplied by a recording transport, so what the port asked about is
// read off the requests that left rather than off a call log a double kept.
public sealed class FolderAgreementMappingTests
{
    private const string CoveRoot = "G:/Downloads/P";

    private const string Sample = "G:/Downloads/P/Blue Harbor/scene.mp4";

    private const long SampleSize = 41;

    private const string Key = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private static readonly Uri Address = new("http://whisparr:6969");

    // The size matches SampleSize. A different one stops the candidate resolving.
    private const string HoldingTheSample = """
        {"parent":"/mnt/media/","directories":[],
         "files":[{"path":"/mnt/media/Blue Harbor/scene.mp4","name":"scene.mp4","size":41,"type":"file"}]}
        """;

    private const string HoldingAnotherLength = """
        {"parent":"/mnt/media/","directories":[],
         "files":[{"path":"/mnt/media/Blue Harbor/scene.mp4","name":"scene.mp4","size":42,"type":"file"}]}
        """;

    private const string SecondMappingHoldingTheSample = """
        {"parent":"/srv/media/","directories":[],
         "files":[{"path":"/srv/media/Blue Harbor/scene.mp4","name":"scene.mp4","size":41,"type":"file"}]}
        """;

    private const string HoldingNothing = """
        {"parent":"/mnt/media/","directories":[],"files":[]}
        """;

    private const string DeclaredRootHoldingTheSample = """
        {"parent":"/data/","directories":[],
         "files":[{"path":"/data/Blue Harbor/scene.mp4","name":"scene.mp4","size":41,"type":"file"}]}
        """;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMappedRootIsAskedAboutOnePathUnderTheMapping()
    {
        var (port, handler, _) = await OverMappingAsync(HoldingTheSample, "/mnt/media");

        var addressed = await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Null(addressed.Refusal);
        Assert.Equal("/mnt/media/Blue Harbor", addressed.InstancePath);
        Assert.Equal(["/mnt/media/Blue Harbor/scene.mp4"], addressed.Tried);
    }

    // An operator who states where a root is has settled it. Joining the two lists would
    // reintroduce the ambiguity the mapping was supplied to remove.
    [Fact]
    public async Task AMappedRootReadsTheRootsTheInstanceDeclaresNotAtAll()
    {
        var (port, handler, declared) = await OverMappingAsync(HoldingTheSample, "/mnt/media");

        await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal(0, declared.Reads);
    }

    [Fact]
    public async Task AMappingTheInstanceHoldsNothingUnderResolvesToNothing()
    {
        var (port, handler, _) = await OverMappingAsync(HoldingNothing, "/mnt/media");

        var addressed = await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Null(addressed.InstancePath);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, addressed.Refusal);
        Assert.Equal(["/mnt/media/Blue Harbor/scene.mp4"], addressed.Tried);
    }

    // The size is what tells one filesystem from another holding a file of the same name, which is
    // the deployment a mapping is most likely to be typed wrongly into.
    [Fact]
    public async Task AMappingHoldingAFileOfAnotherLengthResolvesToNothing()
    {
        var (port, handler, _) = await OverMappingAsync(HoldingAnotherLength, "/mnt/media");

        var addressed = await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal(FolderAgreementRefusal.NothingResolved, addressed.Refusal);
    }

    // Both readings share one cache, which is what the host registers. Without the shared cache
    // the test would pass whether or not the removal invalidated the earlier reading.
    [Fact]
    public async Task RemovingAMappingReturnsTheRootToTheDeclaredRootsOnTheNextReading()
    {
        var options = await StoringAsync("/mnt/media");
        var cache = new FolderAgreementCache(TimeProvider.System);
        var declared = new CountingInstanceRoots(["/data"]);
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, HoldingNothing);
        var mapped = await Port(options, declared, cache)
            .AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, mapped.Refusal);

        await options.SaveAsync(Mapping(null), TestCt);
        var unmapped = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, DeclaredRootHoldingTheSample);

        var addressed = await Port(options, declared, cache)
            .AddressAsync(Target(unmapped), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal("/data/Blue Harbor", addressed.InstancePath);
        Assert.Equal(1, declared.Reads);
    }

    [Fact]
    public async Task ChangingAMappingAsksAboutTheNewPathOnTheNextReading()
    {
        var options = await StoringAsync("/mnt/media");
        var cache = new FolderAgreementCache(TimeProvider.System);
        var declared = new CountingInstanceRoots(["/data"]);
        var first = BodyRecordingHandler.Answering(HttpStatusCode.OK, HoldingTheSample);
        var mapped = await Port(options, declared, cache)
            .AddressAsync(Target(first), CoveRoot + "/Blue Harbor", TestCt);
        Assert.Equal("/mnt/media/Blue Harbor", mapped.InstancePath);

        await options.SaveAsync(Mapping("/srv/media"), TestCt);
        var second = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, SecondMappingHoldingTheSample);

        var addressed = await Port(options, declared, cache)
            .AddressAsync(Target(second), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal("/srv/media/Blue Harbor", addressed.InstancePath);
        Assert.Equal(["/srv/media/Blue Harbor/scene.mp4"], addressed.Tried);
    }

    // A different connection address points this extension at a filesystem the previous instance's
    // reading says nothing about.
    [Fact]
    public async Task AReadingIsNotReusedOnceTheStoredInstanceAddressIsAnotherOne()
    {
        var options = await StoringAsync("/mnt/media");
        var cache = new FolderAgreementCache(TimeProvider.System);
        var declared = new CountingInstanceRoots(["/data"]);
        var first = BodyRecordingHandler.Answering(HttpStatusCode.OK, HoldingTheSample);
        var mapped = await Port(options, declared, cache)
            .AddressAsync(Target(first), CoveRoot + "/Blue Harbor", TestCt);
        Assert.Equal("/mnt/media/Blue Harbor", mapped.InstancePath);

        var second = BodyRecordingHandler.Answering(HttpStatusCode.OK, HoldingNothing);

        var addressed = await Port(options, declared, cache).AddressAsync(
            Target(second, new Uri("http://other-whisparr:6969")),
            CoveRoot + "/Blue Harbor",
            TestCt);

        Assert.Null(addressed.InstancePath);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, addressed.Refusal);
        Assert.Equal(1, Probes(second));
    }

    // Two Whisparrs behind one reverse proxy differ by their base path alone. A reading kept for
    // the authority would answer the second instance with the first one's filesystem spelling.
    [Fact]
    public async Task AReadingIsNotReusedOnceTheStoredInstanceAddressIsAnotherUrlBase()
    {
        var options = await StoringAsync("/mnt/media");
        var cache = new FolderAgreementCache(TimeProvider.System);
        var declared = new CountingInstanceRoots(["/data"]);
        var first = BodyRecordingHandler.Answering(HttpStatusCode.OK, HoldingTheSample);
        var mapped = await Port(options, declared, cache).AddressAsync(
            Target(first, new Uri("http://proxy:443/whisparr-a")),
            CoveRoot + "/Blue Harbor",
            TestCt);
        Assert.Equal("/mnt/media/Blue Harbor", mapped.InstancePath);

        var second = BodyRecordingHandler.Answering(HttpStatusCode.OK, HoldingNothing);

        var addressed = await Port(options, declared, cache).AddressAsync(
            Target(second, new Uri("http://proxy:443/whisparr-b")),
            CoveRoot + "/Blue Harbor",
            TestCt);

        Assert.Null(addressed.InstancePath);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, addressed.Refusal);
        Assert.Equal(1, Probes(second));
    }

    [Fact]
    public async Task AnAddressStoredWithATrailingSeparatorMeetsTheEntryEstablishedWithoutOne()
    {
        var options = await StoringAsync("/mnt/media");
        var cache = new FolderAgreementCache(TimeProvider.System);
        var declared = new CountingInstanceRoots(["/data"]);
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, HoldingTheSample);
        await Port(options, declared, cache).AddressAsync(
            Target(handler, new Uri("http://proxy:443/whisparr-a")),
            CoveRoot + "/Blue Harbor",
            TestCt);

        var addressed = await Port(options, declared, cache).AddressAsync(
            Target(handler, new Uri("http://proxy:443/whisparr-a/")),
            CoveRoot + "/Tushy",
            TestCt);

        Assert.Equal("/mnt/media/Tushy", addressed.InstancePath);
        Assert.Equal(1, Probes(handler));
    }

    // The save stores the spelling the probe answered to, not the one that was typed, so the
    // reading it held is stamped with the resolved spelling.
    [Fact]
    public async Task APathASaveResolvedAnswersTheNextReadingWithNoSecondProbe()
    {
        var options = await StoringAsync(null);
        var cache = new FolderAgreementCache(TimeProvider.System);
        var declared = new CountingInstanceRoots(["/data"]);
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, HoldingTheSample);
        var saved = await Port(options, declared, cache)
            .AddressAsync(Target(handler), CoveRoot, "/mnt/media/", TestCt);
        Assert.Equal("/mnt/media", saved.InstancePath);
        await options.SaveAsync(Mapping(saved.InstancePath), TestCt);

        var addressed = await Port(options, declared, cache)
            .AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal("/mnt/media/Blue Harbor", addressed.InstancePath);
        Assert.Equal(1, Probes(handler));
    }

    // What a run costs grows with the roots an operator configured, never with the folders under
    // them. A load deserialises the whole blob every time, so the store read is counted as well as
    // the probe.
    [Fact]
    public async Task TwoFoldersUnderOneRootWithAStoredPathAreProbedForOnce()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        await options.SaveAsync(Mapping("/mnt/media"), TestCt);
        var declared = new CountingInstanceRoots(["/data"]);
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, HoldingTheSample);
        var port = Port(options, declared);
        var target = Target(handler);

        foreach (var folder in new[] { "Blue Harbor", "Tushy" })
        {
            var addressed = await port.AddressAsync(target, CoveRoot + "/" + folder, TestCt);
            Assert.Equal("/mnt/media/" + folder, addressed.InstancePath);
        }

        Assert.Equal(1, Probes(handler));
        Assert.Equal(0, declared.Reads);
        Assert.Equal(1, store.GetKeys.Count(key => key == OptionsStore.Key));
    }

    private static FolderAddressTarget Target(BodyRecordingHandler handler)
        => Target(handler, Address);

    private static FolderAddressTarget Target(BodyRecordingHandler handler, Uri address)
        => new(
            new WhisparrBinding(WhisparrGeneration.V3, address, Key),
            (IWhisparrInstanceFilesystemReading)TestWhisparrClient.Over(handler));

    private static FolderAddressPort Port(
        OptionsStore options, IReportedRootPort declared, FolderAgreementCache? cache = null)
        => new FolderAddressPort(
            new StubSampleFiles(new SampleFile(Sample, SampleSize)),
            new RecordingLibrary(reached: true, [CoveRoot]),
            declared,
            options,
            cache ?? new FolderAgreementCache(TimeProvider.System),
            NullLogger.Instance);

    private static async Task<OptionsStore> StoringAsync(string? mapping)
    {
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(Mapping(mapping), TestCt);
        return options;
    }

    private static WhisparrSyncOptions Mapping(string? mapping)
        => new()
        {
            OutboundMappings = mapping is null
                ? []
                : [new OutboundRootMapping { CoveRoot = CoveRoot, InstanceRoot = mapping }],
        };

    private static int Probes(BodyRecordingHandler handler)
        => handler.Targets.Count(sent => sent.Contains("filesystem", StringComparison.Ordinal));

    private static async Task<(FolderAddressPort Port, BodyRecordingHandler Handler,
        CountingInstanceRoots Declared)> OverMappingAsync(string listing, string mapping)
    {
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions
            {
                OutboundMappings =
                    [new OutboundRootMapping { CoveRoot = CoveRoot, InstanceRoot = mapping }],
            },
            TestContext.Current.CancellationToken);

        var declared = new CountingInstanceRoots(["/data"]);
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, listing);
        return (Port(options, declared), handler, declared);
    }

    private sealed class StubSampleFiles(SampleFile? answer) : ISampleFilePort
    {
        public Task<SampleFile?> ReadSampleFileAsync(string coveRoot, CancellationToken ct)
            => Task.FromResult(answer);
    }

    private sealed class CountingInstanceRoots(IReadOnlyList<string> roots) : IReportedRootPort
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyList<string>?> ReadAsync(
            WhisparrGeneration generation, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<string>?>(roots);
        }
    }
}
