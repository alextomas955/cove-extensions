using System.Net;
using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Data;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

public sealed class EntityIdsCapTests
{
    // Keep in sync with Renamer.Api.cs MaxEntityIdsPerRequest. Over-cap = cap + 1.
    private const int Cap = 1000;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static int StatusOf(IResult result) => Assert.IsType<IStatusCodeHttpResult>(Unwrap(result), exactMatch: false).StatusCode ?? 0;

    // The cap's own refusal, not one of the other 400s, carrying the bound so a caller can batch to fit.
    private static void AssertTooManyIds(IResult result)
    {
        var bad = Assert.IsType<BadRequest<ErrorCode>>(Unwrap(result));
        Assert.Equal(new ErrorCode("TOO_MANY_IDS", Cap), bad.Value);
    }

    [Fact]
    public async Task PreviewAsync_OverCapIds_Returns400_BeforeAnyDatabaseRead()
    {
        var interceptor = new CommandCountingInterceptor();
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            await using var db = new CoveContext(
                new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).AddInterceptors(interceptor).Options,
                principalAccessor: null);
            await db.Database.EnsureCreatedAsync();
            await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "raw.mkv", "Film");
            interceptor.ReaderCount = 0;

            var ext = RenamerFixture.CreateWithStore();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
            var ids = Enumerable.Range(1, Cap + 1).ToArray();

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", ids), db, principal, default);

            AssertTooManyIds(result);
            Assert.Equal(0, interceptor.ReaderCount);
        }
        finally
        {
            await connection.DisposeAsync();
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

        AssertTooManyIds(result);
        Assert.Empty(jobs.Enqueued);
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
