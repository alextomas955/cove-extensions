using System.Text.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Missing;

/// <summary>What one instance row says about one scene, read once.</summary>
/// <remarks>
/// Both answers come off the same row, so the state a card shows and the identifier a per-scene verb
/// names cannot disagree about which scene they describe.
/// </remarks>
/// <param name="State">What the instance holds for the scene.</param>
/// <param name="InstanceId">
/// The instance's own identifier for the scene, or null where the answer carried no row or the row
/// carried no usable one.
/// </param>
public sealed record SceneOnInstance(MissingSceneState State, int? InstanceId);

/// <summary>What the connected instance holds for each scene on one page.</summary>
public interface ISceneStatusPort
{
    /// <summary>
    /// The state of each of <paramref name="providerSceneIds"/>, keyed as the provider issued them.
    /// </summary>
    /// <remarks>
    /// Costs one entity read plus at most one read per scene, so it is bounded by the page and never
    /// by what the instance holds.
    /// </remarks>
    Task<IReadOnlyDictionary<string, MissingSceneState>> ReadStatesAsync(
        IWhisparrSceneStatusReading reading,
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        string entityForeignId,
        IReadOnlyList<string> providerSceneIds,
        CancellationToken ct);
}

/// <inheritdoc cref="ISceneStatusPort"/>
/// <remarks>
/// One entity probe decides the page before any per-scene read is issued. An instance holding no
/// entry for the entity holds none for a scene under it, so an absence settles forty cards with one
/// request. A kind the instance publishes no entity for has no probe to short-circuit with, and the
/// per-scene reads are issued directly at the same at-most-one-per-card cost.
/// <para>
/// The instance's own catalogue route reports what the instance holds and enumerates nothing, so a
/// whole-catalogue read is both larger and unable to answer for a scene the instance does not hold.
/// </para>
/// <para>
/// The reading role arrives per call rather than per construction. Which generation is connected is
/// a stored setting, so a role held from construction would be one obtained before the connection it
/// describes was known.
/// </para>
/// </remarks>
internal sealed class SceneStatusPort : ISceneStatusPort
{
    public async Task<IReadOnlyDictionary<string, MissingSceneState>> ReadStatesAsync(
        IWhisparrSceneStatusReading reading,
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        string entityForeignId,
        IReadOnlyList<string> providerSceneIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(providerSceneIds);

        // The instance publishes no tag entity, so on a tag page there is nothing to probe and the
        // per-scene reads are issued directly. Widening the probe into a catalogue read to have
        // something to ask would cost an answer that grows with what the instance holds.
        if (HasAnEntityToProbe(kind))
        {
            var presence = await reading
                .ReadEntityPresenceAsync(baseAddress, apiKey, kind, entityForeignId, ct)
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

            var answered = await reading
                .ReadSceneByRemoteIdAsync(baseAddress, apiKey, id, ct)
                .ConfigureAwait(false);
            states[id] = StateOf(answered);
        }

        return states;
    }

    /// <summary>Whether the instance addresses <paramref name="kind"/> as an entity of its own.</summary>
    private static bool HasAnEntityToProbe(WhisparrEntityKind kind)
        => kind is WhisparrEntityKind.Studio or WhisparrEntityKind.Performer;

    /// <summary>The state every card on the page takes, or null where each must be read.</summary>
    /// <remarks>
    /// A not-found means the instance holds no entry for the entity, which settles the page as added
    /// nowhere. Any other unsuccessful answer claims nothing about the instance.
    /// </remarks>
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

    // Derived from the row's own fields. No row is an absence rather than a failure, which is the one
    // distinction a reader acts on differently.
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

    /// <summary>The instance's own identifier on <paramref name="row"/>, or null where it has none.</summary>
    private static int? InstanceIdIn(JsonElement row)
        => row.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt32(out var held)
            && held > 0
                ? held
                : null;

    /// <summary>The instance holds no entry for the scene.</summary>
    private static SceneOnInstance Absent { get; } = new(MissingSceneState.NotAdded, null);

    /// <summary>Nothing about the instance could be established.</summary>
    private static SceneOnInstance Unknown { get; } = new(MissingSceneState.StatusUnknown, null);
}
