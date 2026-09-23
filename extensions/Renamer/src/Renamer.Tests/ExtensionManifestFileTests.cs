using Renamer.Tests.TestSupport;

namespace Renamer.Tests;

public sealed class ExtensionManifestFileTests
{
    [Fact]
    public void Manifest_DeserializesAgainstHostContract_WithCoreIdentity()
    {
        var manifest = RenamerFixture.Manifest;

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
        var manifest = RenamerFixture.Manifest;

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
        var manifest = RenamerFixture.Manifest;
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
