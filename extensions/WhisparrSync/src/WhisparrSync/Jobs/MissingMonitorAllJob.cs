using System.Globalization;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Jobs;

/// <summary>Which catalogue one whole-entity marking run is about, as the host's map held it.</summary>
/// <remarks>
/// The narrowing rather than the scenes. The run re-derives its own set from these, so the map stays
/// one entity and two short strings however large the catalogue behind them is.
/// </remarks>
/// <param name="Kind">The entity kind, or null where the map named none this product expresses.</param>
/// <param name="CoveId">The Cove id, or zero where the map carried none that could be read.</param>
/// <param name="TitleSearch">The title search in force, or null for none.</param>
/// <param name="Filters">The facet selections in force, in the form the address carries them.</param>
public sealed record MissingMonitorAllBatch(
    WhisparrEntityKind? Kind, int CoveId, string? TitleSearch, string? Filters);

/// <summary>
/// The whole-catalogue marking job's id, its (de)serialization onto the host's string-only parameter
/// map, and the page walk one run goes through.
/// </summary>
/// <remarks>
/// The run carries a query rather than a list of scenes, so nothing it holds grows with the
/// catalogue: each page is derived, marked and dropped before the next is read. That is also what
/// makes the set it acts on the set the reader was looking at, the derivation being the one the grid
/// itself reads through.
/// <para>
/// Every body this run composes is the scene add the card's own verb composes. Nothing here reaches
/// a search of any kind.
/// </para>
/// </remarks>
public static class MissingMonitorAllJob
{
    /// <summary>The job id this extension's own type prefix is minted onto.</summary>
    public const string JobId = "missing-monitor-all";

    private const string KindKey = "kind";
    private const string CoveIdKey = "coveId";
    private const string TitleSearchKey = "titleSearch";
    private const string FiltersKey = "filters";

    /// <summary>Encodes one whole-catalogue run onto the host's parameter map.</summary>
    public static Dictionary<string, string> Encode(
        WhisparrEntityKind kind, int coveId, string? titleSearch, string? filters)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KindKey] = kind.ToString(),
            [CoveIdKey] = coveId.ToString(CultureInfo.InvariantCulture),
        };

        // Written only where there is one, so a narrowing nobody asked for cannot be read back out of
        // a key holding an empty string.
        if (!string.IsNullOrWhiteSpace(titleSearch))
        {
            parameters[TitleSearchKey] = titleSearch;
        }

        if (!string.IsNullOrWhiteSpace(filters))
        {
            parameters[FiltersKey] = filters;
        }

        return parameters;
    }

    /// <summary>Reads one whole-catalogue run back off the host's parameter map.</summary>
    /// <remarks>
    /// Never throws, for the reason the selection run's decode does not: it is read inside the host's
    /// job runner, where a throw is a faulted job rather than a handled answer. A map naming no kind
    /// answers none rather than the first one declared, because a run that defaulted to a kind would
    /// mark scenes under an entity nobody named.
    /// </remarks>
    public static MissingMonitorAllBatch Decode(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null)
        {
            return new MissingMonitorAllBatch(null, 0, null, null);
        }

        var kind = Enum.TryParse<WhisparrEntityKind>(
            Read(parameters, KindKey), ignoreCase: true, out var named) && Enum.IsDefined(named)
                ? named
                : (WhisparrEntityKind?)null;

        var coveId = int.TryParse(
            Read(parameters, CoveIdKey), CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : 0;

        return new MissingMonitorAllBatch(
            kind, coveId, Read(parameters, TitleSearchKey), Read(parameters, FiltersKey));
    }

    /// <summary>
    /// Walks the narrowed catalogue a page at a time and offers each scene on it to the instance,
    /// inside ONE scope elevated to System.
    /// </summary>
    /// <remarks>
    /// The run carries no principal of its own, and Cove's per-principal query filters answer an
    /// anonymous reader with zero rows and no error.
    /// <para>
    /// A page that could not be derived ends the walk with what was marked so far. Continuing past it
    /// would skip a page of the catalogue in silence, which reads as a completed run over a set that
    /// was never whole.
    /// </para>
    /// <para>
    /// A cancellation classifies the run as cancelled and what was marked before it stays marked: the
    /// scenes are in the instance's catalogue and there is nothing to undo.
    /// </para>
    /// </remarks>
    /// <param name="batch">Which catalogue the run is about.</param>
    /// <param name="scopes">The scope factory the extension was handed at initialization.</param>
    /// <param name="aiming">
    /// What the run offers each scene through, over the run's own elevated services, or null where it
    /// must not act.
    /// </param>
    /// <param name="readPage">
    /// The derivation one page is read through, answering null where the page could not be derived.
    /// </param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
    internal static Task<MissingBulkRun> RunAsync(
        MissingMonitorAllBatch batch,
        IServiceScopeFactory scopes,
        Func<IServiceProvider, CancellationToken,
            Task<Func<string, CancellationToken, Task<WhisparrResponse?>>?>> aiming,
        Func<IServiceProvider, MissingMonitorAllBatch, int, CancellationToken, Task<MissingPageView?>>
            readPage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(aiming);
        ArgumentNullException.ThrowIfNull(readPage);

        return RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
        {
            if (batch.Kind is null
                || await aiming(services, ct).ConfigureAwait(false) is not { } mark)
            {
                return Untaken;
            }

            return await MarkAsync(
                page => readPage(services, batch, page, ct), mark, ct).ConfigureAwait(false);
        });
    }

    /// <summary>Offers every scene the walk reaches, keeping only counts.</summary>
    /// <remarks>
    /// Repeats are dropped within a page and not across the walk. A set spanning the walk would grow
    /// with the catalogue, and offering one scene twice costs one add the instance answers as already
    /// held rather than a second registration.
    /// </remarks>
    private static async Task<MissingBulkRun> MarkAsync(
        Func<int, Task<MissingPageView?>> readPage,
        Func<string, CancellationToken, Task<WhisparrResponse?>> mark,
        CancellationToken ct)
    {
        var marked = 0;
        var alreadyHeld = 0;
        var refused = 0;
        var offered = 0;
        var page = 1;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (await readPage(page).ConfigureAwait(false) is not { } view)
                {
                    break;
                }

                foreach (var providerSceneId in Distinct(view.Cards))
                {
                    ct.ThrowIfCancellationRequested();
                    offered++;

                    switch (AddAllMissingPlanner.Classify(
                        await mark(providerSceneId, ct).ConfigureAwait(false)))
                    {
                        case SceneRegistration.Registered:
                            marked++;
                            break;
                        case SceneRegistration.AlreadyHeld:
                            alreadyHeld++;
                            break;
                        default:
                            refused++;
                            break;
                    }
                }

                if (page >= view.LastPage)
                {
                    break;
                }

                page++;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new MissingBulkRun(
                MissingBulkRunOutcome.Cancelled, marked, alreadyHeld, refused);
        }

        return offered == 0
            ? Untaken
            : new MissingBulkRun(MissingBulkRunOutcome.Completed, marked, alreadyHeld, refused);
    }

    /// <summary>A run that offered nothing, because it was never aimed at a scene.</summary>
    private static MissingBulkRun Untaken { get; } =
        new(MissingBulkRunOutcome.NothingToMark, 0, 0, 0);

    /// <summary>One page's identifiers with repeats removed, keeping first appearance.</summary>
    private static List<string> Distinct(IReadOnlyList<MissingCard> cards)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>(cards.Count);
        foreach (var card in cards)
        {
            if (seen.Add(card.ProviderSceneId))
            {
                kept.Add(card.ProviderSceneId);
            }
        }

        return kept;
    }

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
