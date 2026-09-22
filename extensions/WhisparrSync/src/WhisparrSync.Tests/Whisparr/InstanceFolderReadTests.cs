using System.Net;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// Asserted over the captured request rather than over a constant. The generated client composes
// the route and escapes the query, so the only source for what leaves is a request it made.
public sealed class InstanceFolderReadTests
{
    // The shape both generations answer a directory listing with.
    private const string Listing = """
        {"parent":"/data/","directories":[],"files":[{"path":"/data/a.mp4","size":41}]}
        """;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task AskingForADirectoryCarriesItAndAsksForFiles(WhisparrGeneration generation)
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Listing);
        var client = TestWhisparrClient.Over(handler, generation: generation);

        await Role(client).ReadInstanceFolderAsync("/data/Blue Harbor/", TestCt);

        Assert.Equal("/api/v3/filesystem", Assert.Single(handler.Requests).Path);
        var target = Assert.Single(handler.Targets);
        Assert.Contains("path=%2fdata%2fBlue+Harbor%2f", target, StringComparison.Ordinal);
        Assert.Contains("includeFiles=true", target, StringComparison.Ordinal);
    }

    // Without the trailing separator the instance treats the spelling as a partial name and
    // describes the parent instead.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task ADirectoryWithNoTrailingSeparatorIsStillSentAsThatDirectory(
        WhisparrGeneration generation)
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Listing);
        var client = TestWhisparrClient.Over(handler, generation: generation);

        await Role(client).ReadInstanceFolderAsync("/data/Blue Harbor", TestCt);

        Assert.Contains(
            "path=%2fdata%2fBlue+Harbor%2f", Assert.Single(handler.Targets), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlankDirectoryIsRefusedWithoutAsking()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Listing);
        var client = TestWhisparrClient.Over(handler);

        await Assert.ThrowsAsync<ArgumentException>(
            () => Role(client).ReadInstanceFolderAsync("  ", TestCt));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public void BothGenerationsHoldTheFilesystemRole(WhisparrGeneration generation)
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Listing);
        var client = TestWhisparrClient.Over(handler, generation: generation);

        var role = GenerationCapabilities
            .For(generation, client)
            .Obtain<IWhisparrInstanceFilesystemReading>()
            .Match<IWhisparrInstanceFilesystemReading?>(filesystem => filesystem, _ => null);

        Assert.NotNull(role);
        Assert.Contains(WhisparrCapability.ReadInstanceFilesystem, GenerationCapabilities.For(generation).Held);
    }

    private static IWhisparrInstanceFilesystemReading Role(IWhisparrClient client)
        => (IWhisparrInstanceFilesystemReading)client;
}
