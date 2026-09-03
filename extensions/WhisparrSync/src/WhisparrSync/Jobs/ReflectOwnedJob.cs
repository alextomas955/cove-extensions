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
    /// What the run needs from the connected instance, over the run's own elevated services, or null
    /// where it must not act.
    /// </param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
    internal static Task<ReflectOwnedRun> RunAsync(
        ReflectOwnedBatch batch,
        IServiceScopeFactory scopes,
        Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAiming?>> aiming,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(aiming);

        return RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
        {
            if (batch.Kind is not { } kind
                || await aiming(services, ct).ConfigureAwait(false) is not { } aimed)
            {
                return Untaken;
            }

            return await ReflectOwnedPlanner.RunAsync(
                aimed.Generation,
                services.GetRequiredService<IEntityFolderPort>().FoldersFor(kind, batch.CoveId, ct),
                aimed.ReadImportable,
                aimed.Attach,
                ct).ConfigureAwait(false);
        });
    }

    /// <summary>The one line the host's Job Drawer shows for <paramref name="run"/>.</summary>
    /// <remarks>
    /// Counts rather than a list of folders. A sentence naming each one would grow with the entity
    /// and would put recorded filesystem paths in a durable place nothing needs them in.
    /// </remarks>
    internal static string SummaryOf(ReflectOwnedRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var ending = run.Outcome == ReflectOwnedRunOutcome.Cancelled ? ", then stopped" : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{run.FoldersAttached} linked, {run.FoldersRefused} refused{ending}.");
    }

    /// <summary>A run that reached no folder, because it was never aimed at an entity.</summary>
    private static ReflectOwnedRun Untaken { get; } =
        new(ReflectOwnedRunOutcome.Completed, 0, 0);

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
