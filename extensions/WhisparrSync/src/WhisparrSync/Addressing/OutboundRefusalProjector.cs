using WhisparrSync.Monitoring;
using WhisparrSync.Options;

namespace WhisparrSync.Addressing;

/// <summary>
/// Folds what one run established about the library roots, and one supplied mapping, into the
/// per-root aggregates the options blob carries.
/// </summary>
/// <remarks>
/// The output is one entry per Cove library root, each a reason and at most
/// <see cref="OutboundRootRefusal.PathsTriedKept"/> paths. An operator created the roots by hand and
/// there are few of them; the folders under them are never counted here.
/// <para>
/// Entries are keyed through <see cref="ImportRootRefusals.NormaliseRoot"/>, so two spellings of one
/// root that differ only by a trailing separator are one entry.
/// </para>
/// </remarks>
internal static class OutboundRefusalProjector
{
    /// <summary>
    /// <paramref name="current"/> with <paramref name="refused"/>'s roots replaced and
    /// <paramref name="addressed"/>'s roots cleared.
    /// </summary>
    /// <remarks>
    /// A root's entry is replaced rather than added to: the reason and the paths are what the last
    /// run established, and an entry that accumulated would report a reason that no longer holds.
    /// <para>
    /// Answers an aggregate equal to <paramref name="current"/> where the fold changes nothing, which
    /// is what lets a caller skip a write on a run that added no information.
    /// </para>
    /// </remarks>
    /// <param name="current">The aggregate as stored.</param>
    /// <param name="refused">One entry per root the run established nothing for.</param>
    /// <param name="addressed">The roots the run did address.</param>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    internal static List<OutboundRootRefusal> Fold(
        IReadOnlyList<OutboundRootRefusal> current,
        IReadOnlyList<FolderAddressRefusal>? refused,
        IReadOnlyList<string>? addressed)
    {
        ArgumentNullException.ThrowIfNull(current);

        var replacing = (refused ?? [])
            .Select(refusal => new OutboundRootRefusal
            {
                Root = refusal.CoveRoot,
                Refusal = refusal.Refusal,
                PathsTried = [.. refusal.Tried],
            })
            .ToList();

        var settled = new HashSet<string>(
            replacing
                .Select(entry => entry.Root)
                .Concat((addressed ?? []).Select(ImportRootRefusals.NormaliseRoot)),
            StringComparer.Ordinal);

        return [.. current.Where(entry => !settled.Contains(entry.Root)), .. replacing];
    }

    /// <summary>
    /// <paramref name="current"/> with <paramref name="coveRoot"/> mapped to
    /// <paramref name="instanceRoot"/>, or with its mapping removed where that is blank.
    /// </summary>
    /// <remarks>
    /// One entry per root. A second save for one root replaces the first, because a root has one
    /// place on the instance and holding two would reintroduce the ambiguity a mapping is supplied to
    /// remove.
    /// </remarks>
    /// <param name="current">The mappings as stored.</param>
    /// <param name="coveRoot">The Cove library root the mapping is for.</param>
    /// <param name="instanceRoot">Where the instance holds it, or blank to remove the mapping.</param>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    internal static List<OutboundRootMapping> WithMapping(
        IReadOnlyList<OutboundRootMapping> current, string coveRoot, string? instanceRoot)
    {
        ArgumentNullException.ThrowIfNull(current);

        var key = ImportRootRefusals.NormaliseRoot(coveRoot);
        var without = current.Where(entry => entry.CoveRoot != key);

        return string.IsNullOrWhiteSpace(instanceRoot)
            ? [.. without]
            : [.. without, new OutboundRootMapping { CoveRoot = key, InstanceRoot = instanceRoot }];
    }

    /// <summary>Where <paramref name="coveRoot"/> is mapped to, or null where it is not mapped.</summary>
    internal static string? MappingFor(
        IReadOnlyList<OutboundRootMapping> mappings, string coveRoot)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        var key = ImportRootRefusals.NormaliseRoot(coveRoot);
        return mappings.FirstOrDefault(entry => entry.CoveRoot == key)?.InstanceRoot;
    }
}
