using System.Text.Json;
using Cove.Plugins;

namespace Renamer.Tests;

/// <summary>
/// Guards the shipped <c>extension.json</c> against the host's real <see cref="ExtensionManifestFile"/>
/// contract: it deserializes the same file the host loads, using the same case-insensitive options the
/// host uses, so a field the loader would reject (or a renamerd/typo'd key) fails here instead of
/// silently dropping at install time. It also pins the runtime-permissions posture and the richer
/// admin-facing description added for v1.3.
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
}
