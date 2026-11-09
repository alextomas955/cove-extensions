using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Cove.Extensions.Shared.Tests;

// What the three problem results put on the wire. The status, the media type and the two extension
// member names are the contract a generated client is built against, so each is asserted as the
// literal a caller reads rather than through the type that produced it.
public sealed class ProblemResultTests
{
    private static async Task<(int Status, string? ContentType, JsonElement Body)> WriteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Position = 0;
        using var document = JsonDocument.Parse(body);
        return (context.Response.StatusCode, context.Response.ContentType, document.RootElement.Clone());
    }

    [Fact]
    public async Task ABadRequest_WritesItsStatus_AsAProblemDocument_CarryingTheCodeAndTheBound()
    {
        var (status, contentType, body) = await WriteAsync(new BadRequestProblem(
            "TOO_MANY_IDS", "The request named more ids than one call may carry.", max: 1000));

        Assert.Equal(400, status);

        // Not plain application/json: the host's own middleware writes that, and the media type is what
        // tells a caller which of the two shapes it has in hand.
        Assert.StartsWith("application/problem+json", contentType, StringComparison.Ordinal);

        Assert.Equal("TOO_MANY_IDS", body.GetProperty("code").GetString());
        Assert.Equal(1000, body.GetProperty("max").GetInt32());
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("Bad Request", body.GetProperty("title").GetString());
        Assert.Equal(
            "The request named more ids than one call may carry.", body.GetProperty("detail").GetString());

        // Stable and non-dereferenceable: a caller may branch on it, and it names no page.
        Assert.Equal("urn:cove:extension:error:TOO_MANY_IDS", body.GetProperty("type").GetString());
    }

    // A refusal that carries no bound omits the member rather than writing a null, so a generated
    // client reading an optional number never meets one.
    [Fact]
    public async Task ARefusalWithNoBound_OmitsTheMaxMember()
    {
        var (_, _, body) = await WriteAsync(new BadRequestProblem("INVALID_BODY", "The body did not parse."));

        Assert.False(body.TryGetProperty("max", out _));
    }

    [Fact]
    public async Task AForbidden_WritesTheOneOccurrenceTheGateHas()
    {
        var (status, contentType, body) = await WriteAsync(new ForbiddenProblem());

        Assert.Equal(403, status);
        Assert.StartsWith("application/problem+json", contentType, StringComparison.Ordinal);
        Assert.Equal("FORBIDDEN", body.GetProperty("code").GetString());
        Assert.Equal("Forbidden", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task AConflict_WritesItsOwnStatusAndCode()
    {
        var (status, contentType, body) = await WriteAsync(
            new ConflictProblem("MIGRATION_PENDING", "A stored conversion has not run yet."));

        Assert.Equal(409, status);
        Assert.StartsWith("application/problem+json", contentType, StringComparison.Ordinal);
        Assert.Equal("MIGRATION_PENDING", body.GetProperty("code").GetString());
        Assert.Equal("Conflict", body.GetProperty("title").GetString());
    }

    // A handler reads the status off the result it is about to return, and a test reads the body off
    // it. Both go through the typed members rather than through the written bytes.
    [Fact]
    public void EachResult_ReportsItsOwnStatusAndBody_BeforeItIsWritten()
    {
        var conflict = new ConflictProblem("MIGRATION_PENDING", "A stored conversion has not run yet.");

        Assert.Equal(409, conflict.StatusCode);
        Assert.Same(conflict.Problem, conflict.Value);
        Assert.Equal("MIGRATION_PENDING", conflict.Problem.Code);
    }
}
