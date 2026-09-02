using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;

namespace WhisparrSync.Monitoring;

/// <summary>What an instance's answer about one entity means.</summary>
/// <remarks>
/// Whether the instance holds the entity at all is read from the status, and the monitored flag from
/// the body. A refused add is classified from the status alone and the instance's own words are not
/// read at all: this generation answers a refused add with a body carrying a full .NET stack trace,
/// and a field nothing reads cannot reach a user or a log line.
/// <para>
/// A refusal is never inferred from a success either. This generation answers an add it did not
/// understand with a created status and an echo showing the field dropped, so the evidence a monitor
/// took effect is the state a later read reports rather than the status of the write.
/// </para>
/// </remarks>
internal static class MonitoringProjector
{
    /// <summary>Whether the instance holds the entity that was read.</summary>
    internal enum EntityReading
    {
        /// <summary>The instance holds it, and the body is its own record of it.</summary>
        Held,

        /// <summary>The instance does not hold it. Not a refusal.</summary>
        NotHeld,

        /// <summary>The instance answered something else.</summary>
        Refused,
    }

    /// <summary>The capability a monitor of <paramref name="kind"/> is honoured through.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses. A kind resolving to a default
    /// capability would report the wrong generation gap.
    /// </exception>
    internal static WhisparrCapability CapabilityFor(WhisparrEntityKind kind)
        => kind switch
        {
            WhisparrEntityKind.Studio => WhisparrCapability.MonitorStudio,
            WhisparrEntityKind.Performer => WhisparrCapability.MonitorPerformer,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };

    /// <summary>What <paramref name="statusCode"/> says about whether the entity is held.</summary>
    internal static EntityReading Reading(int statusCode)
        => statusCode switch
        {
            200 => EntityReading.Held,
            404 => EntityReading.NotHeld,
            _ => EntityReading.Refused,
        };

    /// <summary>Whether the write <paramref name="statusCode"/> answered was accepted.</summary>
    /// <remarks>
    /// A conflict is never read as "it already exists": the entity is read before the add, so a
    /// conflict here is the instance declining, and the one measured cause of it is a value the add
    /// was composed without.
    /// </remarks>
    internal static MonitorRefusalKind Accepted(int statusCode)
        => statusCode is >= 200 and < 300
            ? MonitorRefusalKind.None
            : MonitorRefusalKind.InstanceRefused;

    /// <summary>Whether the entity <paramref name="body"/> describes is monitored.</summary>
    /// <remarks>
    /// Absent or unreadable reads as not monitored. This answers a browser that paints a state, so an
    /// unreadable answer must read as the state that claims less rather than more.
    /// </remarks>
    internal static bool MonitoredIn(string? body)
        => AsObject(body) is { } entity
            && entity["monitored"] is JsonValue flag
            && flag.TryGetValue<bool>(out var monitored)
            && monitored;

    /// <summary>
    /// The instance's own identifier for the entity <paramref name="body"/> describes, or null when
    /// it names none.
    /// </summary>
    /// <remarks>
    /// This is the instance-side row id, which is the only identifier the editor resource takes. It
    /// exists only for an entity the instance already holds, so an absent one is what a caller must
    /// refuse on rather than substitute a value for.
    /// </remarks>
    internal static int? EntityIdIn(string? body)
        => AsObject(body) is { } entity
            && entity["id"] is JsonValue named
            && named.TryGetValue<int>(out var entityId)
            && entityId > 0
                ? entityId
                : null;

    /// <summary><paramref name="body"/> as an object, or null when it is not one.</summary>
    internal static JsonObject? AsObject(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
