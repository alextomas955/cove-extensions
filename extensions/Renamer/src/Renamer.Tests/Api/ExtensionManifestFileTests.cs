using System.Text.Json;
using Cove.Plugins;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests;

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
        // The host adds every enabled cssBundle to every page it serves, the host's own pages included.
        Assert.Null(manifest.CssBundle);
    }

    [Fact]
    public void Manifest_DeclaresNoNetworkScraperOrDownloaderPermissions()
    {
        var manifest = Load();

        // The extension touches files on disk and the DB only - it makes no network calls and runs no
        // scraper/downloader code, so all three runtime-permission buckets the host models are empty.
        Assert.NotNull(manifest.Permissions);
        Assert.Empty(manifest.Permissions.Network);
        Assert.Empty(manifest.Permissions.ScraperRuntime);
        Assert.Empty(manifest.Permissions.DownloaderRuntime);
    }

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
}
