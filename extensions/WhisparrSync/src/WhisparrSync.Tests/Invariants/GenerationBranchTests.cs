using System.Text.RegularExpressions;

namespace WhisparrSync.Tests.Invariants;

// Which generation is connected is a fact the connection layer establishes and the per-generation
// instances and readers carry. Everywhere else works on normalized models, so a generation named
// outside the places below is application code that has taken on wire knowledge again.
public sealed partial class GenerationBranchTests
{
    // Where naming a generation is the point. The connection layer detects one and stores it, the
    // options and settings surfaces carry the stored value, and the instance folder is where each
    // generation's own spellings live.
    private static readonly string[] Exempt =
    [
        "Connection/",
        "Options/",
        "Whisparr/",
        "WhisparrSync.Manifest.cs",
        "WhisparrSync.Settings.cs",
    ];

    // Which metadata source serves which generation. This is a fact about the provider rather than
    // about how a Whisparr instance spells a payload, so it belongs to neither instance: filing it
    // under one would put provider configuration under a type that has nothing to do with it. Each
    // catalogue also names the endpoint slot that is its own.
    private static readonly string[] Allowed =
    [
        "Identity/IdentityEndpoint.cs",
        "Providers/ProviderCatalogueChoice.cs",
        "Providers/StashDbCatalogue.cs",
        "Providers/ThePornDbCatalogue.cs",
    ];

    // Exact both ways. A file on the list that stops naming a generation fails as loudly as one off
    // the list that starts to: a list that only capped the count would let a new branch hide behind
    // a removed one.
    [Fact]
    public void TheOnlyApplicationCodeNamingAGenerationIsTheMetadataProviderCluster()
    {
        var sources = Sources();

        // A scan that reached no source would report nothing wrong for the same reason it reported
        // nothing at all.
        Assert.NotEmpty(sources);

        Assert.Equal(
            Allowed.Order(StringComparer.Ordinal).ToList(),
            sources
                .Where(source => !Exempt.Any(exempt =>
                    source.Path.StartsWith(exempt, StringComparison.Ordinal)
                    || string.Equals(source.Path, exempt, StringComparison.Ordinal)))
                .Where(source => NamesAGeneration(source.Text))
                .Select(source => source.Path)
                .Order(StringComparer.Ordinal)
                .ToList());
    }

    // Comment lines are dropped first. A comment naming a generation is prose about the code, and
    // this product's own rules require those: neither generation may be called older or newer, so
    // the comments saying which is which carry the fact a name cannot.
    private static bool NamesAGeneration(string text)
        => text
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Any(line => GenerationMember().IsMatch(line));

    [GeneratedRegex(@"WhisparrGeneration\.(V2|V3)")]
    private static partial Regex GenerationMember();

    private static IReadOnlyList<(string Path, string Text)> Sources()
    {
        var root = Path.Combine(ExtensionRoot(), "src", "WhisparrSync");
        return
        [
            .. Directory
                .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(file => !IsBuildOutput(file))
                .Select(file => (
                    Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                    File.ReadAllText(file))),
        ];
    }

    private static bool IsBuildOutput(string file)
        => file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    // Found by a committed file rather than by a counted-out "..": the test assembly's depth below
    // the extension directory varies with configuration and target framework.
    private static string ExtensionRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "wire", "openapi.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No wire/openapi.json above {AppContext.BaseDirectory}, so the shipped source cannot "
                + "be found.");
    }
}
