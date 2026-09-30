using System.Globalization;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Jobs;

/// <summary>Which entity one enqueued registration run is about.</summary>
/// <remarks>A null kind or a zero id means the parameter map carried none that could be read.</remarks>
public sealed record AddAllMissingBatch(WhisparrEntityKind? Kind, int CoveId);

// Resolved when the run starts, not when it was enqueued: the instance can change the profile and
// the root at any time.
internal sealed record AddAllMissingAiming(
    WhisparrGeneration Generation,
    Func<string, CancellationToken, Task<WhisparrResponse?>> Register,
    Func<CancellationToken, Task> RefreshCatalogue);

/// <summary>
/// The add-all-missing job's id, its (de)serialization onto the host's string-only parameter map,
/// and the scene loop one entity's run goes through.
/// </summary>
public static class AddAllMissingJob
{
    public const string JobId = "add-all-missing";

    private const string KindKey = "kind";
    private const string CoveIdKey = "coveId";

    public static Dictionary<string, string> Encode(WhisparrEntityKind kind, int coveId)
        => new(StringComparer.Ordinal)
        {
            [KindKey] = kind.ToString(),
            [CoveIdKey] = coveId.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>Reads one entity's run back off the host's parameter map.</summary>
    /// <remarks>
    /// Never throws; it runs inside the host's job runner, where a throw faults the job. An
    /// unreadable kind answers null rather than the first kind declared, so a run never registers
    /// under an entity nobody named.
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

    // Runs as System: the job carries no principal, and Cove's per-principal filters answer an
    // anonymous reader with zero rows and no error, so an entity holding scenes would read as empty.
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

    // Counts, never a list of scenes: the sentence must not grow with the entity.
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

    private static AddAllMissingRun Untaken { get; } =
        new(AddAllMissingRunOutcome.NothingToRegister, 0, 0, 0);

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
