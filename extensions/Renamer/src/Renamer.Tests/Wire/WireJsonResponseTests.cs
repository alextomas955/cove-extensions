using System.Text;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Renamer.Contracts;
using Renamer.Planner;

namespace Renamer.Tests.Wire;

/// <summary>
/// The bytes <see cref="WireJson{T}"/> actually writes, read back as raw text.
/// </summary>
/// <remarks>
/// The host never calls <c>ConfigureHttpJsonOptions</c>, so a response that fell back to the framework
/// default would serialize every enum as a NUMBER while still type-checking, still returning 200 and
/// still matching a schema — and the UI, which compares against the string, would read every item as
/// something else and quietly do nothing. Nothing else in the suite would notice, which is why these
/// assertions read the response text rather than a deserialized object: deserializing launders the very
/// casing under test.
/// </remarks>
[Trait("Tier", "L2")]
public sealed class WireJsonResponseTests
{
    // Transcribed by hand from the committed wire fixtures (Contracts/fixtures/preview-response.json),
    // never read from the converter or from an enum member at test time: an expectation computed from
    // the thing it checks agrees with it forever, including while both are wrong.
    private const string ConfirmLevelLightOnTheWire = "\"confirmLevel\":\"light\"";
    private const string StatusRenamerOnTheWire = "\"status\":\"renamer\"";

    private static readonly PreviewResponse Sample = new(
        [
            new PreviewItemView(
                FileId: 10,
                OldFullPath: "/lib/raw one.mkv",
                NewFullPath: "/lib/First Film.mkv",
                Status: RenamerStatus.Renamer,
                NewBasename: "First Film.mkv",
                TargetFolderPath: "/lib",
                Reason: null,
                Suffixed: false,
                Sanitized: true,
                ResolvedDestinationRoot: null,
                MatchedRule: "InPlace",
                TargetVolume: "/"),
        ],
        new PreviewSummary(
            TotalCount: 1,
            SameVolumeCount: 1,
            CrossVolumeCount: 0,
            CrossVolumeBytes: 0,
            VolumePairs: [],
            ConfirmLevel: ConfirmLevel.Light,
            Undoable: true));

    /// <summary>Executes a result against a real response body and returns what it wrote, as UTF-8 text.</summary>
    private static async Task<(int status, string body)> ExecuteAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;

        await result.ExecuteAsync(ctx);

        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task WireJson_WritesEnumsAsCamelCaseStrings_NotNumbers()
    {
        var (_, body) = await ExecuteAsync(new WireJson<PreviewResponse>(Sample));

        Assert.Contains(ConfirmLevelLightOnTheWire, body, StringComparison.Ordinal);
        Assert.Contains(StatusRenamerOnTheWire, body, StringComparison.Ordinal);

        // The failure this class exists to catch: the framework default writes Light as 0 and Renamer
        // as 1, which is still valid JSON against the same schema.
        Assert.DoesNotContain("\"confirmLevel\":0", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\":1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WireJson_WritesCamelCasePropertyNames()
    {
        var (_, body) = await ExecuteAsync(new WireJson<PreviewResponse>(Sample));

        Assert.Contains("\"crossVolumeBytes\":", body, StringComparison.Ordinal);
        Assert.Contains("\"targetFolderPath\":", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CrossVolumeBytes\":", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TargetFolderPath\":", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WireJson_Reports200_AndExposesTheInstanceItWasBuiltWith()
    {
        var result = new WireJson<PreviewResponse>(Sample);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Same(Sample, Assert.IsAssignableFrom<IValueHttpResult<PreviewResponse>>(result).Value);
        Assert.Same(Sample, Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

        var (status, _) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task ForbiddenCode_Reports403_AndWritesItsErrorCode()
    {
        var result = new ForbiddenCode();

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains("\"code\":\"FORBIDDEN\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadRequestCode_Reports400_AndWritesTheCodeItWasGiven()
    {
        var result = new BadRequestCode("INVALID_BODY");

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("\"code\":\"INVALID_BODY\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadRequestCode_WritesMaxOnlyForACapRejection()
    {
        // One status code admits one response schema, so the cap arm and the plain arm share ErrorCode
        // and the cap's bound rides an optional member. That member must stay INVISIBLE on every other
        // code: a "max":null appearing on each 403 would be a silent wire change that still type-checks,
        // still returns the right status and still validates against the same schema.
        var (_, plain) = await ExecuteAsync(new BadRequestCode("UNSUPPORTED_ENTITY_TYPE"));
        Assert.DoesNotContain("max", plain, StringComparison.Ordinal);

        var (_, forbidden) = await ExecuteAsync(new ForbiddenCode());
        Assert.DoesNotContain("max", forbidden, StringComparison.Ordinal);

        var (_, capped) = await ExecuteAsync(new BadRequestCode("TOO_MANY_IDS", 1000));
        Assert.Contains("\"code\":\"TOO_MANY_IDS\"", capped, StringComparison.Ordinal);
        Assert.Contains("\"max\":1000", capped, StringComparison.Ordinal);
    }
}
