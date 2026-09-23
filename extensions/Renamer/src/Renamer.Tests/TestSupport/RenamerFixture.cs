using System.Text.Json;
using Cove.Plugins;

namespace Renamer.Tests.TestSupport;

// Builds the extension the way the host does: construct it, then apply the shipped extension.json.
// The extension declares no metadata in code, so without the manifest its Id is null and its routes
// register under the wrong prefix.
internal static class RenamerFixture
{
    private const string ManifestFileName = "extension.json";

    // The options the host binds the manifest with: camelCase file, PascalCase CLR properties.
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<ExtensionManifestFile> SharedManifest = new(LoadManifest);

    internal static ExtensionManifestFile Manifest => SharedManifest.Value;

    internal static global::Renamer.Renamer Create()
    {
        var extension = new global::Renamer.Renamer();
        ((IManifestAware)extension).ApplyManifest(Manifest);
        return extension;
    }

    internal static global::Renamer.Renamer CreateWithStore()
    {
        var extension = Create();
        ((IStatefulExtension)extension).SetStore(new FakeStore());
        return extension;
    }

    private static ExtensionManifestFile LoadManifest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ManifestFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{ManifestFileName} is not next to the test assembly ({path}). It reaches the test "
                    + "output through the Renamer project reference, so that copy has been dropped.");
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
