using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Safety;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests;

/// <summary>
/// SEC-02: the root-overlap warning. The pure <see cref="RootOverlapDetector"/> flags a Cove library root
/// that contains — or is contained by — a Whisparr root (either direction), normalizes separators
/// (case-sensitive — the Linux/Docker target) before comparing, and reports nothing for disjoint roots. The <c>/root-overlap</c> endpoint is a
/// best-effort advisory read: 403-first on <c>extensions.read</c>, and when authorized returns the
/// <c>{ overlaps, warning }</c> shape. The warning is advisory only (never a hard gate), so a host with no
/// resolvable Cove roots still answers 200 with an empty overlap set.
/// </summary>
public sealed class RootOverlapTests
{
    private static Ext NewExtension(FakeStore? store = null)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store ?? new FakeStore());
        return ext;
    }

    private static WhisparrClient ClientReturning(string json)
        => new(new HttpClient(FakeHttpMessageHandler.Json(json)));

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    [Fact]
    public void CoveRootNestedInsideWhisparrRoot_ProducesOneOverlap()
    {
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["/data/media"], coveRoots: ["/data/media/movies"]);

        var overlap = Assert.Single(overlaps);
        Assert.Equal("/data/media", overlap.WhisparrRoot);
        Assert.Equal("/data/media/movies", overlap.CoveRoot);
    }

    [Fact]
    public void WhisparrRootNestedInsideCoveRoot_AlsoWarns()
    {
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["/data/media/movies"], coveRoots: ["/data/media"]);

        var overlap = Assert.Single(overlaps);
        Assert.Equal("/data/media/movies", overlap.WhisparrRoot);
        Assert.Equal("/data/media", overlap.CoveRoot);
    }

    [Fact]
    public void IdenticalRoots_Warn()
    {
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["/data/media"], coveRoots: ["/data/media"]);

        Assert.Single(overlaps);
    }

    [Fact]
    public void DisjointRoots_ProduceNoOverlap()
    {
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["/data/whisparr"], coveRoots: ["/data/cove"]);

        Assert.Empty(overlaps);
    }

    [Fact]
    public void SiblingPrefix_IsNotAnOverlap()
    {
        // "/data/media-evil" must NOT be treated as inside "/data/media" — containment is segment-bounded.
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["/data/media"], coveRoots: ["/data/media-evil"]);

        Assert.Empty(overlaps);
    }

    [Fact]
    public void ComparisonIsSeparatorNormalized_CaseSensitive()
    {
        // Separators unify (\ → /), so a Windows-style root overlaps its forward-slash child.
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: [@"C:\Data\Media"], coveRoots: ["C:/Data/Media/Movies"]);
        Assert.Single(overlaps);

        // But comparison is case-SENSITIVE (WR-01): a case-mismatched pair is NOT treated as the same root.
        var caseMismatch = RootOverlapDetector.Detect(
            whisparrRoots: [@"C:\Data\Media"], coveRoots: ["c:/data/media/Movies"]);
        Assert.Empty(caseMismatch);
    }

    [Fact]
    public void EmptyOrWhitespaceRoots_AreIgnored()
    {
        var overlaps = RootOverlapDetector.Detect(
            whisparrRoots: ["", "   "], coveRoots: ["/data/media"]);

        Assert.Empty(overlaps);
    }

    [Fact]
    public async Task RootOverlap_WithoutRead_Returns403()
    {
        var result = await NewExtension().RootOverlapAsync(
            ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task RootOverlap_NullPrincipal_Returns403()
    {
        var result = await NewExtension().RootOverlapAsync(
            ClientReturning("[]"), FakePrincipalAccessor.NullPrincipal(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task RootOverlap_WithRead_Returns200_WithOverlapsAndWarningShape()
    {
        var result = await NewExtension().RootOverlapAsync(
            ClientReturning("[]"), FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);

        Assert.NotEqual(403, StatusOf(result));
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        // The advisory shape is always { overlaps, warning } — both properties are present.
        Assert.NotNull(value.GetType().GetProperty("overlaps"));
        Assert.True(value.GetType().GetProperty("warning") is not null);
    }
}
