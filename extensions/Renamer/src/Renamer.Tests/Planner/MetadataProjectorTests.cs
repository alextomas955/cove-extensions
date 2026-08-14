using Renamer.Engine;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// Proves the <see cref="MetadataProjector"/> entity-graph → (tokens, multiValues) projection
/// and the entity-type-aware token degradation: absent media tokens are OMITTED so the engine's
/// <c>{}</c> groups collapse cleanly.
/// </summary>
[Trait("Tier", "L0")]
public sealed class MetadataProjectorTests
{
    private static RenamerFile VideoFileRow() => new(
        FileId: 1, Kind: RenamerFileKind.Video, Basename: "raw.mkv", ParentFolderId: 5,
        ParentFolderPath: "media/videos", Format: "mkv",
        Width: 1920, Height: 1080, Duration: 3600, VideoCodec: "h264", AudioCodec: "aac", FrameRate: 30);

    // 1h 23m 45s — the reference duration the settings UI's Duration-format example column and
    // SampleTokenSets both quote, held here as the seconds the DTO actually carries.
    private const double ReferenceDurationSeconds = 5025;

    private static RenamerFile DurationFileRow(double seconds) => new(
        FileId: 7, Kind: RenamerFileKind.Video, Basename: "clip.mkv", ParentFolderId: 9,
        ParentFolderPath: "media/videos", Format: "mkv", Duration: seconds);

    private static RenamerEntity VideoEntity(RenamerFile file) => new(
        EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: "ABC-1",
        StudioName: "Acme", Date: new DateOnly(2024, 3, 2), Organized: true,
        Performers: [new RenamerPerformer(1, "Bob", false, null), new RenamerPerformer(2, "Ann", false, null)],
        TagRefs: [(7, "hd"), (9, "fav")], Files: [file]);

    [Fact]
    public void Video_Projects_AllScalarTokens_And_MultiValues()
    {
        var file = VideoFileRow();
        var (tokens, multi, _, _) = MetadataProjector.Project(VideoEntity(file), file, new RenamerOptions());

        Assert.Equal("My Film", tokens[Tokens.Title]);
        Assert.Equal("ABC-1", tokens[Tokens.StudioCode]);
        Assert.Equal("Acme", tokens[Tokens.Studio]);
        Assert.Equal("1080", tokens[Tokens.Height]);
        Assert.Equal("1920", tokens[Tokens.Width]);
        Assert.Equal("h264", tokens[Tokens.VideoCodec]);
        Assert.Equal("aac", tokens[Tokens.AudioCodec]);
        Assert.Equal("2024-03-02", tokens[Tokens.Date]);   // default DateFormat yyyy-MM-dd
        Assert.Equal("2024", tokens[Tokens.Year]);
        // Projector emits the RAW ext token ("mkv"); the engine adds the leading dot at Render
        // time (NormalizeExt) — see the end-to-end test asserting result.Ext == ".mkv".
        Assert.Equal("mkv", tokens[Tokens.Ext]);

        Assert.Equal(new[] { "Bob", "Ann" }, multi[Tokens.Performers]);
        Assert.Equal(new[] { "hd", "fav" }, multi[Tokens.Tags]);
    }

    [Fact]
    public void Audio_Omits_Resolution_VideoCodec_FrameRate_Width_Height()
    {
        var file = new RenamerFile(
            FileId: 2, Kind: RenamerFileKind.Audio, Basename: "song.mp3", ParentFolderId: 6,
            ParentFolderPath: "media/audio", Format: "mp3",
            Duration: 200, AudioCodec: "mp3");
        var entity = new RenamerEntity(
            EntityId: 20, Kind: RenamerFileKind.Audio, Title: "Track", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [], Files: [file]);

        var (tokens, _, _, _) = MetadataProjector.Project(entity, file, new RenamerOptions());

        Assert.Equal("Track", tokens[Tokens.Title]);
        Assert.Equal("mp3", tokens[Tokens.AudioCodec]);
        // Absent — NOT empty-string — so the engine's {} collapse drops them.
        Assert.False(tokens.ContainsKey(Tokens.Resolution));
        Assert.False(tokens.ContainsKey(Tokens.VideoCodec));
        Assert.False(tokens.ContainsKey(Tokens.FrameRate));
        Assert.False(tokens.ContainsKey(Tokens.Width));
        Assert.False(tokens.ContainsKey(Tokens.Height));
    }

    [Fact]
    public void Image_Omits_Video_And_Audio_Only_Tokens()
    {
        var file = new RenamerFile(
            FileId: 3, Kind: RenamerFileKind.Image, Basename: "pic.jpg", ParentFolderId: 7,
            ParentFolderPath: "media/images", Format: "jpg",
            Width: 800, Height: 600);
        var entity = new RenamerEntity(
            EntityId: 30, Kind: RenamerFileKind.Image, Title: "Shot", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [], Files: [file]);

        var (tokens, _, _, _) = MetadataProjector.Project(entity, file, new RenamerOptions());

        Assert.Equal("800", tokens[Tokens.Width]);
        Assert.Equal("600", tokens[Tokens.Height]);
        Assert.False(tokens.ContainsKey(Tokens.VideoCodec));
        Assert.False(tokens.ContainsKey(Tokens.AudioCodec));
        Assert.False(tokens.ContainsKey(Tokens.FrameRate));
        Assert.False(tokens.ContainsKey(Tokens.Duration));
    }

    [Fact]
    public void EmptyScalars_AreOmitted_NotEmptyString()
    {
        // Audio with no Title/Code/Studio/Date — those scalar tokens must be absent.
        var file = new RenamerFile(
            FileId: 4, Kind: RenamerFileKind.Audio, Basename: "x.mp3", ParentFolderId: 8,
            ParentFolderPath: "a", Format: "mp3", Duration: 1, AudioCodec: "mp3");
        var entity = new RenamerEntity(
            EntityId: 40, Kind: RenamerFileKind.Audio, Title: null, Code: "", StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [], Files: [file]);

        // Fallback forced off so a null title stays omitted: this case proves empty scalars are
        // absent (not empty string), distinct from the basename fallback which now defaults on.
        var (tokens, _, _, _) = MetadataProjector.Project(entity, file, new RenamerOptions { FilenameAsTitle = false });

        Assert.False(tokens.ContainsKey(Tokens.Title));
        Assert.False(tokens.ContainsKey(Tokens.StudioCode));
        Assert.False(tokens.ContainsKey(Tokens.Studio));
        Assert.False(tokens.ContainsKey(Tokens.Date));
        Assert.False(tokens.ContainsKey(Tokens.Year));
    }

    [Fact]
    public void Duration_DefaultFormat_RendersTheAdvertisedHhMmSsShape()
    {
        var file = DurationFileRow(ReferenceDurationSeconds);
        var (tokens, _, _, _) = MetadataProjector.Project(VideoEntity(file), file, new RenamerOptions());

        // The shape the settings dropdown's example column and SampleTokenSets both advertise for this
        // duration. Before DurationFormat was honored this token was the raw seconds ("5025"), so the
        // preview sample and the real projection disagreed.
        Assert.Equal("01-23-45", tokens[Tokens.Duration]);
    }

    // One case per option the settings UI offers in its Duration-format dropdown, asserting the exact
    // rendering that UI's example column advertises. The server owns these strings, so the pin belongs
    // here: a UI test would compute the example from the same table it displays and agree with itself.
    [Theory]
    [InlineData(@"hh\-mm\-ss", "01-23-45")]
    [InlineData(@"hh\.mm\.ss", "01.23.45")]
    [InlineData(@"mm\-ss", "23-45")]
    public void Duration_HonorsConfiguredFormat(string format, string expected)
    {
        var file = DurationFileRow(ReferenceDurationSeconds);
        var (tokens, _, _, _) = MetadataProjector.Project(
            VideoEntity(file), file, new RenamerOptions { DurationFormat = format });

        Assert.Equal(expected, tokens[Tokens.Duration]);
    }

    [Fact]
    public void Duration_InvalidFormat_DegradesToRawSeconds_InsteadOfThrowing()
    {
        // DurationFormat is free text a user can type, and this projection runs for EVERY file in a
        // plan — so a throw here would abort a whole batch over one bad setting rather than spoiling one
        // token. "Q" is not a valid TimeSpan format specifier.
        var file = DurationFileRow(ReferenceDurationSeconds);
        var (tokens, _, _, _) = MetadataProjector.Project(
            VideoEntity(file), file, new RenamerOptions { DurationFormat = "Q" });

        Assert.Equal("5025", tokens[Tokens.Duration]);
    }

    [Fact]
    public void Ext_PrefersOnDiskExtension_OverContainerFormatName()
    {
        // Cove's Format field is the container NAME, not the extension: an .mkv file reports
        // Format "matroska". The extension token must be the real on-disk extension ("mkv"), NOT
        // "matroska" — otherwise the rename rewrites movie.mkv → movie.matroska (a non-standard
        // extension that breaks player/OS association). Regression guard for that bug.
        var file = new RenamerFile(
            FileId: 5, Kind: RenamerFileKind.Video, Basename: "movie.mkv", ParentFolderId: 9,
            ParentFolderPath: "media/videos", Format: "matroska", Height: 1080);
        var entity = new RenamerEntity(
            EntityId: 50, Kind: RenamerFileKind.Video, Title: "Movie", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [], Files: [file]);

        var (tokens, multi, _, _) = MetadataProjector.Project(entity, file, new RenamerOptions());
        Assert.Equal("mkv", tokens[Tokens.Ext]);

        // End-to-end: the rendered extension stays .mkv, not .matroska.
        var result = TemplateEngine.Render(
            tokens, multi, new RenamerOptions { FilenameTemplate = "$title" });
        Assert.Equal(".mkv", result.Ext);
    }

    [Fact]
    public void Ext_FallsBackToFormat_WhenBasenameHasNoExtension()
    {
        // A rare extensionless file: with no on-disk extension to read, fall back to Format.
        var file = new RenamerFile(
            FileId: 6, Kind: RenamerFileKind.Video, Basename: "movie", ParentFolderId: 9,
            ParentFolderPath: "media/videos", Format: "mkv", Height: 1080);
        var entity = new RenamerEntity(
            EntityId: 60, Kind: RenamerFileKind.Video, Title: "Movie", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [], Files: [file]);

        var (tokens, _, _, _) = MetadataProjector.Project(entity, file, new RenamerOptions());
        Assert.Equal("mkv", tokens[Tokens.Ext]);
    }

    [Fact]
    public void ProjectorOutput_FedThroughRender_ProducesExpectedName_Video()
    {
        var file = VideoFileRow();
        var (tokens, multi, _, _) = MetadataProjector.Project(VideoEntity(file), file, new RenamerOptions());

        var options = new RenamerOptions { FilenameTemplate = "$studio - $title [$resolution]" };
        var result = TemplateEngine.Render(tokens, multi, options);

        // $resolution is derived by the engine from $height=1080 → "1080p".
        Assert.Equal("Acme - My Film [1080p]", result.Filename);
        Assert.Equal(".mkv", result.Ext);
    }

    [Fact]
    public void Video_Projects_ParentStudio_Director_Bitrate_WhenPresent()
    {
        // $bitrate is stored bits/sec on the file; $director + $parent_studio are entity-level.
        var file = VideoFileRow() with { BitRate = 8_000_000 };  // 8 Mbps stored → 8000 kbps rendered
        var entity = VideoEntity(file) with
        {
            Director = "Jane Roe",
            ParentStudios = [(Id: 7, Name: "Acme Parent"), (Id: 3, Name: "Acme Grandparent")],
        };

        var (tokens, _, _, _) = MetadataProjector.Project(entity, file, new RenamerOptions());

        Assert.Equal("Acme Parent", tokens[Tokens.ParentStudio]);  // NEAREST parent (nearest-first)
        Assert.Equal("Jane Roe", tokens[Tokens.Director]);
        Assert.Equal("8000", tokens[Tokens.Bitrate]);              // bits/sec → kbps
    }

    [Fact]
    public void NewTokens_AreOmitted_WhenAbsent()
    {
        // A video with no parent studio, no director, no bitrate → all three tokens absent
        // (omit-not-blank, so a `{}` group collapses cleanly).
        var file = VideoFileRow();                 // BitRate defaults null
        var entity = VideoEntity(file);            // Director + ParentStudios default null

        var (tokens, _, _, _) = MetadataProjector.Project(entity, file, new RenamerOptions());

        Assert.False(tokens.ContainsKey(Tokens.ParentStudio));
        Assert.False(tokens.ContainsKey(Tokens.Director));
        Assert.False(tokens.ContainsKey(Tokens.Bitrate));
    }

    [Fact]
    public void Rating_IsDeferred_NeverEmitted_NoPrincipalSource()
    {
        // TOKEN-02 ($rating) is DEFERRED (host-fact gate): Cove's Rating is per-UserId/per-Aspect,
        // and the renamer batch runs as a detached job with NO principal, so "the item's rating" is
        // undefined. Per the locked never-ship-garbage decision, $rating is NOT projected and there
        // is NO Tokens.Rating constant. This negative assertion documents + guards the deferral.
        var file = VideoFileRow();
        var (tokens, _, _, _) = MetadataProjector.Project(VideoEntity(file), file, new RenamerOptions());

        // Not emitted under any spelling the engine would resolve.
        Assert.False(tokens.ContainsKey("rating"));
        Assert.False(tokens.ContainsKey("$rating"));
        // And there is no canonical Tokens.Rating constant to project (compile-time guard:
        // if someone adds one, they must revisit this deferral). Confirm via the public Tokens fields.
        var hasRatingConst = typeof(Tokens)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Any(f => string.Equals(f.Name, "Rating", System.StringComparison.Ordinal));
        Assert.False(hasRatingConst, "$rating is deferred (no-principal); do not add Tokens.Rating without revisiting the source decision.");
    }

    // ---- filename-as-title fallback ----

    [Fact]
    public void Title_FallbackOff_OmitsTitleWhenNone()
    {
        var file = VideoFileRow();
        var entity = VideoEntity(file) with { Title = null };

        // Explicitly off: this case proves the strict omit-not-blank behavior, distinct from the
        // basename fallback (covered by Title_FallbackOn_*). The fallback now defaults on, so the
        // off behavior is pinned here by setting the flag rather than relying on the default.
        var (tokens, _, _, _) = MetadataProjector.Project(entity, file, new RenamerOptions { FilenameAsTitle = false });

        Assert.False(tokens.ContainsKey(Tokens.Title)); // omit-not-blank when the fallback is off
    }

    [Fact]
    public void Title_FallbackOn_DoesNotOverridePresentTitle()
    {
        var file = VideoFileRow();
        var entity = VideoEntity(file); // Title = "My Film"

        var (tokens, _, _, _) = MetadataProjector.Project(entity, file, new RenamerOptions { FilenameAsTitle = true });

        Assert.Equal("My Film", tokens[Tokens.Title]); // a present title wins over the basename
    }

    [Theory]
    [InlineData("My Clip.mkv", "My Clip")]
    [InlineData("README", "README")]            // dotless basename keeps its whole name
    [InlineData(".gitignore", ".gitignore")]    // leading-dot/no-stem: ResolveExt treats it as no extension
    public void Title_FallbackOn_UsesBasenameWithoutExtension_WhenTitleEmpty(string basename, string expected)
    {
        var file = VideoFileRow() with { Basename = basename };
        var entity = VideoEntity(file) with { Title = null };

        var (tokens, _, _, _) = MetadataProjector.Project(entity, file, new RenamerOptions { FilenameAsTitle = true });

        Assert.Equal(expected, tokens[Tokens.Title]);
    }

    [Fact]
    public void Title_FilenameDerived_SatisfiesRequiredFieldsGate()
    {
        // A title-less item with the fallback on resolves a non-empty `title` through the SAME map
        // the RequiredFields=["title"] gate reads, so the item is renamed rather than skipped.
        var file = VideoFileRow() with { Basename = "Some Recording.mkv" };
        var entity = VideoEntity(file) with { Title = null };
        var options = new RenamerOptions { FilenameAsTitle = true };

        var (tokens, multi, _, _) = MetadataProjector.Project(entity, file, options);
        var resolved = TemplateEngine.ResolveField(tokens, multi, options, Tokens.Title);

        Assert.Equal("Some Recording", resolved);
        Assert.False(string.IsNullOrEmpty(resolved)); // non-empty => not gated out
    }

    [Fact]
    public void Title_FilenameDerived_IsStableAcrossReRender()
    {
        // The fallback derives from the CURRENT source basename and never re-applies the template's
        // own decorations, so feeding a just-rendered name back as the basename yields the same title.
        var firstFile = VideoFileRow() with { Basename = "My Clip.mkv" };
        var entity = VideoEntity(firstFile) with { Title = null };
        var options = new RenamerOptions { FilenameTemplate = "$title", FilenameAsTitle = true };

        var (tokens1, multi1, _, _) = MetadataProjector.Project(entity, firstFile, options);
        var firstTitle = tokens1[Tokens.Title];
        var rendered = TemplateEngine.Render(tokens1, multi1, options);

        var secondFile = firstFile with { Basename = rendered.Filename + rendered.Ext };
        var (tokens2, _, _, _) = MetadataProjector.Project(entity, secondFile, options);

        Assert.Equal(firstTitle, tokens2[Tokens.Title]); // no progressive drift across a re-render
    }

    [Fact]
    public void ProjectorOutput_FedThroughRender_DegradesCleanly_Audio()
    {
        var file = new RenamerFile(
            FileId: 5, Kind: RenamerFileKind.Audio, Basename: "song.mp3", ParentFolderId: 9,
            ParentFolderPath: "a", Format: "mp3", Duration: 200, AudioCodec: "mp3");
        var entity = new RenamerEntity(
            EntityId: 50, Kind: RenamerFileKind.Audio, Title: "Track", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [], Files: [file]);
        var (tokens, multi, _, _) = MetadataProjector.Project(entity, file, new RenamerOptions());

        // resolution + videoCodec groups have no value → engine drops the {} spans entirely.
        var options = new RenamerOptions { FilenameTemplate = "$title {[$resolution]} {$videoCodec}" };
        var result = TemplateEngine.Render(tokens, multi, options);

        Assert.Equal("Track", result.Filename);
    }

    [Fact]
    public void TagRefs_ReachTheEngine_AndFilterTheTagsTokenByIdWhileRenderingNames()
    {
        var file = VideoFileRow();
        var entity = VideoEntity(file) with { TagRefs = [(7, "hd"), (9, "fav")] };
        var options = new RenamerOptions
        {
            FilenameTemplate = "$tags",
            Tags = new MultiValueOptions { Separator = " ", Sort = SortOrder.None, WhitelistIds = [9] },
        };

        var (tokens, multi, performers, tagRefs) = MetadataProjector.Project(entity, file, options);

        Assert.Equal(new[] { (7, "hd"), (9, "fav") }, tagRefs);

        var filtered = TemplateEngine.Render(tokens, multi, options, performers: performers, tagRecords: tagRefs);

        // The whitelisted id survives and renders as its NAME, never as "9".
        Assert.Equal("fav", filtered.Filename);

        // Contrast: with the channel unwired the engine falls back to the unfiltered name path, so
        // this assertion is what distinguishes "the records were passed" from "they were not".
        var unwired = TemplateEngine.Render(tokens, multi, options, performers: performers);
        Assert.Equal("hd fav", unwired.Filename);
    }
}
