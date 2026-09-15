using System.Net;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// What the instance is asked when this product wants to know what is at a path on its own
/// filesystem.
/// </summary>
/// <remarks>
/// Asserted over the captured request rather than over a constant. The generated client composes the
/// route and escapes the query, so the only honest source for what leaves is a request it made.
/// </remarks>
public sealed class InstanceFolderReadTests
{
    private static readonly Uri Address = new("http://whisparr:6969");

    private const string Key = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    /// <summary>One directory listing, in the shape both generations answer with.</summary>
    private const string Listing = """
        {"parent":"/data/","directories":[],"files":[{"path":"/data/a.mp4","size":41}]}
        """;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// Each generation is asked for the directory itself, with files included.
    /// </summary>
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task AskingForADirectoryCarriesItAndAsksForFiles(WhisparrGeneration generation)
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Listing);
        var client = TestWhisparrClient.Over(handler);

        await Role(client).ReadInstanceFolderAsync(Address, Key, generation, "/data/Blue Harbor/", TestCt);

        Assert.Equal("/api/v3/filesystem", Assert.Single(handler.Requests).Path);
        var target = Assert.Single(handler.Targets);
        Assert.Contains("path=%2fdata%2fBlue+Harbor%2f", target, StringComparison.Ordinal);
        Assert.Contains("includeFiles=true", target, StringComparison.Ordinal);
    }

    /// <summary>
    /// A directory named without a trailing separator still reaches the instance as that directory.
    /// </summary>
    /// <remarks>
    /// Without the separator the instance treats the spelling as a partial name and describes the
    /// parent, so the two spellings would answer different things.
    /// </remarks>
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task ADirectoryWithNoTrailingSeparatorIsStillSentAsThatDirectory(
        WhisparrGeneration generation)
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Listing);
        var client = TestWhisparrClient.Over(handler);

        await Role(client).ReadInstanceFolderAsync(Address, Key, generation, "/data/Blue Harbor", TestCt);

        Assert.Contains(
            "path=%2fdata%2fBlue+Harbor%2f", Assert.Single(handler.Targets), StringComparison.Ordinal);
    }

    /// <summary>A blank directory is refused before anything leaves.</summary>
    [Fact]
    public async Task ABlankDirectoryIsRefusedWithoutAsking()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Listing);
        var client = TestWhisparrClient.Over(handler);

        await Assert.ThrowsAsync<ArgumentException>(
            () => Role(client).ReadInstanceFolderAsync(
                Address, Key, WhisparrGeneration.V3, "  ", TestCt));

        Assert.Empty(handler.Requests);
    }

    /// <summary>Both generations hold the role, so neither answers a refusal in its place.</summary>
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public void BothGenerationsHoldTheFilesystemRole(WhisparrGeneration generation)
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, Listing);
        var client = TestWhisparrClient.Over(handler);

        var role = GenerationCapabilities
            .For(generation, WhisparrRoleSet.From(client))
            .Obtain<IWhisparrInstanceFilesystemReading>()
            .Match<IWhisparrInstanceFilesystemReading?>(filesystem => filesystem, _ => null);

        Assert.NotNull(role);
        Assert.Contains(WhisparrCapability.ReadInstanceFilesystem, GenerationCapabilities.For(generation).Held);
    }

    private static IWhisparrInstanceFilesystemReading Role(IWhisparrClient client)
        => (IWhisparrInstanceFilesystemReading)client;
}
