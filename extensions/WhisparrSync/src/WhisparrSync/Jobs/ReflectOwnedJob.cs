using System.Globalization;
using System.Text.Json.Nodes;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Jobs;

/// <summary>Which entity one enqueued run is about, as it came back out of the host's map.</summary>
/// <param name="Kind">The entity kind, or null where the map named none this product expresses.</param>
/// <param name="CoveId">The Cove id, or zero where the map carried none that could be read.</param>
public sealed record ReflectOwnedBatch(WhisparrEntityKind? Kind, int CoveId);

/// <summary>
/// What one run needs from the connected instance, already aimed at it.
/// </summary>
/// <remarks>
/// Resolved when the run STARTS rather than when it was asked for. A run enqueued minutes ago must
/// not act on a connection, a capability or a hard-link setting read before it: the setting decides
/// whether every matched file is linked or copied in full, and it is the instance's to change at any
/// time.
/// </remarks>
/// <param name="Generation">Whose row spellings the parse answers are read under.</param>
/// <param name="ReadImportable">Reads one folder's attachable rows.</param>
/// <param name="Attach">Hands one folder's rows to the instance, answering whether it took them.</param>
internal sealed record ReflectOwnedAiming(
    WhisparrGeneration Generation,
    Func<string, CancellationToken, Task<string?>> ReadImportable,
    Func<JsonArray, CancellationToken, Task<bool>> Attach);

/// <summary>What one run acts through, and why there is nothing to act through.</summary>
/// <remarks>
/// Exactly one of two things is true of a null <paramref name="Through"/>: the instance's linking
/// setting stopped the run, in which case <paramref name="Skipped"/> names which of the two readings
/// it was, or something else did, in which case <paramref name="Skipped"/> is null and the run
/// reports as a completed run that attached nothing.
/// </remarks>
/// <param name="Through">What the run acts through, or null where it must not act.</param>
/// <param name="Skipped">
/// Which reading of the instance's linking setting stopped the run, or null where no setting
/// stopped it.
/// </param>
internal sealed record ReflectOwnedAim(
    ReflectOwnedAiming? Through, ReflectOwnedSkipReason? Skipped);

/// <summary>
/// The reflect-owned job's id, its (de)serialization onto the host's string-only parameter map, and
/// the folder loop one entity's run goes through.
/// </summary>
/// <remarks>
/// <see cref="Decode"/> is total: it is read inside the host's job runner, where a throw is a faulted
/// job rather than a handled answer, so a run nobody can read is a clean no-op.
/// </remarks>
public static class ReflectOwnedJob
{
    /// <summary>The job id this extension's own type prefix is minted onto.</summary>
    public const string JobId = "reflect-owned";

    private const string KindKey = "kind";
    private const string CoveIdKey = "coveId";

    /// <summary>Encodes one entity's run onto the host's parameter map.</summary>
    public static Dictionary<string, string> Encode(WhisparrEntityKind kind, int coveId)
        => new(StringComparer.Ordinal)
        {
            [KindKey] = kind.ToString(),
            [CoveIdKey] = coveId.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>Reads one entity's run back off the host's parameter map.</summary>
    /// <remarks>
    /// Never throws. A null map, a missing key, a blank value and a kind this product does not
    /// express all answer no kind rather than the first one declared, and a run that defaulted to a
    /// kind would act on an entity nobody named.
    /// </remarks>
    public static ReflectOwnedBatch Decode(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null)
        {
            return new ReflectOwnedBatch(null, 0);
        }

        var kind = Enum.TryParse<WhisparrEntityKind>(Read(parameters, KindKey), ignoreCase: true, out var named)
            && Enum.IsDefined(named)
                ? named
                : (WhisparrEntityKind?)null;

        var coveId = int.TryParse(
            Read(parameters, CoveIdKey), CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : 0;

        return new ReflectOwnedBatch(kind, coveId);
    }

    /// <summary>
    /// Reads each of the entity's folders through <paramref name="aiming"/> and hands the rows that
    /// can be attached to the instance, inside ONE scope elevated to System.
    /// </summary>
    /// <remarks>
    /// The run carries no principal of its own, and Cove's per-principal query filters answer an
    /// anonymous reader with zero rows and no error, which on this path would report an entity that
    /// holds files as holding none.
    /// <para>
    /// A run that cannot be aimed, or that names no entity, reports as a completed run that attached
    /// nothing. It has not been refused by the instance and there is nothing for a reader to retry;
    /// what it could not read is already a line in the host's log.
    /// </para>
    /// </remarks>
    /// <param name="batch">Which entity the run is about.</param>
    /// <param name="scopes">The scope factory the extension was handed at initialization.</param>
    /// <param name="aiming">
    /// Both what the run acts through, over the run's own elevated services, and why there is
    /// nothing to act through.
    /// </param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
    internal static Task<ReflectOwnedRun> RunAsync(
        ReflectOwnedBatch batch,
        IServiceScopeFactory scopes,
        Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAim>> aiming,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(aiming);

        return RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
        {
            if (batch.Kind is not { } kind)
            {
                return Untaken;
            }

            var aim = await aiming(services, ct).ConfigureAwait(false);
            if (aim.Through is not { } aimed)
            {
                return Untaken with { Skipped = aim.Skipped };
            }

            return await RunOneAsync(services, aimed, kind, batch.CoveId, ct).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Reads one entity's folders through <paramref name="aimed"/> and hands the rows that can be
    /// attached to the instance.
    /// </summary>
    /// <remarks>
    /// The ONE folder loop, reached by the enqueued run above and by a selection's per-entity step.
    /// It takes an open <paramref name="services"/> rather than opening its own scope, because a
    /// selection is already inside one that is elevated to System.
    /// </remarks>
    /// <param name="services">Elevated services the folder read is made through.</param>
    /// <param name="aimed">What the run acts through, resolved by its caller.</param>
    /// <param name="kind">Which entity kind the run is about.</param>
    /// <param name="coveId">Which entity.</param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
    internal static Task<ReflectOwnedRun> RunOneAsync(
        IServiceProvider services,
        ReflectOwnedAiming aimed,
        WhisparrEntityKind kind,
        int coveId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(aimed);

        return ReflectOwnedPlanner.RunAsync(
            aimed.Generation,
            services.GetRequiredService<IEntityFolderPort>().FoldersFor(kind, coveId, ct),
            aimed.ReadImportable,
            aimed.Attach,
            ct);
    }

    /// <summary>The one line the host's Job Drawer shows for <paramref name="run"/>.</summary>
    /// <remarks>
    /// Counts rather than a list of folders. A sentence naming each one would grow with the entity
    /// and would put recorded filesystem paths in a durable place nothing needs them in.
    /// <para>
    /// A run the instance's linking setting stopped reached no folder, so both its counts are zero
    /// and a count line would say nothing about it. The reason is the whole content of the line
    /// instead, because this line is the only place that run is reported at all: the gesture that
    /// started it was answered before the setting was read again.
    /// </para>
    /// </remarks>
    internal static string SummaryOf(ReflectOwnedRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Skipped is { } reason)
        {
            return SentenceFor(reason);
        }

        var ending = run.Outcome == ReflectOwnedRunOutcome.Cancelled ? ", then stopped" : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{run.FoldersAttached} linked, {run.FoldersRefused} refused{ending}.");
    }

    /// <summary>The one sentence a run stopped by <paramref name="reason"/> is reported in.</summary>
    /// <remarks>
    /// Read by the entity's own enqueued run and by a selection's linking step alike. Two statements
    /// of one sentence is how a selection comes to say something different from a click.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reason"/> is a skip reason no sentence is written down for.
    /// </exception>
    internal static string SentenceFor(ReflectOwnedSkipReason reason)
        => reason switch
        {
            ReflectOwnedSkipReason.HardLinksOff
                => "No files were linked: Whisparr's hard-link setting is off.",
            ReflectOwnedSkipReason.HardLinkSettingUnreadable
                => "No files were linked: Whisparr's hard-link setting could not be read.",
            _ => throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "This skip reason has no sentence written down for it."),
        };

    /// <summary>A run that reached no folder.</summary>
    /// <remarks>
    /// Stands for a run that reached no folder for ANY reason. Where there is a reason a reader can
    /// act on, it rides on the record rather than on this instance.
    /// </remarks>
    private static ReflectOwnedRun Untaken { get; } =
        new(ReflectOwnedRunOutcome.Completed, 0, 0);

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
