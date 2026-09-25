using System.Globalization;
using System.Text.Json;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Jobs;

/// <summary>Which scenes one enqueued selection is about, and what to do with them.</summary>
/// <remarks>
/// A null kind, a zero id or an empty list means the parameter map carried none. A verb it carried
/// none of reads as monitoring, which is the gesture every run enqueued before the verb existed.
/// </remarks>
public sealed record MissingBulkBatch(
    WhisparrEntityKind? Kind,
    int CoveId,
    IReadOnlyList<string> ProviderSceneIds,
    MissingBulkVerb Verb = MissingBulkVerb.Monitor);

internal enum MissingBulkRunOutcome
{
    Completed,

    NothingToMark,

    // A stop part way. What was marked before it stays marked.
    Cancelled,
}

// Counts only: a member listing the identifiers would grow with the selection.
internal sealed record MissingBulkRun(
    MissingBulkRunOutcome Outcome, int Marked, int AlreadyHeld, int Refused);

/// <summary>
/// The bulk marking job's id, its (de)serialization onto the host's string-only parameter map, and
/// the scene loop one selection's run goes through.
/// </summary>
/// <remarks>
/// The host hands a job its parameters as <c>IReadOnlyDictionary&lt;string,string&gt;?</c>, so the
/// identifier list crosses as JSON under one key.
/// </remarks>
public static class MissingBulkJob
{
    public const string JobId = "missing-bulk";

    private const string KindKey = "kind";
    private const string CoveIdKey = "coveId";
    private const string SceneIdsKey = "providerSceneIds";
    private const string VerbKey = "verb";

    public static Dictionary<string, string> Encode(
        WhisparrEntityKind kind,
        int coveId,
        IReadOnlyList<string> providerSceneIds,
        MissingBulkVerb verb)
        => new(StringComparer.Ordinal)
        {
            [KindKey] = kind.ToString(),
            [CoveIdKey] = coveId.ToString(CultureInfo.InvariantCulture),
            [SceneIdsKey] = JsonSerializer.Serialize(providerSceneIds),
            [VerbKey] = verb.ToString(),
        };

    /// <summary>Reads one selection's run back off the host's parameter map.</summary>
    /// <remarks>
    /// Never throws; it runs inside the host's job runner, where a throw faults the job. An
    /// unreadable kind answers null rather than the first kind declared, so a run never marks scenes
    /// under an entity nobody named. Unreadable JSON answers no identifiers.
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

        // A map carrying no verb, or one it cannot read, is a run enqueued to monitor: that is
        // what every run meant before the verb existed, and it is the gesture that adds rather than
        // retracts.
        var verb = Enum.TryParse<MissingBulkVerb>(
            Read(parameters, VerbKey), ignoreCase: true, out var named2) && Enum.IsDefined(named2)
                ? named2
                : MissingBulkVerb.Monitor;

        return new MissingBulkBatch(
            kind, coveId, SceneIdsIn(Read(parameters, SceneIdsKey)), verb);
    }

    // Runs as System: the job carries no principal, and Cove's per-principal filters answer an
    // anonymous reader with zero rows and no error.
    // A stop classifies as cancelled, not failed: what was already marked is in the instance's
    // catalogue and there is nothing to undo.
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

    // Counts, never a list of scenes: the sentence must not grow with the selection.
    /// <summary>The one line the run reports, naming the verb it carried out.</summary>
    internal static string SummaryOf(MissingBulkRun run, MissingBulkVerb verb)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Outcome == MissingBulkRunOutcome.NothingToMark)
        {
            return "Nothing was marked, because the selection reached no scene this Whisparr can hold.";
        }

        var ending = run.Outcome == MissingBulkRunOutcome.Cancelled ? ", then stopped" : string.Empty;
        var did = verb == MissingBulkVerb.Monitor ? "monitored" : "unmonitored";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{run.Marked} {did}, {run.AlreadyHeld} already {did}, {run.Refused} refused{ending}.");
    }

    // Repeats are dropped before any request: a page's selection can carry one identifier twice,
    // and offering it twice issues two adds.
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

    private static MissingBulkRun Untaken { get; } =
        new(MissingBulkRunOutcome.NothingToMark, 0, 0, 0);

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
