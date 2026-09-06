using System.Text.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Missing;

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
/// request.
/// <para>
/// The instance's own catalogue route reports what the instance holds and enumerates nothing, so a
/// whole-catalogue read is both larger and unable to answer for a scene the instance does not hold.
/// </para>
/// </remarks>
internal sealed class SceneStatusPort(IWhisparrSceneStatusReading reading) : ISceneStatusPort
{
    public async Task<IReadOnlyDictionary<string, MissingSceneState>> ReadStatesAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        string entityForeignId,
        IReadOnlyList<string> providerSceneIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providerSceneIds);

        var presence = await reading
            .ReadEntityPresenceAsync(baseAddress, apiKey, kind, entityForeignId, ct)
            .ConfigureAwait(false);

        if (StateForWholePage(presence) is { } settled)
        {
            return providerSceneIds.ToDictionary(
                id => id, _ => settled, StringComparer.Ordinal);
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

    // Derived from the row's own fields. No row is an absence rather than a failure, which is the one
    // distinction a reader acts on differently.
    private static MissingSceneState StateOf(WhisparrResponse answered)
    {
        if (answered.StatusCode is not (>= 200 and < 300))
        {
            return MissingSceneState.StatusUnknown;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(answered.Body);
        }
        catch (JsonException)
        {
            return MissingSceneState.StatusUnknown;
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            {
                return MissingSceneState.StatusUnknown;
            }

            if (parsed.RootElement.GetArrayLength() == 0)
            {
                return MissingSceneState.NotAdded;
            }

            var row = parsed.RootElement[0];
            if (!row.TryGetProperty("monitored", out var monitored)
                || monitored.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return MissingSceneState.StatusUnknown;
            }

            return monitored.GetBoolean()
                ? MissingSceneState.Monitored
                : MissingSceneState.Unmonitored;
        }
    }
}
