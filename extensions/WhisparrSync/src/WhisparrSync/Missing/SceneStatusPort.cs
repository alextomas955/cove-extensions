using System.Text.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Missing;

/// <summary>What one instance row says about one scene, read once.</summary>
/// <remarks>
/// Both answers come off the same row, so the state a card shows and the identifier a per-scene verb
/// names cannot disagree about which scene they describe. The identifier is null where the answer
/// carried no row or the row carried no usable one.
/// </remarks>
public sealed record SceneOnInstance(MissingSceneState State, int? InstanceId);

// One entity probe decides the page before any per-scene read is issued: an instance holding no
// entry for the entity holds none for a scene under it. A kind the instance publishes no entity for
// has no probe to short-circuit with, and the per-scene reads are issued directly.
//
// The reading role is a parameter rather than held state. Which generation is connected is a stored
// setting, so a role captured ahead of the call would be one obtained before the connection it
// describes was known.
internal static class SceneStatusPort
{
    // Costs one entity read plus at most one read per scene, so it is bounded by the page and never
    // by what the instance holds.
    public static async Task<IReadOnlyDictionary<string, MissingSceneState>> ReadStatesAsync(
        IWhisparrSceneStatusReading reading,
        WhisparrEntityKind kind,
        string entityForeignId,
        IReadOnlyList<string> providerSceneIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(providerSceneIds);

        // The instance publishes no tag entity, so a tag page has nothing to probe. Widening the
        // probe into a catalogue read would cost an answer that grows with what the instance holds.
        if (HasAnEntityToProbe(kind))
        {
            var presence = await reading
                .ReadEntityPresenceAsync(kind, entityForeignId, ct)
                .ConfigureAwait(false);

            if (StateForWholePage(presence) is { } settled)
            {
                return providerSceneIds.ToDictionary(
                    id => id, _ => settled, StringComparer.Ordinal);
            }
        }

        var states = new Dictionary<string, MissingSceneState>(StringComparer.Ordinal);
        foreach (var id in providerSceneIds)
        {
            if (states.ContainsKey(id))
            {
                continue;
            }

            var answered = await reading.ReadSceneByRemoteIdAsync(id, ct).ConfigureAwait(false);
            states[id] = StateOf(answered);
        }

        return states;
    }

    private static bool HasAnEntityToProbe(WhisparrEntityKind kind)
        => kind is WhisparrEntityKind.Studio or WhisparrEntityKind.Performer;

    // A not-found means the instance holds no entry for the entity, which settles the page as added
    // nowhere. Any other unsuccessful answer claims nothing about the instance. Null means each
    // scene must be read.
    private static MissingSceneState? StateForWholePage(WhisparrResponse presence)
    {
        if (presence.StatusCode == 404)
        {
            return MissingSceneState.NotAdded;
        }

        return presence.StatusCode is >= 200 and < 300
            ? null
            : MissingSceneState.StatusUnknown;
    }

    private static MissingSceneState StateOf(WhisparrResponse answered) => ReadRow(answered).State;

    // No row is an absence rather than a failure, which is the one distinction a reader acts on
    // differently.
    internal static SceneOnInstance ReadRow(WhisparrResponse answered)
    {
        ArgumentNullException.ThrowIfNull(answered);

        // The per-scene route answers a held scene and an unheld one alike with a list, so a
        // not-found here is the instance stating an absence rather than declining to answer.
        if (answered.StatusCode == 404)
        {
            return Absent;
        }

        if (answered.StatusCode is not (>= 200 and < 300))
        {
            return Unknown;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(answered.Body);
        }
        catch (JsonException)
        {
            return Unknown;
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Unknown;
            }

            if (parsed.RootElement.GetArrayLength() == 0)
            {
                return Absent;
            }

            var row = parsed.RootElement[0];
            if (!row.TryGetProperty("monitored", out var monitored)
                || monitored.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Unknown;
            }

            return new SceneOnInstance(
                monitored.GetBoolean()
                    ? MissingSceneState.Monitored
                    : MissingSceneState.Unmonitored,
                InstanceIdIn(row));
        }
    }

    private static int? InstanceIdIn(JsonElement row)
        => row.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt32(out var held)
            && held > 0
                ? held
                : null;

    private static SceneOnInstance Absent { get; } = new(MissingSceneState.NotAdded, null);

    private static SceneOnInstance Unknown { get; } = new(MissingSceneState.StatusUnknown, null);
}
