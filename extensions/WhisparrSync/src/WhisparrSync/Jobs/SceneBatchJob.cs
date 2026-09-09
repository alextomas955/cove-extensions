using System.Globalization;
using System.Text.Json;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;

namespace WhisparrSync.Jobs;

/// <summary>What one enqueued selection of scenes was asked to do, as the host's map held it.</summary>
/// <param name="Verb">The gesture, or null where the map named none this product expresses.</param>
/// <param name="CoveIds">The ids, or empty where the map carried none that could be read.</param>
public sealed record SceneBatchBatch(SceneBatchVerb? Verb, int[] CoveIds);

/// <summary>How a run over one selection of scenes ended.</summary>
internal enum SceneBatchRunOutcome
{
    /// <summary>Every selected scene had its turn.</summary>
    Completed,

    /// <summary>The run reached no scene to act on at all.</summary>
    NothingToDo,

    /// <summary>The run was stopped part way. What it applied before that stands.</summary>
    Cancelled,
}

/// <summary>What a run over one selection of scenes did.</summary>
/// <remarks>
/// Counts and nothing else. A member listing the identifiers would grow with the selection, and the
/// one line a reader sees is a sentence rather than a list.
/// </remarks>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Applied">How many the verb took on.</param>
/// <param name="AlreadyInThatState">
/// How many the instance already held, or already had in the state the verb asks for, which is not
/// a failure.
/// </param>
/// <param name="Refused">How many it would not, or could not, be applied to.</param>
internal sealed record SceneBatchRun(
    SceneBatchRunOutcome Outcome, int Applied, int AlreadyInThatState, int Refused);

/// <summary>
/// The scene batch job's id, its (de)serialization onto the host's string-only parameter map, and
/// the scene loop one selection's run goes through.
/// </summary>
/// <remarks>
/// The host hands a job its parameters as <c>IReadOnlyDictionary&lt;string,string&gt;?</c>, so the id
/// array crosses as JSON under one key. <see cref="Decode"/> is total: it is read inside the host's
/// job runner, where a throw is a faulted job rather than a handled answer, so a run nobody can read
/// is a clean no-op.
/// </remarks>
public static class SceneBatchJob
{
    /// <summary>The job id this extension's own type prefix is minted onto.</summary>
    public const string JobId = "scene-batch";

    private const string VerbKey = "verb";
    private const string CoveIdsKey = "coveIds";

    /// <summary>Encodes one selection's run onto the host's parameter map.</summary>
    public static Dictionary<string, string> Encode(
        SceneBatchVerb verb, IReadOnlyList<int> coveIds)
        => new(StringComparer.Ordinal)
        {
            [VerbKey] = verb.ToString(),
            [CoveIdsKey] = JsonSerializer.Serialize(coveIds),
        };

    /// <summary>Reads one selection's run back off the host's parameter map.</summary>
    /// <remarks>
    /// Never throws. A null map, a missing key, a blank value, unparseable JSON and JSON that is not
    /// an id array all answer no ids, and a verb this product does not express answers no verb rather
    /// than the first one declared - a run that defaulted to an acting verb would act on a map nobody
    /// could read.
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

    /// <summary>
    /// Runs <paramref name="act"/> once per DISTINCT id in <paramref name="coveIds"/>, in the order
    /// the ids were supplied, inside ONE scope elevated to System.
    /// </summary>
    /// <remarks>
    /// Deduplicated before any per-scene work, keeping first appearance. A selection can genuinely
    /// carry one video twice, and acting twice issues two requests for it.
    /// <para>
    /// The run carries no principal of its own, and Cove's per-principal query filters answer an
    /// anonymous reader with zero rows and no error, which on this path reports every scene as
    /// carrying no identity.
    /// </para>
    /// <para>
    /// Nothing selected does no work and opens no scope, and is reported as its own outcome rather
    /// than as a completed run: an empty run in the host's Job Drawer reads as work that happened.
    /// </para>
    /// <para>
    /// A cancellation classifies the run as cancelled and keeps what it had already recorded. The
    /// host stops a job by cancelling its token, so classifying that as a failure would report a
    /// shutdown as a fault.
    /// </para>
    /// </remarks>
    /// <param name="coveIds">The Cove videos selected, repeats and all.</param>
    /// <param name="scopes">The scope factory the extension was handed at initialization.</param>
    /// <param name="act">
    /// One scene's turn, over the run's own elevated services. It answers a refusal kind rather than
    /// throwing, so one scene's refusal is not the whole run's.
    /// </param>
    /// <param name="progress">The host's own progress, which the units are reported on.</param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
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

    /// <summary>The one line the host's Job Drawer shows for <paramref name="run"/>.</summary>
    /// <remarks>
    /// Counts rather than a list. The per-scene answers are the run's own units, which the drawer
    /// shows in the order they were started; a sentence naming every scene would grow with the
    /// selection.
    /// </remarks>
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

    /// <summary>A run that acted on nothing, because the map named no scene to act on.</summary>
    internal static SceneBatchRun Untaken { get; } =
        new(SceneBatchRunOutcome.NothingToDo, 0, 0, 0);

    /// <summary>Which of the run's three counts one refusal kind is added to.</summary>
    private enum SceneBatchTally
    {
        Applied,
        AlreadyInThatState,
        Refused,
    }

    /// <summary>Which count <paramref name="refusal"/> is reported under.</summary>
    /// <remarks>
    /// Every member is named and there is no discard arm, so a refusal kind added later stops this
    /// build rather than arriving under whichever count a fallthrough chose.
    /// <para>
    /// An instance that already holds the scene is neither applied nor refused: the state the reader
    /// wanted is the state the instance is already in.
    /// </para>
    /// <para>
    /// An instance holding no entry, or holding one it is not monitoring, is REFUSED rather than
    /// already so. The verb was not applied and the reader has somewhere to go next.
    /// </para>
    /// </remarks>
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

    /// <summary>Which unit outcome one refusal kind is reported under.</summary>
    /// <remarks>
    /// A refusal this product took before contacting the instance is a SKIP: the scene was passed
    /// over for a stated reason. Only an answer from the instance itself, or a request that produced
    /// none, is a failure.
    /// </remarks>
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

    /// <summary>The ids with repeats removed, keeping first appearance.</summary>
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
