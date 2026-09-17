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

/// <summary>What one batch's linking step did across every entity it reached.</summary>
/// <remarks>
/// The hard-link setting is the INSTANCE's rather than the entity's, so a batch is skipped whole or
/// acts whole and the reason is one for the run rather than one per entity.
/// </remarks>
/// <param name="Skipped">Why no file was linked, or null where the step ran.</param>
/// <param name="FoldersAttached">How many folders the instance took, across every entity.</param>
/// <param name="FoldersRefused">How many it declined.</param>
/// <param name="AddressRefusals">
/// One entry per library root no path was established under, across every entity. A root reached by
/// several entities is carried once.
/// </param>
/// <param name="EntriesLeftUnderAnotherRoot">
/// How many files were left out, across every entity, because the instance holds the site they
/// would join under a different declared root from the file.
/// </param>
internal sealed record MonitorBulkLinking(
    ReflectOwnedSkipReason? Skipped,
    int FoldersAttached,
    int FoldersRefused,
    IReadOnlyList<FolderAddressRefusal>? AddressRefusals = null,
    int EntriesLeftUnderAnotherRoot = 0);

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
    /// <param name="progress">The host's own progress, which the units are reported on.</param>
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

    /// <summary>What resolving one selected entity against the connected instance produced.</summary>
    /// <param name="EntityId">
    /// The identifier the instance's own record carries for it, or null where the entity cannot be
    /// named on the instance at all.
    /// </param>
    /// <param name="Refusal">Why it cannot be named there, or that it can.</param>
    internal sealed record MonitorBulkAim(int? EntityId, MonitorRefusalKind Refusal);

    /// <summary>
    /// Resolves every DISTINCT id in <paramref name="entityIds"/>, then acts on the ones that
    /// resolved in ONE call.
    /// </summary>
    /// <remarks>
    /// For a verb whose instance-side command names an id array rather than one entity. The
    /// resolution is still per entity, because each entity's identifier is read from the library and
    /// then from the instance's own record of it.
    /// <para>
    /// An entity that does not resolve is REPORTED and not sent. The command iterates the whole array
    /// and fails outright on the first id the instance does not hold, naming that one alone, so one
    /// unheld entity in a selection would otherwise cost every other entity its search and leave the
    /// reader an error naming one id out of a hundred.
    /// </para>
    /// <para>
    /// A selection where nothing resolves sends nothing. There is no command to compose, and an
    /// empty id array is accepted by the instance and runs over nothing.
    /// </para>
    /// <para>
    /// Each entity's unit is completed only once its outcome is settled. An entity that could not be
    /// resolved settles at that moment and its unit closes there; one that was named in the command
    /// settles when the command answers, and every such unit closes together on that one answer. A
    /// unit closed as done before the command left would report work that had not happened.
    /// </para>
    /// <para>
    /// The scope, the elevation and the cancellation rule are <see cref="RunAsync"/>'s.
    /// </para>
    /// </remarks>
    /// <param name="entityIds">The Cove ids selected, repeats and all.</param>
    /// <param name="scopes">The scope factory the extension was handed at initialization.</param>
    /// <param name="aim">One entity's resolution, over the batch's own elevated services.</param>
    /// <param name="act">
    /// The one call, over every entity that resolved. It answers a refusal kind rather than throwing,
    /// and that one answer is every named entity's outcome.
    /// </param>
    /// <param name="progress">The host's own progress, which the units are reported on.</param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
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

    /// <summary>
    /// <paramref name="outcomes"/> back in the order <paramref name="selected"/> supplied.
    /// </summary>
    /// <remarks>
    /// The outcomes are recorded as they settle, and on this path a refused entity settles before an
    /// acted one whatever order they were selected in. A reader matches this list against the
    /// selection they made, so the supplied order is restored before it is answered.
    /// </remarks>
    private static List<MonitorBulkOutcome> Supplied(
        IReadOnlyList<int> selected, IReadOnlyList<MonitorBulkOutcome> outcomes)
    {
        var byCoveId = outcomes.ToDictionary(outcome => outcome.CoveId);
        return [.. selected
            .Where(byCoveId.ContainsKey)
            .Select(coveId => byCoveId[coveId])];
    }

    /// <summary>The one line the host's Job Drawer shows for <paramref name="run"/>.</summary>
    /// <remarks>
    /// Counts rather than a list. The per-entity answers are the run's own units, which the drawer
    /// shows in the order they were started; a sentence naming every entity would grow with the
    /// selection.
    /// <para>
    /// The linking work is reported APART from the monitor outcomes. A monitor that took while the
    /// linking step was passed over is not a refused monitor, and one number for both would read as
    /// one.
    /// </para>
    /// </remarks>
    /// <param name="run">How the batch ended and what each entity's turn produced.</param>
    /// <param name="linking">
    /// What the linking step did, or null where the batch's verb reaches no linking step and the
    /// connected generation's ability to link is therefore not something the run can report on.
    /// </param>
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

    /// <summary>The one sentence <paramref name="linking"/> is reported in.</summary>
    /// <remarks>
    /// Composed by the same member a single entity's run is, so a selection cannot say something
    /// different about the same linking work. The batch's own ending is on the clause before this
    /// one, so nothing here repeats it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="linking"/> names a reason this product does not express.
    /// </exception>
    private static string LinkingIn(MonitorBulkLinking linking)
        => ReflectOwnedJob.LineFor(
            linking.Skipped,
            linking.FoldersAttached,
            linking.FoldersRefused,
            linking.AddressRefusals,
            linking.EntriesLeftUnderAnotherRoot,
            cancelled: false);

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
    /// <para>
    /// An instance holding no such entity is the one refusal from an instance that is still a skip.
    /// The unit was passed over for a stated reason and nothing was sent that failed.
    /// </para>
    /// <para>
    /// A change the instance did not report is a FAILURE and not a skip, however much it resembles an
    /// absence. The request left and was accepted, so the unit was not passed over, and the entity is
    /// not known to be monitored.
    /// </para>
    /// </remarks>
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
