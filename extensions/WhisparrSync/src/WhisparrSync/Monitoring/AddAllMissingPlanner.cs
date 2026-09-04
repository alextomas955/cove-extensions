using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

/// <summary>What the instance did with one scene registration.</summary>
internal enum SceneRegistration
{
    /// <summary>The instance took it, and its catalogue now holds the scene.</summary>
    Registered,

    /// <summary>The instance already held the scene, so nothing about it changed.</summary>
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

    /// <summary>
    /// The run was stopped part way. What it registered before that stays registered.
    /// </summary>
    Cancelled,
}

/// <summary>What a run over one entity's scene identifiers did.</summary>
/// <remarks>
/// Counts and nothing else. A member listing the identifiers would grow with the entity, and the
/// one line a reader sees is a sentence rather than a list.
/// </remarks>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Registered">How many scenes the instance's catalogue did not already hold.</param>
/// <param name="AlreadyHeld">How many it already held, which is not a failure.</param>
/// <param name="Refused">How many it would not take.</param>
internal sealed record AddAllMissingRun(
    AddAllMissingRunOutcome Outcome, int Registered, int AlreadyHeld, int Refused);

/// <summary>
/// Offers one entity's own scenes to the connected instance, one bounded request each.
/// </summary>
/// <remarks>
/// Pure. It drives the delegates it is given and performs no I/O of its own, and nothing outlives
/// one identifier: each is offered, classified into a count and dropped, so nothing here grows with
/// the entity or with the library behind it.
/// <para>
/// Whether the instance already holds a scene is ANSWERED by the instance, one row at a time,
/// rather than computed from a catalogue listing read off it. That is what keeps the read seam
/// unwidened: a second offer of a scene the instance holds costs one request and changes nothing.
/// </para>
/// <para>
/// The catalogue refresh follows the loop rather than preceding it, because a registration becomes
/// visible in the instance's own catalogue only once one has run. It is issued even where every
/// scene was already held - the refresh re-reads the instance's own metadata source, which is what
/// the reader asked for - and never on the cancelled path, where there is no complete set to make
/// visible.
/// </para>
/// </remarks>
internal static class AddAllMissingPlanner
{
    /// <summary>The code the instance names a scene it already holds by.</summary>
    /// <remarks>
    /// Transcribed from what the instance answered, pinned in
    /// <c>TheNewerGenerationNamesASceneItAlreadyHoldsByAnErrorCodeTheControlDoesNotCarry</c>.
    /// </remarks>
    internal const string AlreadyHeldErrorCode = "MovieExistsValidator";

    /// <summary>What <paramref name="answer"/> says happened, or that nothing did.</summary>
    /// <remarks>
    /// The refusal the answering seam states on the answer itself is read BEFORE the status, because
    /// the bounded read states one on a success status and an empty body: an answer this product
    /// could not hold says nothing about whether the scene was registered.
    /// </remarks>
    internal static SceneRegistration Classify(WhisparrResponse? answer)
        => answer is null || answer.Refusal is not MonitorRefusalKind.None
            ? SceneRegistration.Refused
            : Classify(answer.StatusCode, answer.Body);

    /// <summary>What <paramref name="statusCode"/> and <paramref name="body"/> say happened.</summary>
    /// <remarks>
    /// The status separates an accepted registration from a refused one and separates nothing else:
    /// a scene the instance already holds and a well-formed identifier no provider lists answer the
    /// SAME status and the same content type. The member that tells those two apart is the error
    /// code, which the already-held answer carries and the control does not, and a classification
    /// reading the status alone would report a whole entity as already registered.
    /// <para>
    /// Only that one member is read. The refusal document is otherwise left alone, because this
    /// generation answers a refused add with a body carrying a full stack trace, and it reaches no
    /// count, no log line and no wire answer.
    /// </para>
    /// <para>
    /// An answer nothing can be read out of is refused rather than held. Reporting a scene as held
    /// that the instance refused leaves the reader believing a catalogue is complete when it is not.
    /// </para>
    /// </remarks>
    internal static SceneRegistration Classify(int statusCode, string? body)
    {
        if (MonitoringProjector.AcceptedStatus(statusCode) == MonitorRefusalKind.None)
        {
            return SceneRegistration.Registered;
        }

        return NamesASceneAlreadyHeld(body) ? SceneRegistration.AlreadyHeld : SceneRegistration.Refused;
    }

    /// <summary>
    /// Offers each of <paramref name="identities"/> once through <paramref name="register"/>, then
    /// asks <paramref name="refreshCatalogue"/> to re-read the entity's catalogue.
    /// </summary>
    /// <remarks>
    /// A cancellation classifies the run as cancelled rather than failed, and what was registered
    /// before it stays registered: the scenes are in the instance's catalogue and there is nothing
    /// to undo.
    /// <para>
    /// An identifier set that is empty answers its own outcome rather than a completed run. A run
    /// that did nothing still appears in the host's job list, where it reads as work that happened.
    /// </para>
    /// </remarks>
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

    /// <summary>Whether <paramref name="body"/> is the instance naming a scene it already holds.</summary>
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
