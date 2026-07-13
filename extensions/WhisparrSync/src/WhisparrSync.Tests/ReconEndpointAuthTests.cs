using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Matching;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests;

/// <summary>
/// Security-critical (T-02-03): the reconciliation endpoints enforce their permission themselves (the
/// host <c>[RequiresPermission]</c> filter is inert on minimal-API endpoints). These prove the 403-first
/// pair for each route — <c>/reconciliation</c> is the only read-gated route (a pure match-map read that
/// reaches no credentials); <c>/preview-sync</c> + <c>/match/confirm</c> + <c>/match/reject</c> are
/// configure-gated (they reach the stored key / write the map), so a read-only principal is refused
/// (Elevation-of-Privilege guard, CR-01). They also prove confirm/reject validate the submitted ids
/// against the current diff BEFORE writing the map (a forged id writes nothing, T-02-03-C).
/// </summary>
public sealed class ReconEndpointAuthTests
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

    // A needs-review diff with one fuzzy suggestion: Whisparr movie 10 ↔ Cove video 5. Confirm/reject
    // validate a submitted pair against exactly this shape.
    private static ReconciliationDiff NeedsReviewDiff()
    {
        var movie = new WhisparrMovie(
            Id: 10, Title: "Scene A", Year: 2020, StashId: null, ForeignId: null,
            ItemType: "scene", Monitored: true, HasFile: true, MovieFile: null);
        var cove = new CoveVideo(
            CoveId: 5, Title: "Scene A", Date: new DateOnly(2020, 1, 1),
            StashIds: [], FilePaths: [], Fingerprints: []);
        var row = new MatchResult(movie, cove, MatchedBy.Fuzzy, MatchOutcome.NeedsReview, AutoApplies: false);
        return new ReconciliationDiff(
            Matched: [], Unmatched: [], NeedsReview: [row], Counts: new ReconciliationCounts(0, 0, 1, 1));
    }

    [Fact]
    public async Task Reconciliation_WithoutRead_Returns403()
    {
        var result = await NewExtension().ReconciliationAsync(FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task Reconciliation_WithRead_IsNotForbidden()
    {
        var result = await NewExtension().ReconciliationAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.NotEqual(403, StatusOf(result));
    }

    [Fact]
    public async Task Reconciliation_NullPrincipal_Returns403()
    {
        var result = await NewExtension().ReconciliationAsync(FakePrincipalAccessor.NullPrincipal(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task PreviewSync_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().PreviewSyncAsync(
            ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task PreviewSync_WithReadOnly_Returns403()
    {
        // CR-01 / EoP: /preview-sync reaches the stored key to call Whisparr, so extensions.read (a strictly
        // lower privilege) must NOT reach it — only extensions.configure.
        var result = await NewExtension().PreviewSyncAsync(
            ClientReturning("[]"), FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task PreviewSync_NullPrincipal_Returns403()
    {
        var result = await NewExtension().PreviewSyncAsync(
            ClientReturning("[]"), FakePrincipalAccessor.NullPrincipal(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task MatchConfirm_WithoutConfigure_Returns403()
    {
        var req = new Ext.MatchDecisionRequest(5, 10);
        var result = await NewExtension().MatchConfirmAsync(
            req, ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task MatchConfirm_WithReadOnly_Returns403()
    {
        var req = new Ext.MatchDecisionRequest(5, 10);
        var result = await NewExtension().MatchConfirmAsync(
            req, ClientReturning("[]"), FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task MatchReject_WithoutConfigure_Returns403()
    {
        var req = new Ext.MatchDecisionRequest(5, 10);
        var result = await NewExtension().MatchRejectAsync(
            req, ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task MatchReject_NullPrincipal_Returns403()
    {
        var req = new Ext.MatchDecisionRequest(5, 10);
        var result = await NewExtension().MatchRejectAsync(
            req, ClientReturning("[]"), FakePrincipalAccessor.NullPrincipal(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task MatchConfirm_ForgedId_NotInDiff_IsRejected_WithoutWriting()
    {
        // T-02-03-C: a (coveId, whisparrMovieId) pair absent from the current diff must be refused BEFORE
        // any write — the map is never touched with a forged id.
        var store = new FakeStore();
        var req = new Ext.MatchDecisionRequest(CoveId: 999, WhisparrMovieId: 10); // coveId not in the diff
        var result = await NewExtension(store).ApplyMatchDecisionAsync(
            req, NeedsReviewDiff(), MatchStatus.Confirmed, default);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal(0, store.SetCallCount); // the map was never written
    }

    [Fact]
    public async Task MatchReject_ForgedId_NotInDiff_IsRejected_WithoutWriting()
    {
        var store = new FakeStore();
        var req = new Ext.MatchDecisionRequest(CoveId: 5, WhisparrMovieId: 999); // movie id not in the diff
        var result = await NewExtension(store).ApplyMatchDecisionAsync(
            req, NeedsReviewDiff(), MatchStatus.Rejected, default);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal(0, store.SetCallCount);
    }

    [Fact]
    public async Task MatchConfirm_ValidNeedsReviewPair_WritesTheMap()
    {
        // A pair present in needs-review validates and writes exactly one map entry.
        var store = new FakeStore();
        var req = new Ext.MatchDecisionRequest(CoveId: 5, WhisparrMovieId: 10);
        var result = await NewExtension(store).ApplyMatchDecisionAsync(
            req, NeedsReviewDiff(), MatchStatus.Confirmed, default);

        Assert.NotEqual(403, StatusOf(result));
        Assert.NotEqual(400, StatusOf(result));
        Assert.Equal(1, store.SetCallCount); // the decision persisted to the match store
    }

    [Fact]
    public async Task MatchReject_ValidNeedsReviewPair_WritesTheMap()
    {
        var store = new FakeStore();
        var req = new Ext.MatchDecisionRequest(CoveId: 5, WhisparrMovieId: 10);
        var result = await NewExtension(store).ApplyMatchDecisionAsync(
            req, NeedsReviewDiff(), MatchStatus.Rejected, default);

        Assert.NotEqual(403, StatusOf(result));
        Assert.NotEqual(400, StatusOf(result));
        Assert.Equal(1, store.SetCallCount);
    }
}
