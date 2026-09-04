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
    /// <summary>Which of the reasons an entity may not be monitorable were observed.</summary>
    /// <remarks>
    /// The identity slot carries a kind rather than a flag, because the library can fail to name an
    /// entity in more than one way and each is a different sentence.
    /// </remarks>
    /// <param name="NoConnectionConfigured">No instance is configured at all.</param>
    /// <param name="CapabilityAbsentOnThisGeneration">
    /// The connected generation holds no capability that could honour this.
    /// </param>
    /// <param name="IdentityRefusal">
    /// Why the library names no single entity in this generation's namespace, or
    /// <see cref="MonitorRefusalKind.None"/>.
    /// </param>
    internal readonly record struct MonitorReasons(
        bool NoConnectionConfigured,
        bool CapabilityAbsentOnThisGeneration,
        MonitorRefusalKind IdentityRefusal);

    /// <summary>The one refusal to answer when more than one reason holds.</summary>
    /// <remarks>
    /// More than one reason holds often: an entity with no metadata link, on the older generation,
    /// with nothing configured, has all three. A user reads ONE sentence, so which reason wins is a
    /// decision rather than an accident of the order the reads happen in, and it is stated here and
    /// nowhere else.
    /// <para>
    /// The order, and why each reason sits where it does:
    /// </para>
    /// <para>
    /// 1. Nothing configured. Nothing else is knowable: with no instance there is no generation, so
    /// the generation gap cannot even be evaluated, and whether the entity's metadata link matches is
    /// undecided. Naming the metadata link when the real problem is that no instance is configured
    /// sends the reader to the wrong screen.
    /// </para>
    /// <para>
    /// 2. The generation gap. The connected generation cannot honour this at all, so whether the
    /// entity carries a matching identifier makes no difference to the answer.
    /// </para>
    /// <para>
    /// 3. The metadata link. The narrowest reason, and the only one the reader can act on from the
    /// page in front of them, because both detail pages already show the provider link chips.
    /// </para>
    /// <para>
    /// A reason the caller has not observed is passed as none. That is safe BECAUSE of this order: a
    /// later reason goes unobserved only when an earlier one already holds, and an earlier one wins
    /// either way.
    /// </para>
    /// </remarks>
    internal static MonitorRefusalKind FirstRefusal(MonitorReasons reasons)
        => reasons switch
        {
            { NoConnectionConfigured: true } => MonitorRefusalKind.NotConfigured,
            { CapabilityAbsentOnThisGeneration: true }
                => MonitorRefusalKind.CapabilityAbsentOnThisGeneration,
            _ => reasons.IdentityRefusal,
        };

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

    /// <summary>
    /// Which scope the entity <paramref name="body"/> describes is monitored at, or null where the
    /// product cannot say.
    /// </summary>
    /// <remarks>
    /// The date gate's PRESENCE is the whole reading. Its value is never read, never compared
    /// against a clock and never reported: what a scope covers on either side of that date is the
    /// instance's to decide, and this product states nothing about it.
    /// <para>
    /// Null is not a scope. It says the product does not know which one is in force, so a caller
    /// painting a state must paint none rather than fall back to a default.
    /// </para>
    /// <para>
    /// Only the newer generation's studio read is read at all. The gate exists on that one resource
    /// and on no other, so a performer expresses no scope, and the older generation's own read was
    /// never measured carrying one. Both answer null, as does an unmonitored entity, which has no
    /// scope in force to report.
    /// </para>
    /// <para>
    /// The absent gate is what the wider scope looks like on this generation, so an absent member
    /// answers <see cref="MonitorScope.AllScenes"/> rather than null. That reading is licensed by
    /// the transcribed read in <c>MonitorBodyPinTests</c>, which is where a re-measurement goes red
    /// if the generation starts answering the member some other way.
    /// </para>
    /// </remarks>
    internal static MonitorScope? ScopeIn(
        WhisparrEntityKind kind, WhisparrGeneration generation, bool monitored, string? body)
    {
        if (kind != WhisparrEntityKind.Studio
            || generation != WhisparrGeneration.V3
            || !monitored
            || AsObject(body) is not { } entity)
        {
            return null;
        }

        return entity["afterDate"] is null ? MonitorScope.AllScenes : MonitorScope.FutureScenes;
    }

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
