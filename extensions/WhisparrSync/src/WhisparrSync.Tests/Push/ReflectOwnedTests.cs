using System.Net;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using WhisparrSync.Push;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Push;

/// <summary>
/// The folder-per-scene guard contract for v3 owned-import orchestration: a scene alone in its own folder is
/// ADOPTED in place (the movie path is re-pointed to Cove's folder + a rescan, zero duplication); two owned
/// scenes sharing one parent directory (a flat layout) fall back to the copy import and the result carries a
/// message — never a silent wrong-link on a shared directory. Drives the full
/// <see cref="SceneActions"/> → <c>V3Adapter</c> → <see cref="WhisparrClient"/> composition against a
/// programmable <see cref="FakeHttpMessageHandler"/> and a seeded <see cref="FakeCoveLibraryPort"/>.
/// </summary>
public sealed class ReflectOwnedTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string RootPath = "/data/media";
    private const int ProfileId = 4;
    private const int StudioCoveId = 7;
    private const string StashA = "3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b";
    private const string StashB = "9a4c1e2b-3d5f-4a6b-8c7d-1e2f3a4b5c6d";
    private static readonly int[] OriginTags = [5];

    private static WhisparrOptions V3Options => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        SelectedVersion = "v3",
        DetectedVersion = "3.3.4.808",
        QualityProfileId = ProfileId,
    };

    private static SceneActions ActionsFor(FakeHttpMessageHandler handler, FakeCoveLibraryPort library)
        => new(new WhisparrClient(new HttpClient(handler)), V3Options, library);

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static CoveVideo OwnedVideo(string stashId, string filePath)
        => new(0, "A Cove Scene", null, [stashId], [], [filePath], []);

    private static string Movie(int id, string stashId, bool hasFile) => JsonSerializer.Serialize(new
    {
        id,
        foreignId = stashId,
        stashId,
        title = "A Scene",
        monitored = true,
        hasFile,
        qualityProfileId = ProfileId,
        rootFolderPath = RootPath,
        tags = OriginTags,
    });

    private static string MovieList(params string[] rows) => $"[{string.Join(",", rows)}]";

    private static string ManualImportListing(string path) => JsonSerializer.Serialize(new[]
    {
        new
        {
            path,
            quality = new { quality = new { id = 7, name = "WEB-DL 1080p" }, revision = new { version = 1 } },
            languages = new[] { new { id = 1, name = "English" } },
        },
    });

    [Fact]
    public async Task FolderPerScene_adopts_each_scene_in_place_no_flat_message()
    {
        const string folderA = "/data/media/Scene A [" + StashA + "]";
        const string folderB = "/data/media/Scene B [" + StashB + "]";
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(
            EntityKind.Studio, StudioCoveId,
            OwnedVideo(StashA, folderA + "/a.mkv"),
            OwnedVideo(StashB, folderB + "/b.mkv"));

        var handler = FakeHttpMessageHandler.Sequence(
            Ok(MovieList(Movie(42, StashA, hasFile: false), Movie(43, StashB, hasFile: false))), // ListMovies
            Ok(Movie(42, StashA, hasFile: false)),          // scene A: PUT /movie/42 (re-point)
            Ok("{}"),                                       //          POST /command (RescanMovie)
            Ok(MovieList(Movie(42, StashA, hasFile: true))), //         GET verify -> hasFile:true
            Ok(Movie(43, StashB, hasFile: false)),          // scene B: PUT /movie/43 (re-point)
            Ok("{}"),                                       //          POST /command (RescanMovie)
            Ok(MovieList(Movie(43, StashB, hasFile: true)))); //        GET verify -> hasFile:true

        var result = await ActionsFor(handler, library)
            .ReflectOwnedAsync(EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Total);
        Assert.Equal(2, result.Value.Succeeded);
        Assert.Equal(0, result.Value.Failed);
        Assert.Null(result.Value.Message);   // folder-per-scene: no flat-layout fall-back reported

        // Each scene adopts in place: a PUT re-point carrying its OWN folder + a RescanMovie command. No copy
        // (ManualImport / manualimport listing) and no add (POST /movie).
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Put && r.Url.Contains("/api/v3/movie/")));
        Assert.Contains(handler.Requests, r => r.Url.EndsWith("/api/v3/movie/42", StringComparison.Ordinal) && r.Body!.Contains($"\"path\":\"{folderA}\"", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, r => r.Url.EndsWith("/api/v3/movie/43", StringComparison.Ordinal) && r.Body!.Contains($"\"path\":\"{folderB}\"", StringComparison.Ordinal));
        Assert.Equal(2, handler.Requests.Count(r => r.Body?.Contains("\"name\":\"RescanMovie\"", StringComparison.Ordinal) == true));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/manualimport", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("ManualImport", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flat_shared_directory_falls_back_to_copy_with_a_message()
    {
        const string sharedDir = "/data/media/Shared";
        const string fileA = sharedDir + "/a.mkv";
        const string fileB = sharedDir + "/b.mkv";
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(
            EntityKind.Studio, StudioCoveId,
            OwnedVideo(StashA, fileA),
            OwnedVideo(StashB, fileB));

        var handler = FakeHttpMessageHandler.Sequence(
            Ok(MovieList(Movie(42, StashA, hasFile: false), Movie(43, StashB, hasFile: false))), // ListMovies
            Ok(ManualImportListing(fileA)),                 // scene A: GET /manualimport -> lists the owned file
            Ok("{}"),                                       //          POST /command (ManualImport copy)
            Ok(MovieList(Movie(42, StashA, hasFile: true))), //         GET verify -> hasFile:true
            Ok(ManualImportListing(fileB)),                 // scene B: GET /manualimport
            Ok("{}"),                                       //          POST /command (ManualImport copy)
            Ok(MovieList(Movie(43, StashB, hasFile: true)))); //        GET verify -> hasFile:true

        var result = await ActionsFor(handler, library)
            .ReflectOwnedAsync(EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Total);
        Assert.Equal(2, result.Value.Succeeded);
        Assert.Equal(0, result.Value.Failed);

        // A shared parent directory forces the copy fall-back for both scenes and surfaces a message — never a
        // silent path re-point that would collide two movies on one directory.
        Assert.NotNull(result.Value.Message);
        Assert.Contains("flat", result.Value.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests.Count(r => r.Body?.Contains("\"name\":\"ManualImport\"", StringComparison.Ordinal) == true));
        Assert.All(
            handler.Requests.Where(r => r.Body?.Contains("\"name\":\"ManualImport\"", StringComparison.Ordinal) == true),
            r => Assert.Contains("\"importMode\":\"copy\"", r.Body!));
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("RescanMovie", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put && r.Url.Contains("/api/v3/movie/"));
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("MoviesSearch", StringComparison.Ordinal) == true);
    }
}
