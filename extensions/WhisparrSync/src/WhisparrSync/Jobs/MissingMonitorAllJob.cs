using System.Globalization;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Jobs;

/// <summary>Which catalogue one whole-entity marking run is about.</summary>
/// <remarks>
/// Carries the narrowing, never the scenes, so the parameter map does not grow with the catalogue.
/// A null kind or a zero id means the map carried none that could be read.
/// </remarks>
public sealed record MissingMonitorAllBatch(
    WhisparrEntityKind? Kind, int CoveId, string? TitleSearch, string? Filters);

/// <summary>
/// The whole-catalogue marking job's id, its (de)serialization onto the host's string-only parameter
/// map, and the page walk one run goes through.
/// </summary>
/// <remarks>
/// The run carries a query, not a list of scenes, so nothing it holds grows with the catalogue: each
/// page is derived, marked and dropped before the next is read.
/// </remarks>
public static class MissingMonitorAllJob
{
    public const string JobId = "missing-monitor-all";

    private const string KindKey = "kind";
    private const string CoveIdKey = "coveId";
    private const string TitleSearchKey = "titleSearch";
    private const string FiltersKey = "filters";

    public static Dictionary<string, string> Encode(
        WhisparrEntityKind kind, int coveId, string? titleSearch, string? filters)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KindKey] = kind.ToString(),
            [CoveIdKey] = coveId.ToString(CultureInfo.InvariantCulture),
        };

        // Written only where there is one, so an empty key cannot read back as a narrowing nobody
        // asked for.
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
    /// Never throws; it runs inside the host's job runner, where a throw faults the job. A map naming
    /// no kind answers null rather than the first kind declared, so a run never marks scenes under an
    /// entity nobody named.
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

    // Runs as System: the job carries no principal, and Cove's per-principal filters answer an
    // anonymous reader with zero rows and no error.
    // A page that could not be derived ends the walk. Continuing past it would skip a page in
    // silence and report a completed run over a set that was never whole.
    // A stop classifies as cancelled, not failed: what was already marked is in the instance's
    // catalogue and there is nothing to undo.
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

    // Repeats are dropped within a page, not across the walk: a set spanning the walk would grow
    // with the catalogue. Offering one scene twice costs one add answered as already held.
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

    private static MissingBulkRun Untaken { get; } =
        new(MissingBulkRunOutcome.NothingToMark, 0, 0, 0);

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
