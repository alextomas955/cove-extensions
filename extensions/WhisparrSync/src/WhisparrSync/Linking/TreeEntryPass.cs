using System.Runtime.CompilerServices;
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

/// <summary>One chunk of an entity's names, paired with the rows their files belong to.</summary>
/// <remarks>
/// The map is the chunk's own. The caller acts on it and lets it go, and the tally is that chunk's
/// share of the pass's own figures.
/// </remarks>
internal readonly record struct TreeEntryBatch(
    IReadOnlyDictionary<string, EntryAddress> ByName, TreeEntryTally Tally);

/// <summary>
/// Pairs the names in one entity's folder with the instance rows their files belong to, a chunk at
/// a time.
/// </summary>
/// <remarks>
/// A link is named after the identity of the file it points at, so a name carries nothing an
/// instance can parse a scene out of. This is what supplies that answer instead, for a generation
/// that holds a scene as a row under a site.
/// <para>
/// An entity reaches the size of the library, so the pairs are handed over a chunk at a time and
/// nothing is remembered across chunks. A name reached under two identifier spellings is carried in
/// each chunk that reaches it.
/// </para>
/// </remarks>
internal static class TreeEntryPass
{
    // A bound on what is held at once, not a ceiling on how many scenes the pass reaches. The
    // instance narrows its own answer by no parameter, so it answers the site's whole list however
    // few numbers were asked about: a smaller chunk costs more of those whole-list reads.
    internal const int ChunkSize = 500;

    // A chunk whose every scene went unnumbered still carries that fact to the caller.
    private static readonly IReadOnlyDictionary<string, EntryAddress> NoNames =
        new Dictionary<string, EntryAddress>(StringComparer.Ordinal);

    internal static IAsyncEnumerable<TreeEntryBatch> AddressedAsync(
        TreeEntryPorts ports, int siteRow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentOutOfRangeException.ThrowIfLessThan(siteRow, 1);

        return AddressingAsync(ports, siteRow, ct);
    }

    private static async IAsyncEnumerable<TreeEntryBatch> AddressingAsync(
        TreeEntryPorts ports, int siteRow, [EnumeratorCancellation] CancellationToken ct)
    {
        // One chunk's scenes, each with the names its files carry in the folder. The rows arrive
        // for the whole chunk at once, so a scene's names wait for its number to be answered.
        var chunk = new Dictionary<int, List<string>>();
        var unnumbered = 0;

        await foreach (var identity in ports.OwnedScenes(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (await ports.NumberFor(identity, ct).ConfigureAwait(false) is not { } number)
            {
                unnumbered++;
                continue;
            }

            // Two identifiers for one scene are two spellings of one source, so their names join
            // rather than replace: dropping the first would leave its files unaddressed.
            if (!chunk.TryGetValue(number, out var names))
            {
                names = [];
                chunk[number] = names;
            }

            await foreach (var name in ports.LinkNamesOf(identity, ct)
                .WithCancellation(ct)
                .ConfigureAwait(false))
            {
                names.Add(name);
            }

            if (chunk.Count < ChunkSize)
            {
                continue;
            }

            yield return await PairedAsync(ports, chunk, siteRow, unnumbered, ct)
                .ConfigureAwait(false);
            chunk.Clear();
            unnumbered = 0;
        }

        if (chunk.Count > 0 || unnumbered > 0)
        {
            yield return await PairedAsync(ports, chunk, siteRow, unnumbered, ct)
                .ConfigureAwait(false);
        }
    }

    // One chunk's numbers reduced to the rows the instance holds for them, and every name under a
    // resolved number addressed to its row.
    private static async Task<TreeEntryBatch> PairedAsync(
        TreeEntryPorts ports,
        Dictionary<int, List<string>> chunk,
        int siteRow,
        int scenesWithNoNumber,
        CancellationToken ct)
    {
        var tally = TreeEntryTally.Nothing with { Unnumbered = scenesWithNoNumber };
        if (chunk.Count == 0)
        {
            return new TreeEntryBatch(NoNames, tally);
        }

        var rows = await ports.RowsFor(chunk.Keys, ct).ConfigureAwait(false);
        var byName = new Dictionary<string, EntryAddress>(StringComparer.Ordinal);

        foreach (var (number, names) in chunk)
        {
            ct.ThrowIfCancellationRequested();

            if (!rows.TryGetValue(number, out var row))
            {
                tally = tally with { Unresolved = tally.Unresolved + 1 };
                continue;
            }

            foreach (var name in names)
            {
                byName[name] = new EntryAddress(row, siteRow);
            }

            tally = tally with { Addressed = tally.Addressed + 1 };
        }

        return new TreeEntryBatch(byName, tally);
    }
}
