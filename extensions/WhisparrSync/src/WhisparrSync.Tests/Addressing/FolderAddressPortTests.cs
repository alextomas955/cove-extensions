using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Addressing;

// Driven over doubles for the library side and a recording transport for the instance side, so
// the probe count and the paths asked about are read off the requests that left rather than off a
// call log a double kept.
public sealed class FolderAddressPortTests
{
    private const string CoveRoot = "G:/Downloads/P";

    private const string Sample = "G:/Downloads/P/Blue Harbor/scene.mp4";

    private const long SampleSize = 41;

    private static readonly Uri Address = new("http://whisparr:6969");

    private const string Key = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    // The size matches SampleSize. A different one stops the candidate resolving.
    private const string HoldingTheSample = """
        {"parent":"/data/","directories":[],
         "files":[{"path":"/data/Blue Harbor/scene.mp4","name":"scene.mp4","size":41,"type":"file"}]}
        """;

    private const string HoldingAnotherLength = """
        {"parent":"/data/","directories":[],
         "files":[{"path":"/data/Blue Harbor/scene.mp4","name":"scene.mp4","size":42,"type":"file"}]}
        """;

    private const string HoldingADirectory = """
        {"parent":"/data/","directories":[{"path":"/data/Blue Harbor/scene.mp4","name":"scene.mp4"}],
         "files":[]}
        """;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFolderUnderAnAgreedRootIsAddressedAsTheInstanceSpellsIt()
    {
        var (port, handler) = Over(HoldingTheSample, ["/data"]);

        var addressed = await port.AddressAsync(
            Target(handler), "G:\\Downloads\\P\\Blue Harbor", TestCt);

        Assert.Null(addressed.Refusal);
        Assert.Equal("/data/Blue Harbor", addressed.InstancePath);
        Assert.Equal(CoveRoot, addressed.CoveRoot);
    }

    [Fact]
    public async Task ACandidateTheInstanceReportsADirectoryAtDoesNotResolve()
    {
        var (port, handler) = Over(HoldingADirectory, ["/data"]);

        var addressed = await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Null(addressed.InstancePath);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, addressed.Refusal);
        Assert.Equal(["/data/Blue Harbor/scene.mp4", Sample], addressed.Tried);
    }

    [Fact]
    public async Task ACandidateOfAnotherLengthDoesNotResolve()
    {
        var (port, handler) = Over(HoldingAnotherLength, ["/data"]);

        var addressed = await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal(FolderAgreementRefusal.NothingResolved, addressed.Refusal);
    }

    // Two probes, one per candidate: the rebuild under the instance's declared root, and the
    // library's own spelling. What a run costs grows with the roots an operator configured, never
    // with the folders under them.
    [Fact]
    public async Task ManyFoldersUnderOneRootCostOneSampleFileAndOneRoundOfProbes()
    {
        var samples = new CountingSampleFiles(new SampleFile(Sample, SampleSize));
        var declared = new StubInstanceRoots(["/data"]);
        var (port, handler) = Over(HoldingTheSample, declared, samples);
        var target = Target(handler);

        foreach (var folder in new[] { "Blue Harbor", "Tushy", "Vixen", "Blacked" })
        {
            var addressed = await port.AddressAsync(target, CoveRoot + "/" + folder, TestCt);
            Assert.Equal("/data/" + folder, addressed.InstancePath);
        }

        Assert.Equal(1, samples.Reads);
        Assert.Equal(1, declared.Reads);
        Assert.Equal(
            2, handler.Targets.Count(sent => sent.Contains("filesystem", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ARootHoldingNoFileAsksTheInstanceNothing()
    {
        var samples = new CountingSampleFiles(null);
        var (port, handler) = Over(HoldingTheSample, ["/data"], samples);

        var addressed = await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal(FolderAgreementRefusal.NoFileToProbeWith, addressed.Refusal);
        Assert.DoesNotContain(
            handler.Targets, sent => sent.Contains("filesystem", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFolderUnderNoLibraryRootIsAddressedByNothing()
    {
        var (port, handler) = Over(HoldingTheSample, ["/data"]);

        var addressed = await port.AddressAsync(Target(handler), "H:/Elsewhere/Blue Harbor", TestCt);

        Assert.Equal(FolderAgreementRefusal.FolderUnderNoLibraryRoot, addressed.Refusal);
        Assert.Empty(handler.Requests);
    }

    // A probe that could not be read is not the same as nothing having resolved.
    [Fact]
    public async Task AnAnswerOfAnotherShapeIsAProbeThatCouldNotBeRead()
    {
        var (port, handler) = Over("[]", ["/data"]);

        var addressed = await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal(FolderAgreementRefusal.ProbeCouldNotBeRead, addressed.Refusal);
    }

    [Fact]
    public async Task AnInstanceDeclaringNoRootAnswersItsOwnReason()
    {
        var (port, handler) = Over(HoldingTheSample, declaredRoots: []);

        var addressed = await port.AddressAsync(Target(handler), CoveRoot + "/Blue Harbor", TestCt);

        Assert.Equal(FolderAgreementRefusal.InstanceDeclaresNoRoot, addressed.Refusal);
    }

    // The tail is taken below the root, so the shallower root produces a tail carrying the segment
    // the instance's own root already holds, and the rebuilt candidate then names a path neither
    // system has.
    [Fact]
    public async Task AFolderUnderTwoNestedLibraryRootsIsAddressedUnderTheMoreSpecificOne()
    {
        const string nestedSample = "/shared/media/Blue Harbor/scene.mp4";
        var listing = """
            {"parent":"/data/media/Blue Harbor/","directories":[],
             "files":[{"path":"/data/media/Blue Harbor/scene.mp4","size":41,"type":"file"}]}
            """;
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, listing);
        var port = new FolderAddressPort(
            new CountingSampleFiles(new SampleFile(nestedSample, SampleSize)),
            new StubLibraryRoots(["/shared", "/shared/media"]),
            new StubInstanceRoots(["/data/media"]),
            new OptionsStore(new FakeStore()),
            new FolderAgreementCache(TimeProvider.System),
            NullLogger.Instance);

        var addressed = await port.AddressAsync(
            Target(handler), "/shared/media/Blue Harbor", TestCt);

        Assert.Null(addressed.Refusal);
        Assert.Equal("/data/media/Blue Harbor", addressed.InstancePath);
        Assert.Equal("/shared/media", addressed.CoveRoot);
    }

    private static FolderAddressTarget Target(BodyRecordingHandler handler)
        => new(
            new WhisparrBinding(WhisparrGeneration.V3, Address, Key),
            (IWhisparrInstanceFilesystemReading)TestWhisparrClient.Over(handler));

    private static (IFolderAddressPort Port, BodyRecordingHandler Handler) Over(
        string listing,
        string[] declaredRoots,
        ISampleFilePort? samples = null)
        => Over(listing, new StubInstanceRoots(declaredRoots), samples);

    private static (IFolderAddressPort Port, BodyRecordingHandler Handler) Over(
        string listing,
        IReportedRootPort declared,
        ISampleFilePort? samples = null)
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, listing);

        return (
            new FolderAddressPort(
                samples ?? new CountingSampleFiles(new SampleFile(Sample, SampleSize)),
                new StubLibraryRoots(),
                declared,
                new OptionsStore(new FakeStore()),
                new FolderAgreementCache(TimeProvider.System),
                NullLogger.Instance),
            handler);
    }

    private sealed class CountingSampleFiles(SampleFile? answer) : ISampleFilePort
    {
        public int Reads { get; private set; }

        public Task<SampleFile?> ReadSampleFileAsync(string coveRoot, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(answer);
        }
    }

    // Only the roots are supplied. Every other member raises, so a test reaching one fails rather
    // than reading a value nobody configured.
    private sealed class StubLibraryRoots(IReadOnlyList<string>? roots = null) : ICoveLibraryPort
    {
        public IReadOnlyList<string> LibraryRoots { get; } = roots ?? [CoveRoot];

        public IReadOnlyList<string> ConfiguredMetadataEndpoints
            => throw new NotSupportedException();

        public Task<LibraryImport> ImportVideoAsync(string path, int? videoId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<HeldFile?> HeldFileAtAsync(string path, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> DetachSupersededFilesAsync(int videoId, string keptPath, CancellationToken ct)
            => throw new NotSupportedException();

        public bool StartFollowUpScan(IReadOnlyList<string> paths) => throw new NotSupportedException();

        public Task<IdentityResolution> ResolveByRemoteIdAsync(
            string endpoint, string remoteId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> CarriesIdentityAsync(int videoId, string endpoint, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> StampIdentityAsync(
            int videoId, string endpoint, string remoteId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> EnrichAsync(
            int videoId, string endpoint, string remoteId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class StubInstanceRoots(IReadOnlyList<string> roots) : IReportedRootPort
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyList<string>?> ReadAsync(WhisparrGeneration generation, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<string>?>(roots);
        }
    }
}
