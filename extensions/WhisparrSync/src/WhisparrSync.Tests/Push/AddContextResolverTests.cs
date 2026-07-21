using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Options;
using WhisparrSync.Push;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Push;

/// <summary>
/// The executable contract for the per-add root-folder derivation (there is no stored root id): the owned-file
/// prefix-match (single / multi-root disambiguation / segment-boundary rejection / PathTranslation) and the
/// file-less fallback (single root / first Accessible / none). Drives <see cref="AddContextResolver"/> against a
/// programmable <see cref="FakeHttpMessageHandler"/> serving <c>GET /api/v3/rootfolder</c>, so it needs no host
/// and no live Whisparr.
/// </summary>
public sealed class AddContextResolverTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";

    private static AddContextResolver ResolverFor(FakeHttpMessageHandler handler, WhisparrOptions? options = null)
        => new(
            new WhisparrClient(new HttpClient(handler)),
            options ?? new WhisparrOptions { BaseUrl = BaseUrl, ApiKey = ApiKey });

    private static FakeHttpMessageHandler Roots(params (int Id, string Path, bool Accessible)[] roots)
        => FakeHttpMessageHandler.Json(JsonSerializer.Serialize(
            Array.ConvertAll(roots, r => new { id = r.Id, path = r.Path, accessible = r.Accessible, freeSpace = 1L })));

    // ---- file-less fallback ----

    [Fact]
    public async Task Fallback_with_a_single_root_returns_that_root()
    {
        var result = await ResolverFor(Roots((1, "/data/media", true))).ResolveFallbackRootAsync(CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal("/data/media", result.Value);
    }

    [Fact]
    public async Task Fallback_with_multiple_roots_returns_the_first_accessible_root()
    {
        // The first row is inaccessible, so the fallback skips it and takes the first Accessible one.
        var result = await ResolverFor(Roots((1, "/mnt/down", false), (2, "/data/media", true), (3, "/data/other", true)))
            .ResolveFallbackRootAsync(CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal("/data/media", result.Value);
    }

    [Fact]
    public async Task Fallback_with_no_accessible_root_is_unreachable()
    {
        var result = await ResolverFor(Roots((1, "/mnt/a", false), (2, "/mnt/b", false)))
            .ResolveFallbackRootAsync(CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
    }

    [Fact]
    public async Task Fallback_with_an_empty_root_list_is_unreachable()
    {
        var result = await ResolverFor(FakeHttpMessageHandler.Json("[]")).ResolveFallbackRootAsync(CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
    }

    // ---- owned-file prefix-match ----

    [Fact]
    public async Task File_prefix_match_returns_the_containing_root()
    {
        var result = await ResolverFor(Roots((1, "/data/media", true)))
            .ResolveRootForFileAsync("/data/media/studio/scene.mkv", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal("/data/media", result.Value);
    }

    [Fact]
    public async Task File_prefix_match_disambiguates_among_multiple_roots()
    {
        var result = await ResolverFor(Roots((1, "/data/a", true), (2, "/data/b", true)))
            .ResolveRootForFileAsync("/data/b/studio/scene.mkv", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal("/data/b", result.Value);
    }

    [Fact]
    public async Task File_with_no_containing_root_is_unreachable_never_a_wrong_root()
    {
        var result = await ResolverFor(Roots((1, "/data/media", true)))
            .ResolveRootForFileAsync("/elsewhere/scene.mkv", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
    }

    // The segment-boundary rule: a file under "/data/media-extra" is NOT beneath the root "/data/media" (a raw
    // StartsWith would wrongly match), so it resolves to no root rather than the wrong one.
    [Fact]
    public async Task File_under_a_sibling_prefixed_directory_does_not_match_at_a_non_boundary()
    {
        var result = await ResolverFor(Roots((1, "/data/media", true)))
            .ResolveRootForFileAsync("/data/media-extra/scene.mkv", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
    }

    // ---- PathTranslation (Docker-vs-local) ----

    [Fact]
    public async Task File_is_translated_by_the_cove_prefix_before_matching()
    {
        // Cove sees the library at /cove/library; the containerized Whisparr mounts it at /data/media. The rule
        // rewrites the Cove path into Whisparr's view so it prefix-matches the /data/media root.
        var options = new WhisparrOptions
        {
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            PathTranslation = [new PathTranslationRule("/cove/library", "/data/media")],
        };

        var result = await ResolverFor(Roots((1, "/data/media", true)), options)
            .ResolveRootForFileAsync("/cove/library/studio/scene.mkv", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal("/data/media", result.Value);
    }

    [Fact]
    public async Task Empty_path_translation_is_identity()
    {
        // With no rule, the Cove path is matched as-is: a path that does not sit under the root fails to match.
        var result = await ResolverFor(Roots((1, "/data/media", true)))
            .ResolveRootForFileAsync("/cove/library/studio/scene.mkv", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
    }

    // A non-Ok root list propagates verbatim rather than silently proceeding to an add without a root.
    [Fact]
    public async Task A_bad_key_root_list_propagates_on_both_derivations()
    {
        var fileResult = await ResolverFor(FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.Unauthorized))
            .ResolveRootForFileAsync("/data/media/scene.mkv", CancellationToken.None);
        Assert.Equal(WhisparrResultState.BadKey, fileResult.State);

        var fallbackResult = await ResolverFor(FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.Unauthorized))
            .ResolveFallbackRootAsync(CancellationToken.None);
        Assert.Equal(WhisparrResultState.BadKey, fallbackResult.State);
    }
}
