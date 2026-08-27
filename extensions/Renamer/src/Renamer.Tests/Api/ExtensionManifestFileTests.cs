using System.Reflection;
using System.Text.Json;
using Cove.Plugins;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests;

/// <summary>
/// Guards the shipped <c>extension.json</c> against the host's real <see cref="ExtensionManifestFile"/>
/// contract: it deserializes the same file the host loads, using the same case-insensitive options the
/// host uses, so a field the loader would reject (or a renamed/typo'd key) fails here instead of
/// silently dropping at install time. It also pins the runtime-permissions posture, the richer
/// admin-facing description, and that the extension instance the host builds answers from this file.
/// </summary>
public sealed class ExtensionManifestFileTests
{
    // The manifest is copied next to the test assembly via the Renamer project reference's
    // CopyToOutputDirectory. Read it from there so the test exercises the actual shipped file.
    private static readonly string ManifestPath =
        Path.Combine(AppContext.BaseDirectory, "extension.json");

    // Mirror the host's own deserialization options (ExtensionManager reads the manifest with
    // PropertyNameCaseInsensitive = true). Deserializing with the same options proves the loader
    // will bind every key our manifest declares.
    private static readonly JsonSerializerOptions HostOptions = new() { PropertyNameCaseInsensitive = true };

    private static ExtensionManifestFile Load()
    {
        string json = File.ReadAllText(ManifestPath);
        return JsonSerializer.Deserialize<ExtensionManifestFile>(json, HostOptions)
            ?? throw new InvalidOperationException("extension.json deserialized to null");
    }

    [Fact]
    public void Manifest_DeserializesAgainstHostContract_WithCoreIdentity()
    {
        var manifest = Load();

        Assert.Equal("com.alextomas955.renamer", manifest.Id);
        Assert.Equal("Renamer", manifest.Name);
        // entryDll/jsBundle are the key-links the host uses to load the assembly and bundle.
        Assert.Equal("Renamer.dll", manifest.EntryDll);
        Assert.Equal("index.mjs", manifest.JsBundle);
    }

    [Fact]
    public void Manifest_DeclaresNoNetworkScraperOrDownloaderPermissions()
    {
        var manifest = Load();

        // The extension touches files on disk and the DB only — it makes no network calls and runs no
        // scraper/downloader code, so all three runtime-permission buckets the host models are empty.
        Assert.NotNull(manifest.Permissions);
        Assert.Empty(manifest.Permissions.Network);
        Assert.Empty(manifest.Permissions.ScraperRuntime);
        Assert.Empty(manifest.Permissions.DownloaderRuntime);
    }

    [Fact]
    public void Manifest_DescriptionStatesWhatItTouchesAndRequires()
    {
        var manifest = Load();

        // The host's permission schema has no filesystem/DB bucket, so the admin-facing description
        // is where the real surface is declared. Assert it actually says what it reads/writes and the
        // permissions it needs, so the description can't silently regress to the one-liner.
        Assert.NotNull(manifest.Description);
        string description = manifest.Description!;
        Assert.Contains("disk", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("database", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("videos.read", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("videos.write", description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The metadata an operator sees in Cove's extension list is the shipped manifest's, declared
    /// nowhere in code.
    /// </summary>
    /// <remarks>
    /// The host reads each of these straight off the property on the instance, so an override declared
    /// on the extension class wins over the file and the manifest stops being read at all.
    /// </remarks>
    [Fact]
    public void Extension_AnswersItsMetadataFromTheShippedManifest()
    {
        var manifest = Load();
        var extension = RenamerFixture.Create();

        Assert.Equal(manifest.Id, extension.Id);
        Assert.Equal(manifest.Name, extension.Name);
        Assert.Equal(manifest.Version, extension.Version);
        Assert.Equal(manifest.Description, extension.Description);
        Assert.Equal(manifest.Author, extension.Author);
        Assert.Equal(manifest.Url, extension.Url);
        Assert.Equal(manifest.MinCoveVersion, extension.MinCoveVersion);
        Assert.Equal(manifest.Categories, extension.Categories);
    }

    /// <summary>
    /// The extension redeclares none of its metadata in code, so the manifest is what the host reads.
    /// </summary>
    /// <remarks>
    /// The host reads each value straight off the property, so an override here silently WINS over the
    /// shipped manifest. The regression is therefore not a wrong value but a REDECLARED one, which no
    /// value assertion can catch while the copy still happens to agree with the manifest.
    /// </remarks>
    [Theory]
    [InlineData("Id")]
    [InlineData("Name")]
    [InlineData("Version")]
    [InlineData("MinCoveVersion")]
    [InlineData("Description")]
    [InlineData("Author")]
    [InlineData("Url")]
    [InlineData("IconUrl")]
    [InlineData("Categories")]
    public void Metadata_IsNotRedeclaredInCode(string member)
    {
        var property = typeof(global::Renamer.Renamer).GetProperty(member);

        Assert.NotNull(property);
        Assert.NotEqual(typeof(global::Renamer.Renamer), property!.DeclaringType);
    }

    /// <summary>
    /// Every renamable kind's permission pair is named in the shipped description.
    /// </summary>
    /// <remarks>
    /// The manifest has no structured per-kind permission field, so the claim an operator reads before
    /// granting access lives in the description prose. Prose has no compiler, which makes an
    /// understatement here silent: the endpoints go on accepting a kind the manifest never mentions.
    /// <para>
    /// The expectation is owned by the code the manifest DESCRIBES - the backend's own renamable kinds,
    /// and the permission pair the backend itself returns for each - so a kind added to the backend
    /// fails this test until the manifest names its permissions.
    /// </para>
    /// <para>
    /// It deliberately does NOT assert the reach of the registered bulk actions or of the auto-rename
    /// hook. Both cover fewer kinds than the endpoints do, on purpose, so widening it to them would
    /// demand a manifest sentence that is false.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryRenamableKindsPermissionPairIsNamedInTheShippedManifestDescription()
    {
        // The shipped bytes, through the one seam that reads them, rather than a second path literal.
        var description = RenamerFixture.Manifest.Description;
        Assert.False(
            string.IsNullOrWhiteSpace(description),
            "extension.json declares no description. The per-kind permission claim lives there and "
                + "nowhere else, so an empty one states nothing to an operator granting access.");

        var kinds = RenamableKinds();
        Assert.NotEmpty(kinds);

        foreach (var kind in kinds)
        {
            var (read, write) = global::Renamer.Renamer.PermissionsFor(kind);

            Assert.Contains(read, description, StringComparison.Ordinal);
            Assert.Contains(write, description, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Reads the backend's renamable-kind set out of the field that defines it. Reflection because the
    /// field is private and this pin is not a reason to widen its visibility; the null check is what
    /// keeps a rename of the field a loud failure instead of a pin that inspects nothing.
    /// </summary>
    private static RenamerFileKind[] RenamableKinds()
    {
        var field = typeof(global::Renamer.Renamer).GetField(
            "RenamableKinds",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);

        return Assert.IsType<RenamerFileKind[]>(field!.GetValue(null));
    }
}
