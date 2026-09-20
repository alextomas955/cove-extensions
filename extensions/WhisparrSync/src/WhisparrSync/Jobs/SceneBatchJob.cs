using System.Globalization;
using System.Text.Json;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;

namespace WhisparrSync.Jobs;

/// <summary>What one enqueued selection of scenes was asked to do.</summary>
/// <remarks>A null verb or an empty id array means the parameter map carried none.</remarks>
public sealed record SceneBatchBatch(SceneBatchVerb? Verb, int[] CoveIds);

internal enum SceneBatchRunOutcome
{
    Completed,

    NothingToDo,

    // A stop part way. What was applied before it stands.
    Cancelled,
}

// Counts only: a member listing the identifiers would grow with the selection.
internal sealed record SceneBatchRun(
    SceneBatchRunOutcome Outcome, int Applied, int AlreadyInThatState, int Refused);

/// <summary>
/// The scene batch job's id, its (de)serialization onto the host's string-only parameter map, and
/// the scene loop one selection's run goes through.
/// </summary>
/// <remarks>
/// The host hands a job its parameters as <c>IReadOnlyDictionary&lt;string,string&gt;?</c>, so the id
/// array crosses as JSON under one key.
/// </remarks>
public static class SceneBatchJob
{
    public const string JobId = "scene-batch";

    private const string VerbKey = "verb";
    private const string CoveIdsKey = "coveIds";

    public static Dictionary<string, string> Encode(
        SceneBatchVerb verb, IReadOnlyList<int> coveIds)
        => new(StringComparer.Ordinal)
        {
            [VerbKey] = verb.ToString(),
            [CoveIdsKey] = JsonSerializer.Serialize(coveIds),
        };

    /// <summary>Reads one selection's run back off the host's parameter map.</summary>
    /// <remarks>
    /// Never throws; it runs inside the host's job runner, where a throw faults the job. Unreadable
    /// JSON answers no ids, and an unreadable verb answers null rather than the first verb declared,
    /// so a run never acts on a map nobody could read.
    /// </remarks>
    public static SceneBatchBatch Decode(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null)
        {
            return new SceneBatchBatch(null, []);
        }

        var verb = Enum.TryParse<SceneBatchVerb>(
            Read(parameters, VerbKey), ignoreCase: true, out var named) && Enum.IsDefined(named)
                ? named
                : (SceneBatchVerb?)null;

        return new SceneBatchBatch(verb, IdsIn(Read(parameters, CoveIdsKey)));
    }

    // Deduplicated before any per-scene work: a selection can carry one video twice, and acting
    // twice issues two requests.
    // Runs as System: the job carries no principal, and Cove's per-principal filters answer an
    // anonymous reader with zero rows and no error, reporting every scene as carrying no identity.
    // A stop classifies as cancelled, not failed. The host stops a job by cancelling its token, so a
    // failure here would report a shutdown as a fault.
    internal static async Task<SceneBatchRun> RunAsync(
        IReadOnlyList<int> coveIds,
        IServiceScopeFactory scopes,
        Func<IServiceProvider, int, CancellationToken, Task<SceneRefusalKind>> act,
        IJobProgress progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(coveIds);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(act);
        ArgumentNullException.ThrowIfNull(progress);

        var selected = Distinct(coveIds);
        if (selected.Count == 0)
        {
            return Untaken;
        }

        var applied = 0;
        var alreadyInThatState = 0;
        var refused = 0;

        return await RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
        {
            try
            {
                foreach (var coveId in selected)
                {
                    ct.ThrowIfCancellationRequested();

                    using var unit = progress.StartUnit(UnitOf(coveId));
                    var refusal = await act(services, coveId, ct).ConfigureAwait(false);

                    switch (TallyFor(refusal))
                    {
                        case SceneBatchTally.Applied:
                            applied++;
                            break;
                        case SceneBatchTally.AlreadyInThatState:
                            alreadyInThatState++;
                            break;
                        default:
                            refused++;
                            break;
                    }

                    unit.Complete(
                        UnitOutcomeFor(refusal),
                        refusal == SceneRefusalKind.None ? null : refusal.ToString());
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new SceneBatchRun(
                    SceneBatchRunOutcome.Cancelled, applied, alreadyInThatState, refused);
            }

            return new SceneBatchRun(
                SceneBatchRunOutcome.Completed, applied, alreadyInThatState, refused);
        }).ConfigureAwait(false);
    }

    // Counts, never a list: a sentence naming every scene would grow with the selection. The
    // per-scene answers are the run's own units.
    internal static string SummaryOf(SceneBatchRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Outcome == SceneBatchRunOutcome.NothingToDo)
        {
            return "Nothing was selected, so nothing was done.";
        }

        var ending = run.Outcome == SceneBatchRunOutcome.Cancelled ? ", then stopped" : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{run.Applied} applied, {run.AlreadyInThatState} already so, {run.Refused} refused{ending}.");
    }

    internal static SceneBatchRun Untaken { get; } =
        new(SceneBatchRunOutcome.NothingToDo, 0, 0, 0);

    private enum SceneBatchTally
    {
        Applied,
        AlreadyInThatState,
        Refused,
    }

    // Every member is named and there is no discard arm, so a refusal kind added later stops the
    // build rather than arriving under whichever count a fallthrough chose.
    // An instance that already holds the scene is neither applied nor refused. An instance holding
    // no entry, or one it is not monitoring, is refused: the verb was not applied.
    private static SceneBatchTally TallyFor(SceneRefusalKind refusal)
        => refusal switch
        {
            SceneRefusalKind.None => SceneBatchTally.Applied,
            SceneRefusalKind.WhisparrAlreadyHoldsThisScene => SceneBatchTally.AlreadyInThatState,
            SceneRefusalKind.NoInstanceConnected
                or SceneRefusalKind.NoIdentityInThisNamespace
                or SceneRefusalKind.SeveralIdentitiesInThisNamespace
                or SceneRefusalKind.CapabilityAbsentOnThisGeneration
                or SceneRefusalKind.DidNotReachWhisparr
                or SceneRefusalKind.InstanceRefused
                or SceneRefusalKind.InstanceOffersNoQualityProfile
                or SceneRefusalKind.InstanceOffersNoRootFolder
                or SceneRefusalKind.WhisparrHasNoEntryForScene
                or SceneRefusalKind.WhisparrIsNotMonitoringThisScene => SceneBatchTally.Refused,
            _ => throw new ArgumentOutOfRangeException(
                nameof(refusal), refusal, "This refusal kind has no count written down for it."),
        };

    // A refusal taken before contacting the instance is a skip. Only an answer from the instance, or
    // a request that produced none, is a failure.
    private static JobUnitOutcome UnitOutcomeFor(SceneRefusalKind refusal)
        => refusal switch
        {
            SceneRefusalKind.None => JobUnitOutcome.Succeeded,
            SceneRefusalKind.DidNotReachWhisparr
                or SceneRefusalKind.InstanceRefused => JobUnitOutcome.Failed,
            SceneRefusalKind.NoInstanceConnected
                or SceneRefusalKind.NoIdentityInThisNamespace
                or SceneRefusalKind.SeveralIdentitiesInThisNamespace
                or SceneRefusalKind.CapabilityAbsentOnThisGeneration
                or SceneRefusalKind.InstanceOffersNoQualityProfile
                or SceneRefusalKind.InstanceOffersNoRootFolder
                or SceneRefusalKind.WhisparrAlreadyHoldsThisScene
                or SceneRefusalKind.WhisparrHasNoEntryForScene
                or SceneRefusalKind.WhisparrIsNotMonitoringThisScene => JobUnitOutcome.Skipped,
            _ => throw new ArgumentOutOfRangeException(
                nameof(refusal), refusal, "This refusal kind has no unit outcome written down for it."),
        };

    private static string UnitOf(int coveId) => coveId.ToString(CultureInfo.InvariantCulture);

    private static List<int> Distinct(IReadOnlyList<int> coveIds)
    {
        var seen = new HashSet<int>();
        var kept = new List<int>(coveIds.Count);
        foreach (var coveId in coveIds)
        {
            if (seen.Add(coveId))
            {
                kept.Add(coveId);
            }
        }

        return kept;
    }

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static int[] IdsIn(string? raw)
    {
        if (raw is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<int[]>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
