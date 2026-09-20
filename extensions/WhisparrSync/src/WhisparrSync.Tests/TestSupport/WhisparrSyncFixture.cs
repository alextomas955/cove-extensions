using System.Text.Json;
using Cove.Plugins;

namespace WhisparrSync.Tests.TestSupport;

// Builds the extension the way the host does: instance first, then extension.json applied through
// IManifestAware. The extension declares no metadata in code, so an instance without an applied
// manifest has a null Id and mounts its routes under the wrong prefix. The manifest read is the
// shipped file next to the test assembly, so a file that stops parsing or loses its id fails the
// suite rather than only a live install.
internal static class WhisparrSyncFixture
{
    private const string ManifestFileName = "extension.json";

    // camelCase in the file, PascalCase on the CLR properties, as the host binds it.
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<ExtensionManifestFile> SharedManifest = new(LoadManifest);

    internal static ExtensionManifestFile Manifest => SharedManifest.Value;

    internal static global::WhisparrSync.WhisparrSync Create()
    {
        var extension = new global::WhisparrSync.WhisparrSync();
        ((IManifestAware)extension).ApplyManifest(Manifest);
        return extension;
    }

    private static ExtensionManifestFile LoadManifest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ManifestFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{ManifestFileName} is not next to the test assembly ({path}). It reaches the test "
                    + "output through the WhisparrSync project reference, so that copy has been dropped.");
        }

        var manifest = JsonSerializer.Deserialize<ExtensionManifestFile>(
            File.ReadAllText(path),
            ManifestJsonOptions)
            ?? throw new InvalidOperationException($"{path} deserialized to null.");

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new InvalidOperationException(
                $"{path} declares no id. Every identity assertion downstream would agree with it vacuously.");
        }

        return manifest;
    }
}
