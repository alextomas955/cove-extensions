using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Renamer.Api;
using Renamer.Contracts;
using Renamer.Options;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

public sealed class PreviewSampleEndpointTests
{
    private static int StatusOf(IResult result) => Assert.IsType<IStatusCodeHttpResult>(Unwrap(result), exactMatch: false).StatusCode ?? 0;

    // The endpoint binds the raw request and parses the body itself, so tests hand it a real body
    // stream rather than a pre-bound typed record.
    private static HttpRequest RequestWithBody(string json)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        ctx.Request.ContentType = "application/json";
        return ctx.Request;
    }

    private static IReadOnlyList<PreviewSampleResult> Preview(RenamerOptions? options)
    {
        // Serialize the body exactly as the panel/host would, via the converter-aware options, so the
        // round-trip mirrors production (string enums, case-insensitive props).
        var json = JsonSerializer.Serialize(new PreviewSampleRequest(options), RenamerOptions.JsonOptions);
        return PreviewRaw(json);
    }

    private static IReadOnlyList<PreviewSampleResult> PreviewRaw(string json)
    {
        var ext = RenamerFixture.CreateWithStore();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
        var result = ext.PreviewSampleAsync(RequestWithBody(json), principal, default).GetAwaiter().GetResult();
        var ok = Assert.IsType<Ok<IReadOnlyList<PreviewSampleResult>>>(Unwrap(result));
        return ok.Value!;
    }

    private static PreviewSampleResult Sample(IEnumerable<PreviewSampleResult> all, string label)
        => all.Single(r => r.SampleLabel == label);

    // Both spellings of the one envelope the route declares with .Accepts<PreviewSampleRequest>. The
    // handler binds the raw request and parses it with RenamerOptions.JsonOptions, so the declared body
    // is never bound by the host - and the wire document describes the enum in the camelCase spelling
    // while the persisted blob uses the member name.
    private const string PascalCaseEnvelope = """{ "Options": { "FilenameTemplate": "$title", "Case": "Lower" } }""";
    private const string CamelCaseEnvelope = """{ "options": { "filenameTemplate": "$title", "case": "lower" } }""";

    [Theory]
    [InlineData(PascalCaseEnvelope)]
    [InlineData(CamelCaseEnvelope)]
    public void PreviewSample_AcceptsEitherCasingOfTheDeclaredBody(string body)
    {
        // The Video sample's title lower-cased. Transcribed rather than rendered: an expectation asked of
        // the engine would agree with it however far the posted `case` drifted from reaching it, and a
        // value that never arrived would come back "The Example.mp4".
        var video = Sample(PreviewRaw(body), "Video");

        Assert.Equal("the example.mp4", video.NewName);
    }

    [Fact]
    public void PreviewSample_OptionsWithANullListAndAZeroCap_RenderAsASavedLoadWouldRepairThem()
    {
        var repaired = PreviewRaw("""{ "Options": { "DropOrder": null, "FullPathMax": 0 } }""");
        var defaults = PreviewRaw("""{ "Options": {} }""");

        Assert.Equal(defaults.Select(r => r.NewName), repaired.Select(r => r.NewName));
    }

    [Fact]
    public void PreviewSample_DefaultOptions_ReturnsTheThreeServerSideSamplesOverTheirOriginalNames()
    {
        var all = Preview(new RenamerOptions());

        Assert.Equal(3, all.Count);
        Assert.Equal("the.example.2021.WEBRip.mp4", Sample(all, "Video").OldName);
    }

    [Theory]
    // The default template is "{$date - }$title{ [$resolution]}". The Video sample has date
    // 2021-03-14 and 3840 x 2160, which the engine buckets to $resolution "4K", so both groups
    // render.
    [InlineData("Video", "2021-03-14 - The Example [4K].mp4")]
    // The Audio sample has a date but no height (so no $resolution), so the "{ [$resolution]}"
    // group collapses entirely (leading space + brackets + token) while the "{$date - }" prefix
    // stays: no dangling brackets and no empty " []".
    [InlineData("Audio", "2020-01-09 - Track One.flac")]
    // The Image sample's 6000 x 4000 frame takes its label from the short edge, which outranks the
    // 6K its long edge buckets to. No other sample carries that branch through the endpoint.
    [InlineData("Image", "2022-07-01 - Sunset [7K].jpg")]
    public void PreviewSample_DefaultOptions_RendersEachSampleWithNoFlags(string label, string expectedName)
    {
        var sample = Sample(Preview(new RenamerOptions()), label);

        Assert.Equal(expectedName, sample.NewName);
        Assert.Empty(sample.Flags);
        Assert.Empty(sample.DroppedFields);
    }

    [Fact]
    public void PreviewSample_StudioTitleResolutionTemplate_RendersVideoName()
    {
        var all = Preview(new RenamerOptions { FilenameTemplate = "$studio - $title [$resolution]" });

        var video = Sample(all, "Video");
        // The engine buckets the Video sample's 3840 x 2160 frame to $resolution "4K".
        Assert.Equal("Acme Studios - The Example [4K].mp4", video.NewName);
        Assert.Empty(video.Flags);
    }

    [Fact]
    public void PreviewSample_TinyFilenameMax_FlagsLengthReduced_WithNamedDroppedFields()
    {
        // A template that uses early DropOrder fields + a tiny cap forces the reducer to drop them;
        // the flag is proven truthful by asserting the named dropped fields, not just the boolean.
        var all = Preview(new RenamerOptions
        {
            FilenameTemplate = "$title $videoCodec $audioCodec $resolution",
            FilenameMax = 14, // "The Example" (11) + ".mp4" (4) = 15 > 14 still needs more drops
        });

        var video = Sample(all, "Video");
        Assert.Contains("length-reduced", video.Flags);
        Assert.NotEmpty(video.DroppedFields);
        // videoCodec/audioCodec/resolution are early in the default DropOrder and present in the
        // template - they must be among the named dropped fields.
        Assert.Contains("videoCodec", video.DroppedFields);
        Assert.Contains("audioCodec", video.DroppedFields);
    }

    [Fact]
    public void PreviewSample_RequiredFieldMissing_FlagsGatingSkip()
    {
        // Only the Video sample carries a videoCodec, so only it survives the gate.
        var all = Preview(new RenamerOptions { RequiredFields = ["videoCodec"] });

        Assert.Contains("gating-skip", Sample(all, "Image").Flags);
        Assert.Contains("gating-skip", Sample(all, "Audio").Flags);
        Assert.DoesNotContain("gating-skip", Sample(all, "Video").Flags);
    }

    [Fact]
    public void PreviewSample_IllegalCharTemplate_FlagsSanitized()
    {
        // ':' is illegal, so the sanitizer strips it and the rendered name differs from the raw one.
        var all = Preview(new RenamerOptions { FilenameTemplate = "$title: x" });

        var video = Sample(all, "Video");
        Assert.Contains("sanitized", video.Flags);
        Assert.DoesNotContain(":", video.NewName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{ "Options": null }""")]
    [InlineData("{}")]
    public void PreviewSample_NoOptionsInTheBody_RendersTheDefaults(string body)
    {
        // The default template "{$date - }$title{ [$resolution]}" over the Video sample's date and its
        // 2160-high frame.
        Assert.Equal("2021-03-14 - The Example [4K].mp4", Sample(PreviewRaw(body), "Video").NewName);
    }

    [Fact]
    public async Task PreviewSample_WithNoReadPermission_Returns403_BeforeReadingBody()
    {
        var ext = RenamerFixture.CreateWithStore();

        // Hand a body stream that would throw if read, proving the 403 short-circuits before any
        // body read (permission is enforced before work - including deserialization).
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new ThrowingStream();
        ctx.Request.ContentType = "application/json";

        var result = await ext.PreviewSampleAsync(ctx.Request, FakePrincipalAccessor.None(), default);

        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task PreviewSample_AdmitsACallerWhoCanReadOnlyTexts()
    {
        var ext = RenamerFixture.CreateWithStore();
        var textsOnly = FakePrincipalAccessor.WithPermissions(Permissions.TextsRead);

        var result = await ext.PreviewSampleAsync(RequestWithBody(PascalCaseEnvelope), textsOnly, default);

        Assert.IsType<Ok<IReadOnlyList<PreviewSampleResult>>>(Unwrap(result));
    }

    [Fact]
    public async Task PreviewSample_MalformedJson_Returns400()
    {
        var ext = RenamerFixture.CreateWithStore();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

        var result = await ext.PreviewSampleAsync(RequestWithBody("{ not valid json "), principal, default);

        var bad = Assert.IsType<BadRequestProblem>(Unwrap(result));
        Assert.Equal("INVALID_BODY", bad.Problem.Extensions[Problems.CodeMember]);
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("body must not be read");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new IOException("body must not be read");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("body must not be read");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
