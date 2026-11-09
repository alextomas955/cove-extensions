using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Renamer.Api;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

// Both entity routes refuse a kind this extension does not rename. The refusal is decided before the
// permission check, and has to be: the permission to require is chosen per kind, and PermissionsFor
// throws for a kind that is not renamable. So a parse that started admitting one of those kinds would
// not widen the feature, it would reach that throw and answer 500 where the route answers 400.
public sealed class UnsupportedEntityTypeTests
{
    private const string Unsupported = "gallery";

    [Fact]
    public async Task PreviewAsync_AKindThisExtensionDoesNotRename_RefusesWithTheKindCode()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var ext = RenamerFixture.CreateWithStore();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new RenamerRequest(Unsupported, [1]), db, principal, default);

            var bad = Assert.IsType<BadRequestProblem>(Unwrap(result));
            Assert.Equal("UNSUPPORTED_ENTITY_TYPE", bad.Problem.Code);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task RenamerEnqueue_AKindThisExtensionDoesNotRename_RefusesAndDoesNotEnqueue()
    {
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);

        var result = await ext.RenamerEnqueue(
            new RenamerRequest(Unsupported, [1]), principal, jobs,
            new RecordingAuthorizationService(), default);

        var bad = Assert.IsType<BadRequestProblem>(Unwrap(result));
        Assert.Equal("UNSUPPORTED_ENTITY_TYPE", bad.Problem.Code);
        Assert.Empty(jobs.Enqueued);
    }
}
