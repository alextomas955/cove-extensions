using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Renamer.Api;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// UI-02 backend core: <c>PreviewSampleAsync</c> runs the real <c>TemplateEngine</c> over the fixed
/// <see cref="SampleTokenSets"/> + the posted (unsaved) options and returns per-sample old→new + folder
/// + advisory flags — single-sourcing the naming logic so the React panel never re-implements it. The
/// length-reduced flag is asserted by its NAMED dropped fields (truthful, not a generic boolean),
/// and the videos.read deny path returns 403 with no engine work. Exercised as a plain method
/// (no HTTP host, no DbContext).
/// </summary>
[Trait("Tier", "Integration")]
public sealed class PreviewSampleEndpointTests
{
    private static global::Renamer.Renamer NewExtension()
    {
        var ext = new global::Renamer.Renamer();
        ((Cove.Plugins.IStatefulExtension)ext).SetStore(new FakeStore());
        return ext;
    }

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    /// <summary>
    /// Builds an <see cref="HttpRequest"/> whose body is the given raw JSON — the endpoint now binds the
    /// raw request and parses the body itself (with <see cref="RenamerOptions.JsonOptions"/>), so tests
    /// drive it through a real body stream rather than a pre-bound typed record.
    /// </summary>
    private static HttpRequest RequestWithBody(string json)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        ctx.Request.ContentType = "application/json";
        return ctx.Request;
    }

    /// <summary>Runs the endpoint with a videos.read principal and a serialized {Options:...} body.</summary>
    private static List<PreviewSampleResult> Preview(RenamerOptions? options)
    {
        // Serialize the body exactly as the panel/host would, via the converter-aware options, so the
        // round-trip mirrors production (string enums, case-insensitive props).
        var json = JsonSerializer.Serialize(new PreviewSampleRequest(options), RenamerOptions.JsonOptions);
        return PreviewRaw(json);
    }

    /// <summary>Runs the endpoint with a videos.read principal and a RAW JSON body string.</summary>
    private static List<PreviewSampleResult> PreviewRaw(string json)
    {
        var ext = NewExtension();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
        var result = ext.PreviewSampleAsync(RequestWithBody(json), principal, default).GetAwaiter().GetResult();
        var ok = Assert.IsType<Ok<List<PreviewSampleResult>>>(result);
        return ok.Value!;
    }

    private static PreviewSampleResult Sample(IEnumerable<PreviewSampleResult> all, string label)
        => all.Single(r => r.SampleLabel == label);

    [Fact]
    public void PreviewSample_DefaultOptions_VideoRendersTitle_NoFlags()
    {
        var all = Preview(new RenamerOptions());
        Assert.Equal(3, all.Count); // exactly the 3 server-side samples

        var video = Sample(all, "Video");
        // The default template is "{$date - }$title{ [$resolution]}"; the Video sample has date
        // 2021-03-14 and height 2160, which the engine buckets to $resolution "4K"
        // (ResolutionLabel.FromHeight), so both groups render: "2021-03-14 - The Example [4K]".
        Assert.Equal("2021-03-14 - The Example [4K].mp4", video.NewName);
        Assert.Equal("the.example.2021.WEBRip.mp4", video.OldName);
        Assert.Empty(video.Flags);
        Assert.Empty(video.DroppedFields);
    }

    [Fact]
    public void PreviewSample_DefaultTemplate_AudioSample_NoBracketsNoDanglingSeparator()
    {
        // The Audio sample has a date but NO height (so no $resolution), so the default's
        // "{ [$resolution]}" group collapses ENTIRELY (leading space + brackets + token) while the
        // "{$date - }" prefix stays, leaving a clean name with no dangling brackets.
        var all = Preview(new RenamerOptions()); // default FilenameTemplate = "{$date - }$title{ [$resolution]}"

        var audio = Sample(all, "Audio");
        Assert.Equal("2020-01-09 - Track One.flac", audio.NewName); // date prefix kept, no " []"
        Assert.DoesNotContain("[", audio.NewName);
        Assert.DoesNotContain("]", audio.NewName);
        Assert.Empty(audio.Flags);
    }

    [Fact]
    public void PreviewSample_StudioTitleResolutionTemplate_RendersVideoName()
    {
        var all = Preview(new RenamerOptions { FilenameTemplate = "$studio - $title [$resolution]" });

        var video = Sample(all, "Video");
        // height 2160 → ResolutionLabel.FromHeight(2160) == "4K" (the engine is the source of truth).
        Assert.Equal("Acme Studios - The Example [4K].mp4", video.NewName);
        Assert.Empty(video.Flags);
    }

    [Fact]
    public void PreviewSample_ImageSample_DropsEmptyCodecGroups_NoStrayPunctuation()
    {
        // {} group with only empty tokens collapses entirely (incl. its inner literals) — the image
        // sample has no codecs/duration, so the bracketed group disappears with no stray "[]".
        var all = Preview(new RenamerOptions
        {
            FilenameTemplate = "$title{ [$videoCodec $audioCodec]}",
        });

        var image = Sample(all, "Image");
        Assert.Equal("Sunset.jpg", image.NewName); // group dropped → no " []" left behind
    }

    [Fact]
    public void PreviewSample_TinyFilenameMax_FlagsLengthReduced_WithNamedDroppedFields()
    {
        // A template that uses early DropOrder fields + a tiny cap forces the reducer to drop them;
        // the flag is proven TRUTHFUL by asserting the named dropped fields, not just the boolean.
        var all = Preview(new RenamerOptions
        {
            FilenameTemplate = "$title $videoCodec $audioCodec $resolution",
            FilenameMax = 14, // "The Example" (11) + ".mp4" (4) = 15 > 14 still needs more drops
        });

        var video = Sample(all, "Video");
        Assert.Contains("length-reduced", video.Flags);
        Assert.NotEmpty(video.DroppedFields);
        // videoCodec/audioCodec/resolution are early in the default DropOrder and present in the
        // template — they must be among the named dropped fields (A2 wiring, not a string diff).
        Assert.Contains("videoCodec", video.DroppedFields);
        Assert.Contains("audioCodec", video.DroppedFields);
    }

    [Fact]
    public void PreviewSample_RequiredFieldMissing_FlagsGatingSkip()
    {
        // videoCodec is required, but the image and audio samples have none → gating-skip; the video
        // sample HAS videoCodec → not gated.
        var all = Preview(new RenamerOptions { RequiredFields = ["videoCodec"] });

        Assert.Contains("gating-skip", Sample(all, "Image").Flags);
        Assert.Contains("gating-skip", Sample(all, "Audio").Flags);
        Assert.DoesNotContain("gating-skip", Sample(all, "Video").Flags);
    }

    [Fact]
    public void PreviewSample_IllegalCharTemplate_FlagsSanitized()
    {
        // ':' is illegal and stripped by the sanitizer → the rendered name differs from raw → sanitized.
        var all = Preview(new RenamerOptions { FilenameTemplate = "$title: x" });

        var video = Sample(all, "Video");
        Assert.Contains("sanitized", video.Flags);
        Assert.DoesNotContain(":", video.NewName); // proof the illegal char is gone
    }

    [Fact]
    public void PreviewSample_NullOptions_FallsBackToDefaults()
    {
        var all = Preview(null); // null options → new RenamerOptions()
        // The default template "{$date - }$title{ [$resolution]}" + the Video sample's date + height
        // 2160 → $resolution "4K".
        Assert.Equal("2021-03-14 - The Example [4K].mp4", Sample(all, "Video").NewName);
    }

    [Fact]
    public async Task PreviewSample_WithoutVideosRead_Returns403_BeforeReadingBody()
    {
        var ext = NewExtension();

        // Hand a body stream that would THROW if read, proving the 403 short-circuits before any
        // body read (permission is enforced before work — including deserialization).
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new ThrowingStream();
        ctx.Request.ContentType = "application/json";

        var result = await ext.PreviewSampleAsync(ctx.Request, FakePrincipalAccessor.None(), default);

        Assert.Equal(403, StatusOf(result)); // permission denied
    }

    [Fact]
    public void PreviewSample_StringEnumBody_Parses_Returns200_WithRenderedNames()
    {
        // REGRESSION (UI-02 gap): the panel posts string enum values. The host's default minimal-API
        // JsonSerializerOptions has no JsonStringEnumConverter, so typed binding would 400. The endpoint
        // now parses with RenamerOptions.JsonOptions, so this MUST succeed and render the expected name.
        const string body = """
            {
              "Options": {
                "filenameTemplate": "$studio - $title [$resolution]",
                "case": "None",
                "performers": { "separator": ", ", "maxCount": 3, "onOverflow": "KeepFirst", "sort": "NameAsc" },
                "tags": { "separator": " ", "onOverflow": "DropAll", "sort": "None" }
              }
            }
            """;

        var all = PreviewRaw(body);
        Assert.Equal(3, all.Count);

        var video = Sample(all, "Video");
        // height 2160 → "4K" (engine is source of truth); proves the string-enum body deserialized AND
        // rendered (not a 400/throw).
        Assert.Equal("Acme Studios - The Example [4K].mp4", video.NewName);
    }

    [Fact]
    public void PreviewSample_LowerCaseStringEnum_AppliesCaseTransform()
    {
        // "case":"Lower" must deserialize to CaseTransform.Lower (not 400) and actually lower the name —
        // proves the enum VALUE flows through, not just that parsing didn't throw.
        const string body = """{ "Options": { "filenameTemplate": "$title", "case": "Lower" } }""";

        var video = Sample(PreviewRaw(body), "Video");
        Assert.Equal("the example.mp4", video.NewName);
    }

    [Fact]
    public async Task PreviewSample_MalformedJson_Returns400()
    {
        var ext = NewExtension();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

        var result = await ext.PreviewSampleAsync(RequestWithBody("{ not valid json "), principal, default);

        Assert.Equal(400, StatusOf(result)); // malformed body → clean 400, not an unhandled throw
    }

    [Fact]
    public void PreviewSample_EmptyBody_FallsBackToDefaults_Returns200()
    {
        // No content (empty body) deserializes to null → safe defaults, not a 400.
        // The default template "{$date - }$title{ [$resolution]}" + the Video sample's date + height
        // 2160 → $resolution "4K".
        var video = Sample(PreviewRaw(""), "Video");
        Assert.Equal("2021-03-14 - The Example [4K].mp4", video.NewName);
    }

    [Fact]
    public void PreviewSample_SingleCanonicalPascalCaseKey_RendersTheLiveTemplate()
    {
        // Characterization of the wire-fix: the dual-source preview bug was that a legacy blob's
        // stale camelCase `filenameTemplate` rode into the body AFTER the live PascalCase `FilenameTemplate`
        // and won under System.Text.Json case-insensitive last-write-wins. The real fix is client-side
        // (frontend `normalizeOptions` now sends ONE canonical key per property). This test documents the
        // backend contract the fix relies on: given a clean SINGLE-PascalCase-key body (no camelCase
        // duplicate — the shape the normalized frontend now always sends), the endpoint renders using that
        // live template value. No backend normalize is added — the binder is unchanged.
        const string body = """
            {
              "Options": {
                "FilenameTemplate": "$title LIVE",
                "FolderTemplate": "",
                "Case": "None"
              }
            }
            """;

        var video = Sample(PreviewRaw(body), "Video");
        Assert.Equal("The Example LIVE.mp4", video.NewName);
    }

    /// <summary>A read-once stream that throws on any read — proves the 403 path never touches the body.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("body must not be read");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new IOException("body must not be read");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new IOException("body must not be read");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
