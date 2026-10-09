using WhisparrSync.Monitoring;
using WhisparrSync.Options;

namespace WhisparrSync.Addressing;

// The output is one entry per Cove library root, each holding a reason and at most
// OutboundRootRefusal.PathsTriedKept paths. The folders under a root are never counted here.
// Entries are keyed through ImportRootRefusals.NormaliseRoot, so two spellings of one root that
// differ only by a trailing separator are one entry.
internal static class OutboundRefusalProjector
{
    // A root's entry is replaced, never added to: the reason and the paths are what the last run
    // established, and an accumulated entry would report a reason that no longer holds. The result
    // equals current where the fold changes nothing, which lets a caller skip the write.
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

    // A blank instanceRoot removes the mapping. One entry per root: a second save replaces the
    // first, because a root has one place on the instance.
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

    internal static string? MappingFor(
        IReadOnlyList<OutboundRootMapping> mappings, string coveRoot)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        var key = ImportRootRefusals.NormaliseRoot(coveRoot);
        return mappings.FirstOrDefault(entry => entry.CoveRoot == key)?.InstanceRoot;
    }
}
