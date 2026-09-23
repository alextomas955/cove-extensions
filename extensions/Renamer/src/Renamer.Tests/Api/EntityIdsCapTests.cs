using System.Net;
using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

public sealed class EntityIdsCapTests
{
    // Keep in sync with Renamer.Api.cs MaxEntityIdsPerRequest. Over-cap = cap + 1.
    private const int Cap = 1000;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static int StatusOf(IResult result) => Assert.IsType<IStatusCodeHttpResult>(Unwrap(result), exactMatch: false).StatusCode ?? 0;

    [Fact]
    public async Task PreviewAsync_OverCapIds_Returns400_AndMutatesNothing()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, _, fileId) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "raw.mkv", "Film");
            var (beforeName, beforePath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            var ext = RenamerFixture.CreateWithStore();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            var ids = Enumerable.Range(1, Cap + 1).ToArray(); // over the cap by one.

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", ids), db, principal, default);

            Assert.Equal(400, StatusOf(result));

            // The reject happens before any planner/DB work - the seeded row is untouched.
            var (afterName, afterPath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal(beforeName, afterName);
            Assert.Equal(beforePath, afterPath);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task RenamerEnqueue_OverCapIds_Returns400_AndDoesNotEnqueue()
    {
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
        var ids = Enumerable.Range(1, Cap + 1).ToArray(); // over the cap by one.

        var result = await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("video", ids), principal, jobs,
            new RecordingAuthorizationService(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Empty(jobs.Enqueued); // no work scheduled for an over-cap request.
    }

    [Fact]
    public async Task RenamerEnqueue_AtCapIds_PassesTheBound_AndEnqueues()
    {
        // Exactly at the cap is allowed - the bound rejects only what exceeds it.
        var ext = RenamerFixture.CreateWithStore();
        var jobs = new RecordingJobService();
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);
        var ids = Enumerable.Range(1, Cap).ToArray();

        var result = await ext.RenamerEnqueue(
            new global::Renamer.Api.RenamerRequest("video", ids), principal, jobs,
            new RecordingAuthorizationService(), default);

        Assert.Equal(202, StatusOf(result));
        Assert.Single(jobs.Enqueued);
    }

    public static TheoryData<string, string, string> AbsentIdArrayRequests()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var body in new[]
        {
            """{"entityType":"video"}""",
            """{"entityType":"video","entityIds":null}""",
        })
        {
            data.Add("/preview", Permissions.VideosRead, body);
            data.Add("/renamer", Permissions.VideosWrite, body);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AbsentIdArrayRequests))]
    public async Task AbsentIdArray_Returns400_MissingEntityIds(string path, string permission, string body)
    {
        await using var host = await TransportHost.BootAsync(FakePrincipalAccessor.WithPermissions(permission));
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await host.Client.PostAsync(TransportHost.BaseRoute + path, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = JsonSerializer.Deserialize<ErrorCode>(await response.Content.ReadAsStringAsync(), Web);
        Assert.Equal("MISSING_ENTITY_IDS", error?.Code);
    }
}
