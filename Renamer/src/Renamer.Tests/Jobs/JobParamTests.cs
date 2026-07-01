using Renamer.Jobs;
using Renamer.Planner;

namespace Renamer.Tests.Jobs;

/// <summary>
/// Param decode: the job-parameter (de)serialization helpers round-trip a typed id list through the
/// host's string-only map and tolerate bad/empty input with a clean empty array (never throwing),
/// so a malformed batch is a no-op.
/// </summary>
public sealed class JobParamTests
{
    [Fact]
    public void Encode_Then_Decode_RoundTrips()
    {
        var encoded = RenamerJob.Encode("video", [1, 2, 3]);

        var (entityType, ids) = RenamerJob.Decode(encoded);

        Assert.Equal("video", entityType);
        Assert.Equal([1, 2, 3], ids);
    }

    [Fact]
    public void Decode_Null_Parameters_Yields_Empty()
    {
        var (entityType, ids) = RenamerJob.Decode(null);

        Assert.Equal(string.Empty, entityType);
        Assert.Empty(ids);
    }

    [Fact]
    public void Decode_Missing_EntityIds_Yields_Empty_NoThrow()
    {
        var parameters = new Dictionary<string, string> { ["entityType"] = "image" };

        var (entityType, ids) = RenamerJob.Decode(parameters);

        Assert.Equal("image", entityType);
        Assert.Empty(ids);
    }

    [Fact]
    public void Decode_Blank_Or_Garbage_EntityIds_Yields_Empty_NoThrow()
    {
        var blank = RenamerJob.Decode(new Dictionary<string, string>
        {
            ["entityType"] = "video",
            ["entityIds"] = "",
        });
        Assert.Empty(blank.ids);

        var garbage = RenamerJob.Decode(new Dictionary<string, string>
        {
            ["entityType"] = "video",
            ["entityIds"] = "not-json",
        });
        Assert.Empty(garbage.ids);
    }

    [Theory]
    [InlineData("video", RenamerFileKind.Video)]
    [InlineData("image", RenamerFileKind.Image)]
    [InlineData("audio", RenamerFileKind.Audio)]
    [InlineData("VIDEO", RenamerFileKind.Video)] // case-insensitive
    public void TryParseKind_Maps_Supported_Kinds(string entityType, RenamerFileKind expected)
    {
        Assert.True(global::Renamer.Renamer.TryParseKind(entityType, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("gallery")] // not a renamable kind
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseKind_Rejects_Unsupported_Kinds_NoThrow(string? entityType)
    {
        Assert.False(global::Renamer.Renamer.TryParseKind(entityType, out _));
    }
}
