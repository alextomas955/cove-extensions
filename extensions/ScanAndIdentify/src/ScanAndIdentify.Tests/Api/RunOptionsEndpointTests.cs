using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using ScanAndIdentify.Options;
using ScanAndIdentify.Tests.TestSupport;

namespace ScanAndIdentify.Tests.Api;

public sealed class RunOptionsEndpointTests
{
    [Fact]
    public async Task GetOptions_AnswersTheStoredRecord_ForAPrincipalThatMayConfigure()
    {
        var store = new FakeStore();
        await store.SetAsync(RunOptionsStore.Key, """{"BackfillFingerprints":false}""");
        var extension = ScanAndIdentifyFixture.Create(store);

        var result = await extension.GetOptionsAsync(Configurer);

        var ok = Assert.IsType<Ok<RunOptions>>(HttpResultUnwrap.Unwrap(result));
        Assert.False(ok.Value!.BackfillFingerprints);
    }

    [Fact]
    public async Task GetOptions_Refuses_ForAPrincipalWithoutTheConfigurePermission()
    {
        var store = new FakeStore();
        var extension = ScanAndIdentifyFixture.Create(store);

        var result = await extension.GetOptionsAsync(FakePrincipalAccessor.None());

        Assert.IsType<ForbiddenCode>(HttpResultUnwrap.Unwrap(result));
        Assert.Equal(0, store.SetCallCount);
    }

    [Fact]
    public async Task SaveOptions_PersistsEveryFieldThatTheNextReadAnswers()
    {
        var store = new FakeStore();
        var extension = ScanAndIdentifyFixture.Create(store);
        var flipped = new RunOptions
        {
            Thumbnails = false,
            Previews = false,
            Sprites = false,
            VrStereo = true,
            ImageThumbnails = false,
            GalleryThumbnails = false,
            BackfillFingerprints = false,
            Rescan = true,
            CreateMissingEntities = false,
            MarkOrganized = true,
        };

        var saved = await extension.SaveOptionsAsync(
            Body(JsonSerializer.Serialize(flipped, RunOptions.JsonOptions)), Configurer);
        var read = await extension.GetOptionsAsync(Configurer);

        Assert.IsType<NoContent>(HttpResultUnwrap.Unwrap(saved));
        var ok = Assert.IsType<Ok<RunOptions>>(HttpResultUnwrap.Unwrap(read));
        Assert.Equal(flipped, ok.Value);
        Assert.NotEqual(new RunOptions(), ok.Value);
    }

    [Fact]
    public async Task SaveOptions_RefusesABodyThatIsNotJson_AndWritesNothing()
    {
        var store = new FakeStore();
        var extension = ScanAndIdentifyFixture.Create(store);

        var result = await extension.SaveOptionsAsync(Body("not json"), Configurer);

        var bad = Assert.IsType<BadRequest<ErrorCode>>(HttpResultUnwrap.Unwrap(result));
        Assert.Equal("INVALID_OPTIONS", bad.Value!.Code);
        Assert.Equal(0, store.SetCallCount);
    }

    [Fact]
    public async Task SaveOptions_Refuses_ForAPrincipalWithoutTheConfigurePermission()
    {
        var store = new FakeStore();
        var extension = ScanAndIdentifyFixture.Create(store);

        var result = await extension.SaveOptionsAsync(
            Body("""{"BackfillFingerprints":false}"""), FakePrincipalAccessor.None());

        Assert.IsType<ForbiddenCode>(HttpResultUnwrap.Unwrap(result));
        Assert.Equal(0, store.SetCallCount);
    }

    private static FakePrincipalAccessor Configurer
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    // The write binds the raw request so a malformed body is the route's own 400, so a test has to
    // supply a body rather than a bound record.
    private static HttpRequest Body(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return context.Request;
    }
}
