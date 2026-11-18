using ScanAndIdentify.Tests.TestSupport;

namespace ScanAndIdentify.Tests;

public sealed class ExtensionManifestFileTests
{
    [Fact]
    public void Manifest_DeserializesAgainstHostContract_WithCoreIdentity()
    {
        var manifest = ScanAndIdentifyFixture.Manifest;

        Assert.Equal("com.alextomas955.scanandidentify", manifest.Id);
        Assert.Equal("ScanAndIdentify", manifest.Name);
        // entryDll/jsBundle are the key-links the host uses to load the assembly and bundle.
        Assert.Equal(
            typeof(global::ScanAndIdentify.ScanAndIdentify).Assembly.GetName().Name + ".dll", manifest.EntryDll);
        Assert.Equal("index.mjs", manifest.JsBundle);
        // The host adds every enabled cssBundle to every page it serves, the host's own pages included.
        Assert.Null(manifest.CssBundle);
    }

    [Fact]
    public void Extension_AnswersItsMetadataFromTheShippedManifest()
    {
        var manifest = ScanAndIdentifyFixture.Manifest;
        var extension = ScanAndIdentifyFixture.Create();

        Assert.Equal(manifest.Id, extension.Id);
        Assert.Equal(manifest.Name, extension.Name);
        Assert.Equal(manifest.Version, extension.Version);
        Assert.Equal(manifest.MinCoveVersion, extension.MinCoveVersion);
        Assert.Equal(manifest.Categories, extension.Categories);
    }
}
