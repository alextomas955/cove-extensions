using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

internal enum SceneRegistration
{
    Registered,

    AlreadyHeld,

    // Only the site path answers this; a scene is never moved.
    Moved,

    Refused,
}

internal enum AddAllMissingRunOutcome
{
    Completed,

    NothingToRegister,

    // Stopped part way. What was registered before the stop stays registered.
    Cancelled,
}

// Counts only. A member listing the identifiers would grow with the entity.
internal sealed record AddAllMissingRun(
    AddAllMissingRunOutcome Outcome, int Registered, int AlreadyHeld, int Refused);

// Offers one entity's own scenes to the connected instance, one bounded request each. Nothing
// outlives one identifier: each is offered, classified into a count and dropped, so nothing grows
// with the entity.
//
// Whether the instance already holds a scene is the instance's own answer, one row at a time, never
// computed from a catalogue listing. A second offer of a scene it holds costs one request and
// changes nothing.
//
// The catalogue refresh follows the loop, because a registration becomes visible in the instance's
// catalogue only once one has run. It is issued even where every scene was already held, and never
// on the cancelled path.
internal static class AddAllMissingPlanner
{
    // Transcribed from what the instance answered, pinned in
    // V3NamesASceneItAlreadyHoldsByAnErrorCodeTheControlDoesNotCarry.
    internal const string AlreadyHeldErrorCode = "MovieExistsValidator";

    // The answering seam's own refusal is read before the status, because the bounded read states a
    // refusal on a success status and an empty body: an answer this product could not hold says
    // nothing about whether the scene was registered.
    internal static SceneRegistration Classify(WhisparrResponse? answer)
        => answer is null || answer.Refusal is not MonitorRefusalKind.None
            ? SceneRegistration.Refused
            : Classify(answer.StatusCode, answer.Body);

    // The status separates an accepted registration from a refused one and nothing else: a scene
    // the instance already holds and a well-formed identifier no provider lists answer the same
    // status and content type. Only the error code tells them apart, so reading the status alone
    // would report a whole entity as already registered.
    //
    // Only that member is read. The rest of the refusal document carries a full stack trace and
    // reaches no count, no log line and no wire answer. An answer nothing can be read out of is
    // refused rather than held, so a reader is not told a catalogue is complete when it is not.
    internal static SceneRegistration Classify(int statusCode, string? body)
    {
        if (MonitoringProjector.AcceptedStatus(statusCode) == MonitorRefusalKind.None)
        {
            return SceneRegistration.Registered;
        }

        return NamesASceneAlreadyHeld(body) ? SceneRegistration.AlreadyHeld : SceneRegistration.Refused;
    }

    // A cancellation classifies the run as cancelled, never failed, and what was registered before
    // it stays registered. An empty identifier set answers its own outcome rather than a completed
    // run, because a run that did nothing still appears in the host's job list.
    internal static async Task<AddAllMissingRun> RunAsync(
        IAsyncEnumerable<string> identities,
        Func<string, CancellationToken, Task<WhisparrResponse?>> register,
        Func<CancellationToken, Task> refreshCatalogue,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(refreshCatalogue);

        var registered = 0;
        var alreadyHeld = 0;
        var refused = 0;
        var offered = 0;

        try
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var identity in identities.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                offered++;

                switch (Classify(await register(identity, ct).ConfigureAwait(false)))
                {
                    case SceneRegistration.Registered:
                        registered++;
                        break;
                    case SceneRegistration.AlreadyHeld:
                        alreadyHeld++;
                        break;
                    default:
                        refused++;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AddAllMissingRun(
                AddAllMissingRunOutcome.Cancelled, registered, alreadyHeld, refused);
        }

        if (offered == 0)
        {
            return new AddAllMissingRun(AddAllMissingRunOutcome.NothingToRegister, 0, 0, 0);
        }

        await refreshCatalogue(ct).ConfigureAwait(false);

        return new AddAllMissingRun(
            AddAllMissingRunOutcome.Completed, registered, alreadyHeld, refused);
    }

    private static bool NamesASceneAlreadyHeld(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        JsonArray? refusals;
        try
        {
            refusals = JsonNode.Parse(body) as JsonArray;
        }
        catch (JsonException)
        {
            return false;
        }

        return refusals is not null
            && refusals.OfType<JsonObject>().Any(
                refusal => refusal["errorCode"] is JsonValue code
                    && code.TryGetValue<string>(out var named)
                    && string.Equals(named, AlreadyHeldErrorCode, StringComparison.Ordinal));
    }
}
