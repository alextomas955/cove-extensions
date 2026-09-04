using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

/// <summary>What the instance did with one scene registration.</summary>
internal enum SceneRegistration
{
    /// <summary>The instance took it, and its catalogue now holds the scene.</summary>
    Registered,

    /// <summary>The instance already held the scene, so nothing changed.</summary>
    AlreadyHeld,

    /// <summary>The instance would not take it.</summary>
    Refused,
}

/// <summary>How a run over one entity's scene identifiers ended.</summary>
internal enum AddAllMissingRunOutcome
{
    /// <summary>Every identifier was offered.</summary>
    Completed,

    /// <summary>The entity named no scene, so nothing was offered at all.</summary>
    NothingToRegister,

    /// <summary>The run was stopped part way. What it registered before that stays registered.</summary>
    Cancelled,
}

/// <summary>What a run over one entity's scene identifiers did.</summary>
internal sealed record AddAllMissingRun(
    AddAllMissingRunOutcome Outcome, int Registered, int AlreadyHeld, int Refused);

/// <summary>Offers one entity's own scenes to the instance, one at a time.</summary>
internal static class AddAllMissingPlanner
{
    /// <summary>The code the instance names a scene it already holds by.</summary>
    internal const string AlreadyHeldErrorCode = "MovieExistsValidator";

    /// <summary>What <paramref name="statusCode"/> and <paramref name="body"/> say happened.</summary>
    internal static SceneRegistration Classify(int statusCode, string? body)
        => (statusCode, body) switch
        {
            ( >= 200 and < 300, _) => SceneRegistration.Registered,
            (400, not null) => SceneRegistration.AlreadyHeld,
            _ => SceneRegistration.Refused,
        };

    /// <summary>Offers each of <paramref name="identities"/> once, then refreshes the catalogue.</summary>
    internal static async Task<AddAllMissingRun> RunAsync(
        IAsyncEnumerable<string> identities,
        Func<string, CancellationToken, Task<WhisparrResponse?>> register,
        Func<CancellationToken, Task<bool>> refreshCatalogue,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(refreshCatalogue);

        var registered = 0;
        await foreach (var identity in identities.WithCancellation(ct).ConfigureAwait(false))
        {
            var answer = await register(identity, ct).ConfigureAwait(false);
            if (answer is not null)
            {
                registered++;
            }
        }

        return new AddAllMissingRun(AddAllMissingRunOutcome.Completed, registered, 0, 0);
    }
}
