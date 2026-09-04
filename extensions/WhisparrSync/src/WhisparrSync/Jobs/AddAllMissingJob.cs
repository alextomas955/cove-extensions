using System.Globalization;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Jobs;

/// <summary>Which entity one enqueued registration run is about, as the host's map held it.</summary>
/// <param name="Kind">The entity kind, or null where the map named none this product expresses.</param>
/// <param name="CoveId">The Cove id, or zero where the map carried none that could be read.</param>
public sealed record AddAllMissingBatch(WhisparrEntityKind? Kind, int CoveId);

/// <summary>What one registration run needs from the connected instance, already aimed at it.</summary>
/// <remarks>
/// Resolved when the run STARTS rather than when it was asked for. The profile and the root each
/// registration carries are the instance's to change at any time, and a run enqueued minutes ago
/// must not create catalogue items under values read before that.
/// </remarks>
/// <param name="Generation">Whose namespace the library's own scene identifiers are read under.</param>
/// <param name="Register">Offers one scene, answering whatever the instance said.</param>
/// <param name="RefreshCatalogue">Asks the instance to re-read the entity's own catalogue.</param>
internal sealed record AddAllMissingAiming(
    WhisparrGeneration Generation,
    Func<string, CancellationToken, Task<WhisparrResponse?>> Register,
    Func<CancellationToken, Task> RefreshCatalogue);

/// <summary>
/// The add-all-missing job's id, its (de)serialization onto the host's string-only parameter map,
/// and the scene loop one entity's run goes through.
/// </summary>
/// <remarks>
/// <see cref="Decode"/> is total: it is read inside the host's job runner, where a throw is a
/// faulted job rather than a handled answer, so a run nobody can read is a clean no-op.
/// </remarks>
public static class AddAllMissingJob
{
    /// <summary>The job id this extension's own type prefix is minted onto.</summary>
    public const string JobId = "add-all-missing";

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
    /// kind would create catalogue items under an entity nobody named.
    /// </remarks>
    public static AddAllMissingBatch Decode(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null)
        {
            return new AddAllMissingBatch(null, 0);
        }

        var kind = Enum.TryParse<WhisparrEntityKind>(
            Read(parameters, KindKey), ignoreCase: true, out var named) && Enum.IsDefined(named)
                ? named
                : (WhisparrEntityKind?)null;

        var coveId = int.TryParse(
            Read(parameters, CoveIdKey), CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : 0;

        return new AddAllMissingBatch(kind, coveId);
    }

    /// <summary>
    /// Offers each of the entity's own scene identifiers to the instance through
    /// <paramref name="aiming"/>, inside ONE scope elevated to System.
    /// </summary>
    /// <remarks>
    /// The run carries no principal of its own, and Cove's per-principal query filters answer an
    /// anonymous reader with zero rows and no error, which on this path would report an entity that
    /// holds scenes as holding none and register nothing at all.
    /// <para>
    /// A run that cannot be aimed, or that names no entity, reports as a run with nothing to
    /// register. It has not been refused by the instance and there is nothing for a reader to retry;
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
    internal static Task<AddAllMissingRun> RunAsync(
        AddAllMissingBatch batch,
        IServiceScopeFactory scopes,
        Func<IServiceProvider, CancellationToken, Task<AddAllMissingAiming?>> aiming,
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

            return await AddAllMissingPlanner.RunAsync(
                services.GetRequiredService<IEntitySceneIdentityPort>()
                    .SceneIdentitiesFor(kind, batch.CoveId, aimed.Generation, ct),
                aimed.Register,
                aimed.RefreshCatalogue,
                ct).ConfigureAwait(false);
        });
    }

    /// <summary>The one line the host's job list shows for <paramref name="run"/>.</summary>
    /// <remarks>
    /// Counts rather than a list of scenes. A sentence naming each one would grow with the entity.
    /// An already-held scene is stated apart from a refused one, because they are different facts:
    /// one is the catalogue already being complete and the other is the instance declining.
    /// </remarks>
    internal static string SummaryOf(AddAllMissingRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Outcome == AddAllMissingRunOutcome.NothingToRegister)
        {
            return "No scene here carries an identifier this Whisparr names entities by.";
        }

        var ending = run.Outcome == AddAllMissingRunOutcome.Cancelled ? ", then stopped" : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{run.Registered} registered, {run.AlreadyHeld} already held, {run.Refused} refused{ending}.");
    }

    /// <summary>A run that reached no identifier, because it was never aimed at an entity.</summary>
    private static AddAllMissingRun Untaken { get; } =
        new(AddAllMissingRunOutcome.NothingToRegister, 0, 0, 0);

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
