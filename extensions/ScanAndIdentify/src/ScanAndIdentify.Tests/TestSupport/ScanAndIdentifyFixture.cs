using System.Text.Json;
using Cove.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ScanAndIdentify.Tests.TestSupport;

// Builds the extension the way the host does: construct it, then apply the shipped extension.json.
// The extension declares no metadata in code, so without the manifest its Id is null and its routes
// register under the wrong prefix.
internal static class ScanAndIdentifyFixture
{
    private const string ManifestFileName = "extension.json";

    // The options the host binds the manifest with: camelCase file, PascalCase CLR properties.
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<ExtensionManifestFile> SharedManifest = new(LoadManifest);

    internal static ExtensionManifestFile Manifest => SharedManifest.Value;

    internal static global::ScanAndIdentify.ScanAndIdentify Create()
    {
        var extension = new global::ScanAndIdentify.ScanAndIdentify();
        ((IManifestAware)extension).ApplyManifest(Manifest);
        return extension;
    }

    // The store the host hands a stateful extension, which the options routes read and write through.
    internal static global::ScanAndIdentify.ScanAndIdentify Create(IExtensionStore store)
    {
        var extension = Create();
        ((IStatefulExtension)extension).SetStore(store);
        return extension;
    }

    // Runs the two host lifecycle hooks the capabilities view is built from, in the host's order.
    internal static async Task<global::ScanAndIdentify.ScanAndIdentify> CreateInitializedAsync(
        string coveVersion, IServiceProvider services)
    {
        var extension = Create();
        extension.ConfigureServices(new ServiceCollection(), Context(coveVersion));
        await extension.InitializeAsync(services);
        return extension;
    }

    internal static ExtensionContext Context(string coveVersion) => new()
    {
        Configuration = new ConfigurationBuilder().Build(),
        DataDirectory = AppContext.BaseDirectory,
        CoveVersion = coveVersion,
    };

    private static ExtensionManifestFile LoadManifest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ManifestFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{ManifestFileName} is not next to the test assembly ({path}). It reaches the test "
                    + "output through the ScanAndIdentify project reference, so that copy has been dropped.");
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
