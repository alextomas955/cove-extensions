using WhisparrSync.Scene;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Scene;

// Every body here is a raw answer. A builder composing the value under test would agree with
// itself whatever the projection did.
public sealed class CommandProjectorTests
{
    private const string JsonContentType = "application/json; charset=utf-8";

    // The answer Whisparr's command route gives to a post it accepted.
    private const string Posted =
        """
        {"id":8123,"name":"MoviesSearch","commandName":"Movies Search","status":"queued",
         "trigger":"manual","queued":"2026-09-08T20:14:11Z"}
        """;

    [Fact]
    public void TheAnswerToAPostNamesTheCommandTheInstanceTook()
        => Assert.Equal(8123, CommandProjector.IdIn(Json(Posted)));

    [Fact]
    public void AnUnreadableAnswerNamesNoCommand()
        => Assert.Null(CommandProjector.IdIn(Json("{\"id\":")));

    [Fact]
    public void AnEmptyAnswerNamesNoCommand()
        => Assert.Null(CommandProjector.IdIn(Json(string.Empty)));

    [Fact]
    public void AnAnswerWhoseIdentifierIsNotANumberNamesNoCommand()
        => Assert.Null(CommandProjector.IdIn(Json("""{"id":"8123","name":"MoviesSearch"}""")));

    // A just-posted command's status is unmeasured, so confirmation is identifier equality alone.
    [Fact]
    public void AReadBackNamingThePostedCommandConfirmsItWhateverProgressItReports()
    {
        Assert.True(CommandProjector.Confirmed(
            Json("""{"id":8123,"name":"MoviesSearch","status":"queued"}"""), 8123));
        Assert.True(CommandProjector.Confirmed(
            Json("""{"id":8123,"name":"MoviesSearch","status":"completed"}"""), 8123));
        Assert.True(CommandProjector.Confirmed(
            Json("""{"id":8123,"name":"MoviesSearch","status":"failed"}"""), 8123));
    }

    [Fact]
    public void AReadBackNamingAnotherCommandConfirmsNothing()
        => Assert.False(CommandProjector.Confirmed(
            Json("""{"id":8124,"name":"MoviesSearch","status":"queued"}"""), 8123));

    [Fact]
    public void AnAnswerNamingNoCommandConfirmsNothing()
    {
        Assert.False(CommandProjector.Confirmed(Json("""{"name":"MoviesSearch"}"""), 8123));
        Assert.False(CommandProjector.Confirmed(Json(string.Empty), 8123));
        Assert.False(CommandProjector.Confirmed(null, 8123));
        Assert.False(CommandProjector.Confirmed(
            new WhisparrResponse(500, JsonContentType, """{"id":8123}"""), 8123));
    }

    private static WhisparrResponse Json(string body) => new(200, JsonContentType, body);
}
