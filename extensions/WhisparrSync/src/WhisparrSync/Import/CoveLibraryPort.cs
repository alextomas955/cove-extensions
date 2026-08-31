using Cove.Core.Interfaces;

namespace WhisparrSync.Import;

/// <inheritdoc cref="ICoveLibraryPort"/>
/// <remarks>
/// The scan service is optional. An extension container builds its own copy of the host's scoped
/// registration, and a container that cannot produce one must still load the extension and report
/// the fact rather than fail every request behind an unresolved dependency.
/// </remarks>
internal sealed class CoveLibraryPort(IScanService? scan, CoveConfiguration? config) : ICoveLibraryPort
{
    public IReadOnlyList<string> LibraryRoots => ReadLibraryRoots(config);

    public async Task<LibraryImport> ImportVideoAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (scan is null)
        {
            return new LibraryImport(false, null);
        }

        return new LibraryImport(true, await scan.ImportDownloadedVideoAsync(path, null, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The one reading of the host's configured library paths this extension works from: blank
    /// entries dropped, absent configuration an empty list.
    /// </summary>
    /// <remarks>
    /// Static because the port is not the only caller: the pure candidate arithmetic is tested
    /// against the same normalisation with no container and no configuration object to hand, and a
    /// second projection there could disagree about a blank entry.
    /// </remarks>
    internal static IReadOnlyList<string> ReadLibraryRoots(CoveConfiguration? config)
        => config is null
            ? []
            : [.. config.CovePaths
                .Select(path => path.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))];
}
