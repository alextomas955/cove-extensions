using System.Runtime.CompilerServices;

namespace WhisparrSync.Tests.Safety;

/// <summary>
/// The single-read-path guard for Whisparr's root folders: the role method may be NAMED only where the read is
/// defined — the transport, the version-role declaration and its one shared implementation, and the port that
/// owns the read policy. A hit anywhere else is a caller reading roots directly, which bypasses the cache, the
/// fail-closed empty-set rule and the failure classification the port owns on that path.
/// </summary>
/// <remarks>
/// Pure text scanning with no Cove reference, so this file stays OFF the csproj bare-CI Compile-Remove group and
/// the criterion is checked on the cove-absent leg too. The technique mirrors the coordinator source guard in
/// <c>NoMutationTests</c>, including its comment stripping — a doc comment naming the role method can therefore
/// neither satisfy nor invalidate the guard.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class RootReadSeamTests
{
    private const string RoleMethod = "ListRootFoldersAsync";

    // Top-level production directories the read may be named in: Client/ is the transport, Adapters/ declares the
    // role and implements it once on the shared base (both generations answer the same endpoint), Safety/ holds
    // the one port that owns the policy around it.
    private static readonly string[] ReadDefinitionDirectories = ["Safety", "Adapters", "Client"];

    [Fact]
    public void RootFolderRead_IsNamedOnlyWhereTheReadIsDefined()
    {
        var offenders = Hits().Where(h => !h.IsDefinitionSite).Select(h => h.RelativePath).ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{RoleMethod} is called outside {string.Join(", ", ReadDefinitionDirectories)}: "
            + $"{string.Join(", ", offenders)}. Route the read through Safety/WhisparrRootsPort instead.");
    }

    // Without this the guard would pass vacuously after a rename that removed the read everywhere — an empty
    // offender set proves nothing on its own.
    [Fact]
    public void TheGuardIsNotVacuous_TheReadIsStillNamedInsideItsDefinitionSites()
    {
        var definitionSites = Hits().Count(h => h.IsDefinitionSite);

        Assert.True(definitionSites > 0, $"{RoleMethod} is named nowhere in the production source.");
    }

    private static IEnumerable<(string RelativePath, bool IsDefinitionSite)> Hits()
    {
        var root = ProductionRoot();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            var segments = relative.Split(Path.DirectorySeparatorChar);

            // Generated build output is not live source.
            if (segments[0] is "obj" or "bin")
            {
                continue;
            }

            if (!CodeOf(file).Contains(RoleMethod, StringComparison.Ordinal))
            {
                continue;
            }

            yield return (relative, ReadDefinitionDirectories.Contains(segments[0], StringComparer.Ordinal));
        }
    }

    // Strip line + doc comments (`//` and `///`), the same rule the coordinator source guard uses, so only real
    // code lines are inspected.
    private static string CodeOf(string file)
        => string.Join(
            '\n',
            File.ReadAllLines(file).Select(line =>
            {
                var idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx >= 0 ? line[..idx] : line;
            }));

    // The production project sits beside this test project: ../../WhisparrSync/ (this test file lives one
    // concern-subfolder deep under the test project root).
    private static string ProductionRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "WhisparrSync"));
}
