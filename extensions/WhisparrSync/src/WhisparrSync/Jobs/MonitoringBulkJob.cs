using System.Globalization;
using System.Text.Json;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Jobs;

/// <summary>What one enqueued batch was asked to do, as it came back out of the host's map.</summary>
/// <param name="EntityType">The selection type as the host's bar passed it, or empty.</param>
/// <param name="Verb">The gesture, or null where the map named none this product expresses.</param>
/// <param name="Scope">The scope named, or null where none was.</param>
/// <param name="EntityIds">The ids, or empty where the map carried none that could be read.</param>
public sealed record MonitorBulkBatch(
    string EntityType, MonitorBulkVerb? Verb, MonitorScope? Scope, int[] EntityIds);

/// <summary>
/// The bulk monitoring job's id, its (de)serialization onto the host's string-only parameter map,
/// and the batch loop every selection runs through.
/// </summary>
/// <remarks>
/// The host hands a job its parameters as <c>IReadOnlyDictionary&lt;string,string&gt;?</c>, so the id
/// array crosses as JSON under one key. <see cref="Decode"/> is total: it is read inside the host's
/// job runner, where a throw is a faulted job rather than a handled answer, so a batch nobody can
/// read is a clean no-op.
/// </remarks>
public static class MonitoringBulkJob
{
    /// <summary>The job id this extension's own type prefix is minted onto.</summary>
    public const string JobId = "monitoring-bulk";

    private const string EntityTypeKey = "entityType";
    private const string VerbKey = "verb";
    private const string ScopeKey = "scope";
    private const string EntityIdsKey = "entityIds";

    /// <summary>Encodes one batch onto the host's parameter map.</summary>
    /// <remarks>
    /// The entity type is carried in the spelling the selection bar passed rather than a normalized
    /// one, so what the batch matches on is what the host actually sent.
    /// </remarks>
    public static Dictionary<string, string> Encode(
        string entityType, MonitorBulkVerb verb, MonitorScope? scope, IReadOnlyList<int> entityIds)
        => new(StringComparer.Ordinal)
        {
            [EntityTypeKey] = entityType,
            [VerbKey] = verb.ToString(),
            [ScopeKey] = scope?.ToString() ?? string.Empty,
            [EntityIdsKey] = JsonSerializer.Serialize(entityIds),
        };

    /// <summary>Reads one batch back off the host's parameter map.</summary>
    /// <remarks>
    /// Never throws. A null map, a missing key, a blank value, unparseable JSON and JSON that is not
    /// an id array all answer no ids, and a verb this product does not express answers no verb rather
    /// than the first one declared - a batch that defaulted to an acting verb would act on a map
    /// nobody could read.
    /// </remarks>
    public static MonitorBulkBatch Decode(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null)
        {
            return new MonitorBulkBatch(string.Empty, null, null, []);
        }

        var entityType = Read(parameters, EntityTypeKey) ?? string.Empty;

        var verb = Enum.TryParse<MonitorBulkVerb>(Read(parameters, VerbKey), ignoreCase: true, out var named)
            && Enum.IsDefined(named)
                ? named
                : (MonitorBulkVerb?)null;

        var scope = Enum.TryParse<MonitorScope>(Read(parameters, ScopeKey), ignoreCase: true, out var covering)
            && Enum.IsDefined(covering)
                ? covering
                : (MonitorScope?)null;

        return new MonitorBulkBatch(entityType, verb, scope, IdsIn(Read(parameters, EntityIdsKey)));
    }

    /// <summary>
    /// Runs <paramref name="act"/> once per DISTINCT id in <paramref name="entityIds"/>, in the order
    /// the ids were supplied.
    /// </summary>
    /// <remarks>
    /// Deduplicated before any per-id work, keeping first appearance. A selection can genuinely carry
    /// one entity twice, and acting twice issues two adds for it.
    /// <para>
    /// Nothing selected does no work and opens no scope, and is reported as its own outcome rather
    /// than as a completed batch: an empty run in the host's Job Drawer reads as work that happened.
    /// </para>
    /// <para>
    /// The whole batch runs inside ONE scope already elevated to System. The batch carries no
    /// principal of its own, and Cove's per-principal query filters answer an anonymous reader with
    /// zero rows and no error, which on this path reports every entity as carrying no identity.
    /// </para>
    /// <para>
    /// A cancellation classifies the batch as cancelled and keeps what it had already recorded. The
    /// host stops a job by cancelling its token, so classifying that as a failure would report a
    /// shutdown as a fault.
    /// </para>
    /// </remarks>
    /// <param name="entityIds">The Cove ids selected, repeats and all.</param>
    /// <param name="scopes">The scope factory the extension was handed at initialization.</param>
    /// <param name="act">
    /// One entity's turn, over the batch's own elevated services. It answers a refusal kind rather
    /// than throwing, so one entity's refusal is not the whole batch's.
    /// </param>
    /// <param name="progress">The host's own progress, which the units are declared and reported on.</param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
    internal static async Task<MonitorBulkRun> RunAsync(
        IReadOnlyList<int> entityIds,
        IServiceScopeFactory scopes,
        Func<IServiceProvider, int, CancellationToken, Task<MonitorRefusalKind>> act,
        IJobProgress progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(act);
        ArgumentNullException.ThrowIfNull(progress);

        var selected = Distinct(entityIds);
        if (selected.Count == 0)
        {
            return MonitorBulkRun.NothingSelected;
        }

        progress.DeclareUnitCount(selected.Count);
        progress.DeclareUnits(selected.Select(coveId => (UnitOf(coveId), (string?)null)));

        var outcomes = new List<MonitorBulkOutcome>(selected.Count);

        return await RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
        {
            try
            {
                foreach (var coveId in selected)
                {
                    ct.ThrowIfCancellationRequested();

                    using var unit = progress.StartUnit(UnitOf(coveId));
                    var refusal = await act(services, coveId, ct).ConfigureAwait(false);
                    outcomes.Add(new MonitorBulkOutcome(coveId, refusal));
                    unit.Complete(UnitOutcomeFor(refusal), refusal == MonitorRefusalKind.None ? null : refusal.ToString());
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return MonitorBulkRun.Cancelled(outcomes);
            }

            return MonitorBulkRun.Completed(outcomes);
        }).ConfigureAwait(false);
    }

    /// <summary>The one line the host's Job Drawer shows for <paramref name="run"/>.</summary>
    /// <remarks>
    /// Counts rather than a list. The per-entity answers are the run's own units, which the drawer
    /// shows in the order they were declared; a sentence naming every entity would grow with the
    /// selection.
    /// </remarks>
    internal static string SummaryOf(MonitorBulkRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Outcome == MonitorBulkOutcomeKind.NothingSelected)
        {
            return "Nothing was selected, so nothing was done.";
        }

        var applied = run.Outcomes.Count(outcome => outcome.Refusal == MonitorRefusalKind.None);
        var refused = run.Outcomes.Count - applied;
        var ending = run.Outcome == MonitorBulkOutcomeKind.Cancelled ? ", then stopped" : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture, $"{applied} applied, {refused} refused{ending}.");
    }

    /// <summary>
    /// Which unit outcome one refusal kind is reported under.
    /// </summary>
    /// <remarks>
    /// Every member is named and there is no discard arm, so a refusal kind added later stops this
    /// build rather than arriving under whichever outcome a fallthrough chose.
    /// <para>
    /// A refusal this product took before contacting the instance is a SKIP: the entity was passed
    /// over for a stated reason. Only an answer from the instance itself is a failure.
    /// </para>
    /// </remarks>
    private static JobUnitOutcome UnitOutcomeFor(MonitorRefusalKind refusal)
        => refusal switch
        {
            MonitorRefusalKind.None => JobUnitOutcome.Succeeded,
            MonitorRefusalKind.InstanceRefused => JobUnitOutcome.Failed,
            MonitorRefusalKind.NotConfigured
                or MonitorRefusalKind.NoIdentityInThisNamespace
                or MonitorRefusalKind.SeveralIdentitiesInThisNamespace
                or MonitorRefusalKind.CapabilityAbsentOnThisGeneration
                or MonitorRefusalKind.NoQualityProfile
                or MonitorRefusalKind.NoRootFolder => JobUnitOutcome.Skipped,
            _ => throw new ArgumentOutOfRangeException(
                nameof(refusal), refusal, "This refusal kind has no unit outcome written down for it."),
        };

    private static string UnitOf(int coveId) => coveId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The ids with repeats removed, keeping first appearance.</summary>
    private static List<int> Distinct(IReadOnlyList<int> entityIds)
    {
        var seen = new HashSet<int>();
        var kept = new List<int>(entityIds.Count);
        foreach (var coveId in entityIds)
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
