using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Addressing;

/// <summary>
/// What a stored mapping does to the candidates one library root is asked about, and what it does
/// not do: the instance's answer still decides.
/// </summary>
/// <remarks>
/// The instance's answers are supplied by a recording transport, so what the port asked about is read
/// off the requests that left rather than off a call log a double kept.
/// </remarks>
public sealed class FolderAgreementMappingTests
{
    private const string CoveRoot = "G:/Downloads/P";

    private const string Sample = "G:/Downloads/P/Blue Harbor/scene.mp4";

    private const long SampleSize = 41;

    private const string Key = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private static readonly Uri Address = new("http://whisparr:6969");

    /// <summary>The mapped directory, holding the sample file at the length the library holds.</summary>
    private const string HoldingTheSample = """
        {"parent":"/mnt/media/","directories":[],
         "files":[{"path":"/mnt/media/Blue Harbor/scene.mp4","name":"scene.mp4","size":41,"type":"file"}]}
        """;

    /// <summary>The same directory, holding a file of another length.</summary>
    private const string HoldingAnotherLength = """
        {"parent":"/mnt/media/","directories":[],
         "files":[{"path":"/mnt/media/Blue Harbor/scene.mp4","name":"scene.mp4","size":42,"type":"file"}]}
        """;

    /// <summary>A second mapped directory, holding the sample file at the library's length.</summary>
    private const string SecondMappingHoldingTheSample = """
        {"parent":"/srv/media/","directories":[],
         "files":[{"path":"/srv/media/Blue Harbor/scene.mp4","name":"scene.mp4","size":41,"type":"file"}]}
        """;

    /// <summary>A directory the instance lists nothing in.</summary>
    private const string HoldingNothing = """
        {"parent":"/mnt/media/","directories":[],"files":[]}
        """;

    /// <summary>The declared root's own directory, holding the sample file.</summary>
    private const string DeclaredRootHoldingTheSample = """
        {"parent":"/data/","directories":[],
         "files":[{"path":"/data/Blue Harbor/scene.mp4","name":"scene.mp4","size":41,"type":"file"}]}
        """;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// One candidate is built under a mapped root, and it is the mapping's own spelling.
    /// </summary>
    [Fact]
    public async Task AMappedRootIsAskedAboutOnePathUnderTheMapping()
    {
        var (port, handler, _) = await OverMappingAsync(HoldingTheSample, "/mnt/media");

        var addressed = await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Null(addressed.Refusal);
        Assert.Equal("/mnt/media/Blue Harbor", addressed.InstancePath);
        Assert.Equal(["/mnt/media/Blue Harbor/scene.mp4"], addressed.Tried);
    }

    /// <summary>
    /// The roots the instance declares are not read at all for a root that has a mapping.
    /// </summary>
    /// <remarks>
    /// An operator who states where a root is has settled it. Joining the two lists would reintroduce
    /// the ambiguity the mapping was supplied to remove, and would leave the operator's own answer
    /// competing with the guess it replaced.
    /// </remarks>
    [Fact]
    public async Task AMappedRootReadsTheRootsTheInstanceDeclaresNotAtAll()
    {
        var (port, handler, declared) = await OverMappingAsync(HoldingTheSample, "/mnt/media");

        await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal(0, declared.Reads);
    }

    /// <summary>
    /// A mapping the instance reports no file under resolves to nothing, exactly as an unmapped root
    /// would.
    /// </summary>
    [Fact]
    public async Task AMappingTheInstanceHoldsNothingUnderResolvesToNothing()
    {
        var (port, handler, _) = await OverMappingAsync(HoldingNothing, "/mnt/media");

        var addressed = await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Null(addressed.InstancePath);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, addressed.Refusal);
        Assert.Equal(["/mnt/media/Blue Harbor/scene.mp4"], addressed.Tried);
    }

    /// <summary>A mapping whose candidate carries a file of a different size resolves to nothing.</summary>
    /// <remarks>
    /// The size is what tells one filesystem from another holding a file of the same name, which is
    /// the deployment a mapping is most likely to be typed wrongly into.
    /// </remarks>
    [Fact]
    public async Task AMappingHoldingAFileOfAnotherLengthResolvesToNothing()
    {
        var (port, handler, _) = await OverMappingAsync(HoldingAnotherLength, "/mnt/media");

        var addressed = await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal(FolderAgreementRefusal.NothingResolved, addressed.Refusal);
    }

    /// <summary>Removing a mapping returns the root to the declared roots on the next reading.</summary>
    /// <remarks>
    /// Both readings share one cache, which is what the host registers. A reading established under
    /// the mapping must not answer the reading taken after it was removed.
    /// </remarks>
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

    /// <summary>Changing a mapping asks about the new path on the next reading.</summary>
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

    /// <summary>A reading established against one instance is not reused for another.</summary>
    /// <remarks>
    /// An operator who stores a different connection address is pointing this extension at a
    /// filesystem the previous instance's reading says nothing about.
    /// </remarks>
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

    /// <summary>A path a save resolved is in force on the next reading, unprobed.</summary>
    /// <remarks>
    /// The save stores the spelling the probe answered to, so the reading it held has to be stamped
    /// with that spelling rather than with the one that was typed.
    /// </remarks>
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

    /// <summary>
    /// Two folders under one root with a stored path in force establish that root once.
    /// </summary>
    /// <remarks>
    /// What a run costs grows with the roots an operator configured, never with the folders under
    /// them. Reading the store before the cache is asked must not put a probe on each folder.
    /// </remarks>
    [Fact]
    public async Task TwoFoldersUnderOneRootWithAStoredPathAreProbedForOnce()
    {
        var options = await StoringAsync("/mnt/media");
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
    }

    private static FolderAddressTarget Target(BodyRecordingHandler handler)
        => Target(handler, Address);

    private static FolderAddressTarget Target(BodyRecordingHandler handler, Uri address)
        => new(
            WhisparrGeneration.V3,
            address,
            Key,
            (IWhisparrInstanceFilesystemReading)TestWhisparrClient.Over(handler));

    /// <summary>
    /// The port over one store and one declared-root source, sharing <paramref name="cache"/> where
    /// a case takes two readings and needs the second to meet what the first held.
    /// </summary>
    private static FolderAddressPort Port(
        OptionsStore options, IReportedRootPort declared, FolderAgreementCache? cache = null)
        => new FolderAddressPort(
            new StubSampleFiles(new SampleFile(Sample, SampleSize)),
            new RecordingLibrary(reached: true, [CoveRoot]),
            declared,
            options,
            cache ?? new FolderAgreementCache(TimeProvider.System),
            NullLogger.Instance);

    /// <summary>A store holding <paramref name="mapping"/> for <see cref="CoveRoot"/>.</summary>
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

    /// <summary>How many filesystem reads left over <paramref name="handler"/>.</summary>
    private static int Probes(BodyRecordingHandler handler)
        => handler.Targets.Count(sent => sent.Contains("filesystem", StringComparison.Ordinal));

    /// <summary>
    /// The port over a store mapping <see cref="CoveRoot"/> to <paramref name="mapping"/>, with the
    /// instance answering <paramref name="listing"/> to every filesystem read.
    /// </summary>
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

    /// <summary>The one file a root establishes its agreement from, with no database behind it.</summary>
    private sealed class StubSampleFiles(SampleFile? answer) : ISampleFilePort
    {
        public Task<SampleFile?> ReadSampleFileAsync(string coveRoot, CancellationToken ct)
            => Task.FromResult(answer);
    }

    /// <summary>The roots the instance declares, counting how often they were asked for.</summary>
    private sealed class CountingInstanceRoots(IReadOnlyList<string> roots) : IReportedRootPort
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyList<string>> ReadAsync(
            WhisparrGeneration generation, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(roots);
        }
    }
}
