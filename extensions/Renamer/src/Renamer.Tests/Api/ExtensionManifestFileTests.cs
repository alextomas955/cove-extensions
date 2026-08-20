using System.Reflection;
using System.Text.Json;
using Cove.Plugins;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests;

/// <summary>
/// Everything the shipped <c>extension.json</c> has to say for itself: that the host's real
/// <see cref="ExtensionManifestFile"/> contract binds it, that the identity the host registers under
/// comes from the file and is redeclared nowhere in code, and that its prose states the permission
/// reach the backend actually has.
/// </summary>
/// <remarks>
/// Two reading paths on purpose, and the difference is load-bearing. <see cref="Load"/> deserializes
/// the file directly with the host's own options, so a key the loader would reject fails here instead
/// of dropping silently at install time. <c>RenamerFixture.Manifest</c> is the seam every
/// extension-constructing test builds through, so asserting across it is what would catch a fixture
/// that started answering from a stub — and on the cove-absent CI leg this class is the only place
/// that is caught at all, since every route-level identity assertion is compiled out there.
/// </remarks>
[Trait("Tier", "L0")]
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

    // Hand-transcribed on purpose. Comparing against something derived from the manifest would make
    // the expectation agree with itself forever, including when the manifest is what broke.
    private const string ExpectedId = "com.alextomas955.renamer";
    private const string ExpectedName = "Renamer";

    /// <summary>
    /// The identity the host registers under, read through the seam every other test builds on.
    /// </summary>
    [Fact]
    public void ShippedManifest_DeclaresTheIdentityTheHostWillRegisterUnder()
    {
        Assert.Equal(ExpectedId, RenamerFixture.Manifest.Id);
        Assert.Equal(ExpectedName, RenamerFixture.Manifest.Name);
    }

    /// <summary>
    /// The extension redeclares none of its metadata in code, so the manifest is what the host reads.
    /// </summary>
    /// <remarks>
    /// The host reads each value straight off the property, so an override here silently WINS over the
    /// shipped manifest — which is how a dead repository URL and a truncated description once reached
    /// users, and how two copies of one id made a second instance of this extension unloadable. A
    /// regression is therefore not a wrong value but a REDECLARED one, which no value assertion can
    /// catch while the copy still happens to agree with the manifest.
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
        Assert.NotEqual(typeof(global::Renamer.Renamer), property.DeclaringType);
    }

    /// <summary>
    /// Every renamable kind's permission pair is named in the shipped description.
    /// </summary>
    /// <remarks>
    /// The manifest has no structured per-kind permission field, so the claim an operator reads before
    /// granting the extension access lives in the description prose. Prose has no compiler, which makes
    /// an understatement here silent: the endpoints go on accepting a kind the manifest never mentions,
    /// and permission review is the surface that pays for it.
    /// <para>
    /// The direction of this pin is what earns it. The expectation is owned by the code the manifest
    /// DESCRIBES — the backend's own renamable kinds, and the permission pair the backend itself returns
    /// for each — so a kind added to the backend fails this test until the manifest names its
    /// permissions. A test holding its own list of kind names would instead agree with itself forever
    /// and go stale in exactly the way the manifest already did.
    /// </para>
    /// <para>
    /// What the pin deliberately does NOT assert is the reach of the registered bulk actions or of the
    /// auto-rename hook. Both cover fewer kinds than the endpoints do, on purpose, so widening it to
    /// them would demand a manifest sentence that is false.
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

        return Assert.IsType<RenamerFileKind[]>(field.GetValue(null));
    }
}
