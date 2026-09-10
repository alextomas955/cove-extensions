using System.Globalization;
using System.Text.Json;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Jobs;

/// <summary>Which scenes one enqueued selection is about, as the host's map held it.</summary>
/// <param name="Kind">The entity kind, or null where the map named none this product expresses.</param>
/// <param name="CoveId">The Cove id, or zero where the map carried none that could be read.</param>
/// <param name="ProviderSceneIds">The scenes ticked, or empty where the map carried none.</param>
public sealed record MissingBulkBatch(
    WhisparrEntityKind? Kind, int CoveId, IReadOnlyList<string> ProviderSceneIds);

/// <summary>How a run over one page's ticked scenes ended.</summary>
internal enum MissingBulkRunOutcome
{
    /// <summary>Every ticked scene was offered.</summary>
    Completed,

    /// <summary>The run reached no scene to offer at all.</summary>
    NothingToMark,

    /// <summary>The run was stopped part way. What it marked before that stays marked.</summary>
    Cancelled,
}

/// <summary>What a run over one page's ticked scenes did.</summary>
/// <remarks>
/// Counts and nothing else. A member listing the identifiers would grow with the selection, and the
/// one line a reader sees is a sentence rather than a list.
/// </remarks>
/// <param name="Outcome">How the run ended.</param>
/// <param name="MarkedWanted">How many the instance's catalogue did not already hold.</param>
/// <param name="AlreadyHeld">How many it already held, which is not a failure.</param>
/// <param name="Refused">How many it would not take.</param>
internal sealed record MissingBulkRun(
    MissingBulkRunOutcome Outcome, int MarkedWanted, int AlreadyHeld, int Refused);

/// <summary>
/// The bulk marking job's id, its (de)serialization onto the host's string-only parameter map, and
/// the scene loop one selection's run goes through.
/// </summary>
/// <remarks>
/// The host hands a job its parameters as <c>IReadOnlyDictionary&lt;string,string&gt;?</c>, so the
/// identifier list crosses as JSON under one key. <see cref="Decode"/> is total: it is read inside
/// the host's job runner, where a throw is a faulted job rather than a handled answer, so a run
/// nobody can read is a clean no-op.
/// <para>
/// Every body this run composes is the scene add the card's own verb composes. Nothing here reaches
/// a search of any kind.
/// </para>
/// </remarks>
public static class MissingBulkJob
{
    /// <summary>The job id this extension's own type prefix is minted onto.</summary>
    public const string JobId = "missing-bulk";

    private const string KindKey = "kind";
    private const string CoveIdKey = "coveId";
    private const string SceneIdsKey = "providerSceneIds";

    /// <summary>Encodes one selection's run onto the host's parameter map.</summary>
    public static Dictionary<string, string> Encode(
        WhisparrEntityKind kind, int coveId, IReadOnlyList<string> providerSceneIds)
        => new(StringComparer.Ordinal)
        {
            [KindKey] = kind.ToString(),
            [CoveIdKey] = coveId.ToString(CultureInfo.InvariantCulture),
            [SceneIdsKey] = JsonSerializer.Serialize(providerSceneIds),
        };

    /// <summary>Reads one selection's run back off the host's parameter map.</summary>
    /// <remarks>
    /// Never throws. A null map, a missing key, a blank value and a kind this product does not
    /// express all answer no kind rather than the first one declared, and a run that defaulted to a
    /// kind would mark scenes under an entity nobody named. Unparseable JSON and JSON that is not a
    /// string array both answer no identifiers.
    /// </remarks>
    public static MissingBulkBatch Decode(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null)
        {
            return new MissingBulkBatch(null, 0, []);
        }

        var kind = Enum.TryParse<WhisparrEntityKind>(
            Read(parameters, KindKey), ignoreCase: true, out var named) && Enum.IsDefined(named)
                ? named
                : (WhisparrEntityKind?)null;

        var coveId = int.TryParse(
            Read(parameters, CoveIdKey), CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : 0;

        return new MissingBulkBatch(kind, coveId, SceneIdsIn(Read(parameters, SceneIdsKey)));
    }

    /// <summary>
    /// Offers each ticked scene to the instance through <paramref name="aiming"/>, inside ONE scope
    /// elevated to System.
    /// </summary>
    /// <remarks>
    /// The run carries no principal of its own, and Cove's per-principal query filters answer an
    /// anonymous reader with zero rows and no error.
    /// <para>
    /// A run that cannot be aimed, or that names no entity, reports as a run with nothing to mark. It
    /// has not been refused by the instance and there is nothing for a reader to retry; what it could
    /// not read is already a line in the host's log.
    /// </para>
    /// <para>
    /// A cancellation classifies the run as cancelled and what was marked before it stays marked: the
    /// scenes are in the instance's catalogue and there is nothing to undo.
    /// </para>
    /// </remarks>
    /// <param name="batch">Which scenes the run is about.</param>
    /// <param name="scopes">The scope factory the extension was handed at initialization.</param>
    /// <param name="aiming">
    /// What the run offers each scene through, over the run's own elevated services, or null where it
    /// must not act.
    /// </param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
    internal static Task<MissingBulkRun> RunAsync(
        MissingBulkBatch batch,
        IServiceScopeFactory scopes,
        Func<IServiceProvider, CancellationToken,
            Task<Func<string, CancellationToken, Task<WhisparrResponse?>>?>> aiming,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(aiming);

        return RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
        {
            if (batch.Kind is null
                || batch.ProviderSceneIds.Count == 0
                || await aiming(services, ct).ConfigureAwait(false) is not { } mark)
            {
                return Untaken;
            }

            return await MarkAsync(batch.ProviderSceneIds, mark, ct).ConfigureAwait(false);
        });
    }

    /// <summary>The one line the host's job list shows for <paramref name="run"/>.</summary>
    /// <remarks>
    /// Counts rather than a list of scenes. An already-held scene is stated apart from a refused one,
    /// because they are different facts: one is the instance already holding the scene and the other
    /// is the instance declining.
    /// </remarks>
    internal static string SummaryOf(MissingBulkRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Outcome == MissingBulkRunOutcome.NothingToMark)
        {
            return "Nothing was marked, because the selection reached no scene this Whisparr can hold.";
        }

        var ending = run.Outcome == MissingBulkRunOutcome.Cancelled ? ", then stopped" : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{run.MarkedWanted} marked wanted, {run.AlreadyHeld} already marked, {run.Refused} refused{ending}.");
    }

    /// <summary>Offers each of <paramref name="providerSceneIds"/> once, keeping only counts.</summary>
    /// <remarks>
    /// Repeats are dropped before any request. A page's selection can carry one identifier twice, and
    /// offering it twice issues two adds for it.
    /// </remarks>
    private static async Task<MissingBulkRun> MarkAsync(
        IReadOnlyList<string> providerSceneIds,
        Func<string, CancellationToken, Task<WhisparrResponse?>> mark,
        CancellationToken ct)
    {
        var marked = 0;
        var alreadyHeld = 0;
        var refused = 0;
        var offered = 0;

        try
        {
            ct.ThrowIfCancellationRequested();
            foreach (var providerSceneId in Distinct(providerSceneIds))
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

    /// <summary>The identifiers with repeats removed, keeping first appearance.</summary>
    private static List<string> Distinct(IReadOnlyList<string> providerSceneIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>(providerSceneIds.Count);
        foreach (var providerSceneId in providerSceneIds)
        {
            if (seen.Add(providerSceneId))
            {
                kept.Add(providerSceneId);
            }
        }

        return kept;
    }

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string[] SceneIdsIn(string? raw)
    {
        if (raw is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
