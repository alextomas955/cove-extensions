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
    [Fact]
    public async Task RemovingAMappingReturnsTheRootToTheDeclaredRootsOnTheNextReading()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        await options.SaveAsync(
            new WhisparrSyncOptions
            {
                OutboundMappings =
                    [new OutboundRootMapping { CoveRoot = CoveRoot, InstanceRoot = "/mnt/media" }],
            },
            TestCt);

        var declared = new CountingInstanceRoots(["/data"]);
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, HoldingNothing);
        var mapped = await Port(options, declared)
            .AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, mapped.Refusal);

        await options.SaveAsync(new WhisparrSyncOptions(), TestCt);
        var unmapped = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, DeclaredRootHoldingTheSample);

        var addressed = await Port(options, declared)
            .AddressAsync(Target(unmapped), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal("/data/Blue Harbor", addressed.InstancePath);
        Assert.Equal(1, declared.Reads);
    }

    private static FolderAddressTarget Target(BodyRecordingHandler handler)
        => new(
            WhisparrGeneration.V3,
            Address,
            Key,
            (IWhisparrInstanceFilesystemReading)TestWhisparrClient.Over(handler));

    /// <summary>The port over one store and one declared-root source.</summary>
    private static IFolderAddressPort Port(OptionsStore options, IReportedRootPort declared)
        => new FolderAddressPort(
            new StubSampleFiles(new SampleFile(Sample, SampleSize)),
            new RecordingLibrary(reached: true, [CoveRoot]),
            declared,
            options,
            new FolderAgreementCache(TimeProvider.System),
            NullLogger.Instance);

    /// <summary>
    /// The port over a store mapping <see cref="CoveRoot"/> to <paramref name="mapping"/>, with the
    /// instance answering <paramref name="listing"/> to every filesystem read.
    /// </summary>
    private static async Task<(IFolderAddressPort Port, BodyRecordingHandler Handler,
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
