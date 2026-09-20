using System.Globalization;
using System.Text.Json;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Jobs;

/// <summary>What one enqueued batch was asked to do.</summary>
/// <remarks>
/// A null verb or scope, an empty entity type and an empty id array each mean the parameter map
/// carried nothing that could be read.
/// </remarks>
public sealed record MonitorBulkBatch(
    string EntityType, MonitorBulkVerb? Verb, MonitorScope? Scope, int[] EntityIds);

// The hard-link setting is the instance's, not the entity's, so a batch is skipped whole or acts
// whole and the reason is one for the run. Counts and one entry per library root, never one entry
// per entity or per file.
internal sealed record MonitorBulkLinking(
    ReflectOwnedSkipReason? Skipped,
    int FoldersAttached,
    int FoldersRefused,
    IReadOnlyList<FolderAddressRefusal>? AddressRefusals = null,
    int EntriesLeftUnderAnotherRoot = 0,
    bool RootsCouldNotBeRead = false);

/// <summary>
/// The bulk monitoring job's id, its (de)serialization onto the host's string-only parameter map,
/// and the batch loop every selection runs through.
/// </summary>
/// <remarks>
/// The host hands a job its parameters as <c>IReadOnlyDictionary&lt;string,string&gt;?</c>, so the id
/// array crosses as JSON under one key.
/// </remarks>
public static class MonitoringBulkJob
{
    public const string JobId = "monitoring-bulk";

    private const string EntityTypeKey = "entityType";
    private const string VerbKey = "verb";
    private const string ScopeKey = "scope";
    private const string EntityIdsKey = "entityIds";

    // The entity type is carried in the spelling the selection bar passed, not a normalized one, so
    // the batch matches on what the host actually sent.
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
    /// Never throws; it runs inside the host's job runner, where a throw faults the job. Unreadable
    /// JSON answers no ids, and an unreadable verb answers null rather than the first verb declared,
    /// so a batch never acts on a map nobody could read.
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

    // Deduplicated before any per-id work: a selection can carry one entity twice, and acting twice
    // issues two adds.
    // Runs as System: the batch carries no principal, and Cove's per-principal filters answer an
    // anonymous reader with zero rows and no error, reporting every entity as carrying no identity.
    // A stop classifies as cancelled, not failed. The host stops a job by cancelling its token, so a
    // failure here would report a shutdown as a fault.
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

    // A null EntityId means the entity cannot be named on the instance at all.
    internal sealed record MonitorBulkAim(int? EntityId, MonitorRefusalKind Refusal);

    // For a verb whose instance-side command names an id array rather than one entity.
    // An entity that does not resolve is reported and not sent: the command fails outright on the
    // first id the instance does not hold, so one unheld entity would cost every other entity its
    // search.
    // Scope, System elevation and the cancellation rule are RunAsync's.
    internal static async Task<MonitorBulkRun> RunOneCallAsync(
        IReadOnlyList<int> entityIds,
        IServiceScopeFactory scopes,
        Func<IServiceProvider, int, CancellationToken, Task<MonitorBulkAim>> aim,
        Func<IServiceProvider, IReadOnlyList<int>, CancellationToken, Task<MonitorRefusalKind>> act,
        IJobProgress progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(aim);
        ArgumentNullException.ThrowIfNull(act);
        ArgumentNullException.ThrowIfNull(progress);

        var selected = Distinct(entityIds);
        if (selected.Count == 0)
        {
            return MonitorBulkRun.NothingSelected;
        }

        return await RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
        {
            var outcomes = new List<MonitorBulkOutcome>(selected.Count);
            var named = new List<int>(selected.Count);
            var open = new List<(int CoveId, IJobUnit Unit)>(selected.Count);

            try
            {
                foreach (var coveId in selected)
                {
                    ct.ThrowIfCancellationRequested();

                    var unit = progress.StartUnit(UnitOf(coveId));
                    var aimed = await aim(services, coveId, ct).ConfigureAwait(false);
                    if (aimed.EntityId is { } entityId)
                    {
                        named.Add(entityId);
                        open.Add((coveId, unit));
                        continue;
                    }

                    outcomes.Add(new MonitorBulkOutcome(coveId, aimed.Refusal));
                    Close(unit, aimed.Refusal);
                }

                var refusal = named.Count == 0
                    ? MonitorRefusalKind.None
                    : await act(services, named, ct).ConfigureAwait(false);

                foreach (var (coveId, unit) in open)
                {
                    outcomes.Add(new MonitorBulkOutcome(coveId, refusal));
                    Close(unit, refusal);
                }

                open.Clear();
                return MonitorBulkRun.Completed(Supplied(selected, outcomes));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return MonitorBulkRun.Cancelled(Supplied(selected, outcomes));
            }
            finally
            {
                foreach (var (_, unit) in open)
                {
                    unit.Dispose();
                }
            }
        }).ConfigureAwait(false);
    }

    private static void Close(IJobUnit unit, MonitorRefusalKind refusal)
    {
        unit.Complete(
            UnitOutcomeFor(refusal), refusal == MonitorRefusalKind.None ? null : refusal.ToString());
        unit.Dispose();
    }

    // Outcomes are recorded as they settle, so a refused entity settles before an acted one whatever
    // order they were selected in. The supplied order is restored so a reader can match the list
    // against the selection they made.
    private static List<MonitorBulkOutcome> Supplied(
        IReadOnlyList<int> selected, IReadOnlyList<MonitorBulkOutcome> outcomes)
    {
        var byCoveId = outcomes.ToDictionary(outcome => outcome.CoveId);
        return [.. selected
            .Where(byCoveId.ContainsKey)
            .Select(coveId => byCoveId[coveId])];
    }

    // Counts, never a list: a sentence naming every entity would grow with the selection. The
    // per-entity answers are the run's own units.
    // The linking work is reported apart from the monitor outcomes, because a monitor that took
    // while linking was passed over is not a refused monitor.
    internal static string SummaryOf(MonitorBulkRun run, MonitorBulkLinking? linking = null)
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
                CultureInfo.InvariantCulture, $"{applied} applied, {refused} refused{ending}.")
            + (linking is { } linked ? " " + LinkingIn(linked) : string.Empty);
    }

    // Composed by the same member a single entity's run is, so a selection cannot say something
    // different about the same linking work.
    private static string LinkingIn(MonitorBulkLinking linking)
        => ReflectOwnedJob.LineFor(
            linking.Skipped,
            linking.FoldersAttached,
            linking.FoldersRefused,
            linking.AddressRefusals,
            linking.EntriesLeftUnderAnotherRoot,
            cancelled: false,
            linking.RootsCouldNotBeRead);

    // Every member is named and there is no discard arm, so a refusal kind added later stops the
    // build rather than arriving under whichever outcome a fallthrough chose.
    // A refusal taken before contacting the instance is a skip; only an answer from the instance is
    // a failure. An instance holding no such entity is the one instance answer that is still a skip,
    // because nothing was sent that failed. A change the instance did not report is a failure: the
    // request was accepted, so the entity is not known to be monitored.
    private static JobUnitOutcome UnitOutcomeFor(MonitorRefusalKind refusal)
        => refusal switch
        {
            MonitorRefusalKind.None => JobUnitOutcome.Succeeded,
            MonitorRefusalKind.InstanceRefused
                or MonitorRefusalKind.AnswerTooLargeToRead
                or MonitorRefusalKind.InstanceDidNotReportTheChange => JobUnitOutcome.Failed,
            MonitorRefusalKind.NotConfigured
                or MonitorRefusalKind.NoIdentityInThisNamespace
                or MonitorRefusalKind.SeveralIdentitiesInThisNamespace
                or MonitorRefusalKind.CapabilityAbsentOnThisGeneration
                or MonitorRefusalKind.NoQualityProfile
                or MonitorRefusalKind.NoRootFolder
                or MonitorRefusalKind.NoAgreedRootForThisEntity
                or MonitorRefusalKind.InstanceHoldsNoSuchEntity => JobUnitOutcome.Skipped,
            _ => throw new ArgumentOutOfRangeException(
                nameof(refusal), refusal, "This refusal kind has no unit outcome written down for it."),
        };

    private static string UnitOf(int coveId) => coveId.ToString(CultureInfo.InvariantCulture);

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
