using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Client;

/// <summary>
/// The executable safety contract for the Whisparr file-settings config transport (item 6): the naming +
/// media-management singletons are whole-object replaces, so a write MUST be read-modify-write — GET the full
/// object, flip ONLY the whitelisted booleans, PUT the complete object back. These tests drive
/// <see cref="V3Adapter"/> → <see cref="WhisparrClient"/> against a programmable
/// <see cref="FakeHttpMessageHandler"/> so the exact outbound order + captured PUT body are asserted with no
/// live Whisparr. The load-bearing invariants: a PUT never drops an unknown Whisparr field (round-trip
/// preservation), the server never adds a field the GET did not return (whitelist), a non-Ok GET issues no
/// PUT, and v2 defers with zero wire calls.
/// </summary>
public sealed class ConfigTransportTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";

    private static V3Adapter V3(FakeHttpMessageHandler handler) => new(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

    private static V2Adapter V2(FakeHttpMessageHandler handler) => new(new WhisparrClient(new HttpClient(handler)));

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    // A naming singleton with the two typed booleans PLUS unknown fields (id + two format fields) that a
    // partial PUT would wipe — the round-trip must preserve them.
    private const string NamingBody =
        """{"id":1,"renameMovies":false,"replaceIllegalCharacters":true,"standardMovieFormat":"{Movie Title}","colonReplacementFormat":"delete"}""";

    private const string MediaBody =
        """{"id":1,"autoRenameFolders":false,"deleteEmptyFolders":true,"recycleBin":"/data/.recycle"}""";

    [Fact]
    public async Task Get_file_settings_reads_both_config_singletons()
    {
        var handler = FakeHttpMessageHandler.Sequence(Ok(NamingBody), Ok(MediaBody));

        var result = await V3(handler).GetFileSettingsAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var settings = result.Value!;
        Assert.False(settings.RenameMovies);
        Assert.True(settings.ReplaceIllegalCharacters);
        Assert.False(settings.AutoRenameFolders);
        Assert.True(settings.DeleteEmptyFolders);
        Assert.Equal($"{BaseUrl}/api/v3/config/naming", handler.Requests[0].Url);
        Assert.Equal($"{BaseUrl}/api/v3/config/mediamanagement", handler.Requests[1].Url);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Edit_flipping_rename_movies_preserves_unknown_fields_and_flips_only_the_target()
    {
        // GET naming → PUT naming → GET media (no media PUT: no media toggle supplied).
        var handler = FakeHttpMessageHandler.Sequence(Ok(NamingBody), Ok(NamingBody), Ok(MediaBody));

        var result = await V3(handler).EditFileSettingsAsync(
            BaseUrl, ApiKey, new WhisparrFileSettingsRequest(RenameMovies: true), CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);

        var put = handler.Requests[1];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal($"{BaseUrl}/api/v3/config/naming", put.Url);

        using var body = JsonDocument.Parse(put.Body!);
        var root = body.RootElement;
        // Only the target boolean flipped; the sibling boolean is preserved.
        Assert.True(root.GetProperty("renameMovies").GetBoolean());
        Assert.True(root.GetProperty("replaceIllegalCharacters").GetBoolean());
        // The unknown fields survive byte-for-value — a partial PUT would have wiped these.
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.Equal("{Movie Title}", root.GetProperty("standardMovieFormat").GetString());
        Assert.Equal("delete", root.GetProperty("colonReplacementFormat").GetString());
    }

    [Fact]
    public async Task Edit_never_adds_a_field_the_get_did_not_return()
    {
        // Whitelist: the PUT body is built from the GET result + the four booleans, never a client object, so its
        // field set must equal the GET singleton's field set (only values change, never the key set).
        var handler = FakeHttpMessageHandler.Sequence(Ok(NamingBody), Ok(NamingBody), Ok(MediaBody));

        await V3(handler).EditFileSettingsAsync(
            BaseUrl, ApiKey, new WhisparrFileSettingsRequest(RenameMovies: true), CancellationToken.None);

        Assert.Equal(KeySet(NamingBody), KeySet(handler.Requests[1].Body!));
    }

    [Fact]
    public async Task Edit_flipping_a_media_toggle_read_modify_writes_mediamanagement()
    {
        // GET naming (no naming PUT) → GET media → PUT media.
        var handler = FakeHttpMessageHandler.Sequence(Ok(NamingBody), Ok(MediaBody), Ok(MediaBody));

        var result = await V3(handler).EditFileSettingsAsync(
            BaseUrl, ApiKey, new WhisparrFileSettingsRequest(AutoRenameFolders: true), CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(3, handler.CallCount);

        var put = handler.Requests[2];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal($"{BaseUrl}/api/v3/config/mediamanagement", put.Url);

        using var body = JsonDocument.Parse(put.Body!);
        Assert.True(body.RootElement.GetProperty("autoRenameFolders").GetBoolean());
        Assert.True(body.RootElement.GetProperty("deleteEmptyFolders").GetBoolean());
        Assert.Equal("/data/.recycle", body.RootElement.GetProperty("recycleBin").GetString());
        // No naming PUT was issued (no naming toggle supplied).
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put && r.Url.EndsWith("/config/naming", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Edit_non_ok_get_short_circuits_with_no_put()
    {
        // A bad-key GET must abort before any PUT — a write never proceeds on an unread config.
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized);

        var result = await V3(handler).EditFileSettingsAsync(
            BaseUrl, ApiKey, new WhisparrFileSettingsRequest(RenameMovies: true), CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
        Assert.Equal(1, handler.CallCount);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task V2_get_file_settings_defers_with_no_wire_call()
    {
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await V2(handler).GetFileSettingsAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Equal("v2", result.DetectedVersion);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task V2_edit_file_settings_defers_with_no_wire_call()
    {
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await V2(handler).EditFileSettingsAsync(
            BaseUrl, ApiKey, new WhisparrFileSettingsRequest(RenameMovies: true), CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Equal(0, handler.CallCount);
    }

    private static HashSet<string> KeySet(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
    }
}
