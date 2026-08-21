using System.Text.Json;
using Cove.Plugins;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Constructs the extension the way the host does: instance first, then <c>extension.json</c> applied
/// through <see cref="IManifestAware"/>. Every test that needs an extension instance MUST go through
/// here rather than calling the constructor directly.
/// </summary>
/// <remarks>
/// Reading the SHIPPED manifest rather than a hand-built stub is deliberate. The extension declares no
/// metadata in code, so a stub would let the tests agree with themselves while the real file drifted —
/// and a manifest that fails to parse, or that lost its id, now fails the suite here instead of only at
/// install time on a live host. The file is copied next to the test assembly by this project's
/// <c>Content</c> item.
/// </remarks>
internal static class RenamerFixture
{
    private const string ManifestFileName = "extension.json";

    // Matches how the host binds the manifest: the file is camelCase (entryDll, jsBundle,
    // minCoveVersion) while the CLR properties are PascalCase.
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<ExtensionManifestFile> SharedManifest = new(LoadManifest);

    /// <summary>The parsed, shipped manifest — the same bytes the host would read.</summary>
    internal static ExtensionManifestFile Manifest => SharedManifest.Value;

    /// <summary>A ready-to-use extension instance with the shipped manifest already applied.</summary>
    internal static global::Renamer.Renamer Create()
    {
        var extension = new global::Renamer.Renamer();
        ((IManifestAware)extension).ApplyManifest(Manifest);
        return extension;
    }

    private static ExtensionManifestFile LoadManifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ManifestFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{ManifestFileName} is not next to the test assembly ({path}). The Content item in "
                    + "Renamer.Tests.csproj that copies the shipped manifest has been removed or renamed.");
        }

        var manifest = JsonSerializer.Deserialize<ExtensionManifestFile>(
            File.ReadAllText(path),
            ManifestJsonOptions)
            ?? throw new InvalidOperationException($"{path} deserialized to null.");

        // An empty id would make every identity assertion below vacuously agree with a broken manifest.
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new InvalidOperationException($"{path} declares no id.");
        }

        return manifest;
    }
}
