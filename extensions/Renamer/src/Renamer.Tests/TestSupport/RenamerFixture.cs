using System.Text.Json;
using Cove.Plugins;

namespace Renamer.Tests.TestSupport;

// Constructs the extension the way the host does: instance first, then extension.json applied
// through IManifestAware. Every test that needs an extension instance has to build it through here.
// The extension declares no metadata in code, so an instance without an applied manifest has a null
// Id and registers its routes under the wrong prefix. The manifest read is the shipped file next to
// the test assembly, not a stub, so a file that stops parsing or loses its id fails the suite
// rather than only a live install.
internal static class RenamerFixture
{
    private const string ManifestFileName = "extension.json";

    // The options the host binds the manifest with: camelCase file, PascalCase CLR properties.
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<ExtensionManifestFile> SharedManifest = new(LoadManifest);

    // The parsed, shipped manifest - the same bytes the host would read.
    internal static ExtensionManifestFile Manifest => SharedManifest.Value;

    // A ready-to-use extension instance with the shipped manifest already applied.
    internal static global::Renamer.Renamer Create()
    {
        var extension = new global::Renamer.Renamer();
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
