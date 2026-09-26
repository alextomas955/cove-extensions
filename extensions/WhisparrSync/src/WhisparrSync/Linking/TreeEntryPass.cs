using WhisparrSync.Whisparr;

namespace WhisparrSync.Linking;

/// <summary>What one pass over an entity's scenes could and could not address.</summary>
/// <remarks>
/// <c>Unnumbered</c> is a scene the metadata source named no number for, and <c>Unresolved</c> is
/// one the site's catalogue holds no row for. They are separate because the first is this product
/// failing to identify a scene and the second is the instance not holding it, and a reader acts on
/// them differently.
/// </remarks>
internal readonly record struct TreeEntryTally(int Addressed, int Unnumbered, int Unresolved)
{
    internal static TreeEntryTally Nothing { get; }

    internal TreeEntryTally Plus(TreeEntryTally other)
        => new(
            Addressed + other.Addressed,
            Unnumbered + other.Unnumbered,
            Unresolved + other.Unresolved);
}

// Which generation and which instance each delegate acts against is bound where the pass is
// composed; nothing here reads a generation. OwnedScenes and LinkNamesOf stream, because an
// entity's catalogue and a scene's files are as large as the reader's library under it.
internal sealed record TreeEntryPorts(
    Func<CancellationToken, IAsyncEnumerable<string>> OwnedScenes,
    Func<string, CancellationToken, Task<int?>> NumberFor,
    Func<IReadOnlyCollection<int>, CancellationToken, Task<IReadOnlyDictionary<int, int>>> RowsFor,
    Func<string, CancellationToken, IAsyncEnumerable<string>> LinkNamesOf);

/// <summary>
/// Pairs the names in one entity's folder with the instance rows their files belong to.
/// </summary>
/// <remarks>
/// A link is named after the identity of the file it points at, so a name carries nothing an
/// instance can parse a scene out of. This is what supplies that answer instead, for a generation
/// that holds a scene as a row under a site.
/// <para>
/// Nothing here grows with the library. The scenes walked are the entity's own, the numbers held at
/// once are one chunk, and each scene's names are paired and dropped as it is reached.
/// </para>
/// </remarks>
internal static class TreeEntryPass
{
    // A bound on what is held at once, not a ceiling on how many scenes the pass reaches. The
    // instance narrows its own answer by no parameter, so it answers the site's whole list however
    // few numbers were asked about: a smaller chunk costs more of those whole-list reads.
    internal const int ChunkSize = 500;

    internal static async Task<(IReadOnlyDictionary<string, EntryAddress> ByName, TreeEntryTally Tally)>
        AddressedAsync(TreeEntryPorts ports, int siteRow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentOutOfRangeException.ThrowIfLessThan(siteRow, 1);

        var byName = new Dictionary<string, EntryAddress>(StringComparer.Ordinal);
        var tally = TreeEntryTally.Nothing;

        // One chunk's scenes, each with the names its files carry in the folder. The rows arrive
        // for the whole chunk at once, so a scene's names wait for its number to be answered.
        var chunk = new Dictionary<int, List<string>>();

        await foreach (var identity in ports.OwnedScenes(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (await ports.NumberFor(identity, ct).ConfigureAwait(false) is not { } number)
            {
                tally = tally with { Unnumbered = tally.Unnumbered + 1 };
                continue;
            }

            var names = new List<string>();
            await foreach (var name in ports.LinkNamesOf(identity, ct)
                .WithCancellation(ct)
                .ConfigureAwait(false))
            {
                names.Add(name);
            }

            // Two identifiers for one scene are two spellings of one source, so their names join
            // rather than replace: dropping the first would leave its files unaddressed.
            if (chunk.TryGetValue(number, out var held))
            {
                held.AddRange(names);
            }
            else
            {
                chunk[number] = names;
            }

            if (chunk.Count < ChunkSize)
            {
                continue;
            }

            tally = tally.Plus(await PairAsync().ConfigureAwait(false));
            chunk.Clear();
        }

        if (chunk.Count > 0)
        {
            tally = tally.Plus(await PairAsync().ConfigureAwait(false));
        }

        return (byName, tally);

        async Task<TreeEntryTally> PairAsync()
        {
            var rows = await ports.RowsFor(chunk.Keys, ct).ConfigureAwait(false);
            var paired = TreeEntryTally.Nothing;

            foreach (var (number, names) in chunk)
            {
                ct.ThrowIfCancellationRequested();

                if (!rows.TryGetValue(number, out var row))
                {
                    paired = paired with { Unresolved = paired.Unresolved + 1 };
                    continue;
                }

                foreach (var name in names)
                {
                    byName[name] = new EntryAddress(row, siteRow);
                }

                paired = paired with { Addressed = paired.Addressed + 1 };
            }

            return paired;
        }
    }
}
