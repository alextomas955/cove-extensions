using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

// Presence is read from the status and the monitored flag from the body. A refused add is
// classified from the status alone: the refusal body carries a full .NET stack trace, and a field
// nothing reads cannot reach a user or a log line.
//
// A refusal is never inferred from a success. An add the instance did not understand answers a
// created status and an echo with the field dropped, so the evidence a monitor took effect is what
// a later read reports, not the status of the write.
internal static class MonitoringProjector
{
    internal readonly record struct MonitorReasons(
        bool NoConnectionConfigured,
        bool CapabilityAbsentOnThisGeneration,
        MonitorRefusalKind IdentityRefusal);

    // Several reasons hold at once often, and a user reads one sentence, so the precedence is
    // decided here and nowhere else. Nothing configured first: with no instance there is no
    // generation to compare and no screen the metadata link would send the reader to. Then the
    // generation gap, which no identifier changes. Then the metadata link, the narrowest reason and
    // the only one actionable from the entity's own page.
    //
    // A reason the caller has not observed is passed as none, which is safe under this order: a
    // later reason goes unobserved only when an earlier one already holds.
    internal static MonitorRefusalKind FirstRefusal(MonitorReasons reasons)
        => reasons switch
        {
            { NoConnectionConfigured: true } => MonitorRefusalKind.NotConfigured,
            { CapabilityAbsentOnThisGeneration: true }
                => MonitorRefusalKind.CapabilityAbsentOnThisGeneration,
            _ => reasons.IdentityRefusal,
        };

    internal enum EntityReading
    {
        Held,

        NotHeld,

        Refused,
    }

    internal readonly record struct EntityAnswer(EntityReading Reading, MonitorRefusalKind Refusal);

    // A refusal the answering seam established wins over the status. On v2 a site nothing could be
    // numbered for is refused before any request leaves, so the answer carries no status about the
    // entity and reading one would report the wrong reason.
    internal static EntityAnswer Classify(WhisparrResponse answered)
    {
        ArgumentNullException.ThrowIfNull(answered);

        if (answered.Refusal is not MonitorRefusalKind.None)
        {
            return new EntityAnswer(EntityReading.Refused, answered.Refusal);
        }

        var reading = answered.StatusCode switch
        {
            200 => EntityReading.Held,
            404 => EntityReading.NotHeld,
            _ => EntityReading.Refused,
        };

        return new EntityAnswer(
            reading,
            reading == EntityReading.Refused
                ? MonitorRefusalKind.InstanceRefused
                : MonitorRefusalKind.None);
    }

    // Null in either member means the answer established neither.
    internal readonly record struct EntityPresence(bool? Present, bool? Monitored);

    // The single-entity read and the card batch both go through here, so they cannot disagree about
    // the same instance answer. Not held answers a false flag, not a null one: the instance was
    // asked and holds no entry, which is established rather than unknown.
    internal static EntityPresence PresenceOf(EntityReading reading, string? body)
        => reading switch
        {
            EntityReading.Held => new EntityPresence(true, MonitoredIn(body)),
            EntityReading.NotHeld => new EntityPresence(false, false),
            _ => new EntityPresence(null, null),
        };

    // A refusal the answering seam established wins, for the reason Classify states.
    internal static MonitorRefusalKind Accepted(WhisparrResponse answered)
    {
        ArgumentNullException.ThrowIfNull(answered);

        return answered.Refusal is MonitorRefusalKind.None
            ? AcceptedStatus(answered.StatusCode)
            : answered.Refusal;
    }

    // A conflict is never read as "it already exists": the entity is read before the add, so a
    // conflict here is the instance declining, and the one measured cause is a value the add was
    // composed without.
    internal static MonitorRefusalKind AcceptedStatus(int statusCode)
        => statusCode is >= 200 and < 300
            ? MonitorRefusalKind.None
            : MonitorRefusalKind.InstanceRefused;

    // Absent or unreadable reads as not monitored: this paints a state in a browser, so an
    // unreadable answer claims less rather than more.
    internal static bool MonitoredIn(string? body)
        => AsObject(body) is { } entity
            && entity["monitored"] is JsonValue flag
            && flag.TryGetValue<bool>(out var monitored)
            && monitored;

    // The instance-side row id, the only identifier the editor resource takes. It exists only for
    // an entity the instance holds, so a caller refuses on an absent one rather than substituting.
    internal static int? EntityIdIn(string? body)
        => AsObject(body) is { } entity
            && entity["id"] is JsonValue named
            && named.TryGetValue<int>(out var entityId)
            && entityId > 0
                ? entityId
                : null;

    // Null means the answer named no root, which says nothing about where the instance has the
    // entity. A caller leaves it alone rather than correcting a root read from nothing.
    internal static string? RootFolderPathIn(string? body)
        => AsObject(body) is { } entity
            && entity["rootFolderPath"] is JsonValue named
            && named.TryGetValue<string>(out var root)
            && !string.IsNullOrWhiteSpace(root)
                ? root
                : null;

    // Null is distinct from zero. Zero is the instance stating it has linked no file, which a
    // caller acts on by asking for the catalogue to be read again; null is no count at all, which a
    // caller sends nothing on.
    internal static int? FileCountIn(string? body)
        => AsObject(body) is { } entity
            && entity["statistics"] is JsonObject statistics
            && statistics["episodeFileCount"] is JsonValue counted
            && counted.TryGetValue<int>(out var files)
            && files >= 0
                ? files
                : null;

    // The date gate's presence is the whole reading. Its value is never read and never compared
    // against a clock: what a scope covers on either side of that date is the instance's to decide.
    // An absent gate is the wider scope on this generation, which MonitorBodyPinTests transcribes.
    //
    // Null is not a scope. It says which one is in force is unknown, so a caller paints no state
    // rather than a default. The gate exists on v3's studio resource only, so a performer, v2 and
    // an unmonitored entity all answer null.
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
