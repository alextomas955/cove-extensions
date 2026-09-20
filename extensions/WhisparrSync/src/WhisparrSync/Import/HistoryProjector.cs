using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;

namespace WhisparrSync.Import;

internal enum HistoryProjectionOutcome
{
    Projected,

    Ignored,

    NoReadablePath,
}

internal sealed record HistoryReading(
    HistoryProjectionOutcome Outcome, string? EventType, ImportCandidate? Candidate);

// Pure. Every member is read defensively off the JSON and an absent member, or one of another type,
// reads as absent: one generation publishes no contract, so the shape comes from parsing.
// The history surface spells event types in camelCase where the webhook surface spells the same
// events in PascalCase. The two vocabularies are separate.
internal static class HistoryProjector
{
    // The history route's spelling. The webhook surface calls the same event Download.
    internal const string ImportedEventType = "downloadFolderImported";

    private const string RecordsMember = "records";

    // Null when the body is not a paged answer. The caller refuses on that rather than reading it
    // as an empty page, because an empty page ends a walk.
    internal static JsonArray? RecordsIn(string body)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        return (parsed as JsonObject)?[RecordsMember] as JsonArray;
    }

    // Each record's instant in page order, or null when one cannot be read. A page with an undated
    // record is refused whole: a walk that stops at a stored instant cannot place it, and guessing
    // its position makes the walk stop early or replay. The list is one page long.
    internal static IReadOnlyList<DateTimeOffset>? InstantsIn(JsonArray records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var instants = new List<DateTimeOffset>(records.Count);
        foreach (var record in records)
        {
            if (InstantOf(record as JsonObject) is not { } instant)
            {
                return null;
            }

            instants.Add(instant);
        }

        return instants;
    }

    // Each record's identifier in page order, read as text because the walk only compares one
    // against another and the rendered type is not documented. Null when a record carries none,
    // which places that page by its instants instead; an absent identifier is not a refusal. The
    // list is one page long.
    internal static IReadOnlyList<string>? IdsIn(JsonArray records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var ids = new List<string>(records.Count);
        foreach (var record in records)
        {
            if (ValueOf(record as JsonObject, "id") is not { } id)
            {
                return null;
            }

            ids.Add(id);
        }

        return ids;
    }

    internal static HistoryReading Read(WhisparrGeneration generation, JsonObject? record)
    {
        if (record is null || ValueOf(record, "eventType") is not { } eventType)
        {
            return new HistoryReading(HistoryProjectionOutcome.Ignored, null, null);
        }

        if (!string.Equals(eventType, ImportedEventType, StringComparison.Ordinal))
        {
            return new HistoryReading(HistoryProjectionOutcome.Ignored, eventType, null);
        }

        if (ValueOf(record["data"] as JsonObject, "importedPath") is not { } path)
        {
            return new HistoryReading(HistoryProjectionOutcome.NoReadablePath, eventType, null);
        }

        // No size: a history record has not been shown to carry one, and a guessed member name
        // reads as absent either way.
        return new HistoryReading(
            HistoryProjectionOutcome.Projected,
            eventType,
            new ImportCandidate(generation, eventType, path, null, RemoteIdOf(generation, record)));
    }

    // Each generation names its own entity and identifier member, matching what the live channel
    // reads for that generation, so an arrival through either channel is the same scene. A record
    // with no embedded entity yields no identifier and is imported without one.
    private static string? RemoteIdOf(WhisparrGeneration generation, JsonObject record)
        => generation switch
        {
            WhisparrGeneration.V3 => IdentifierOn(record, "movie", "stashId"),
            WhisparrGeneration.V2 => IdentifierOn(record, "episode", "tvdbId"),
            _ => null,
        };

    private static string? IdentifierOn(JsonObject record, string entity, string member)
        => RemoteIdGuard.Identifying(ValueOf(record[entity] as JsonObject, member));

    private static DateTimeOffset? InstantOf(JsonObject? record)
        => ValueOf(record, "date") is { } rendered
            && DateTimeOffset.TryParse(
                rendered,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var instant)
                ? instant
                : null;

    private static string? ValueOf(JsonObject? parent, string name)
    {
        if (parent?[name] is not JsonValue value)
        {
            return null;
        }

        var rendered = value.TryGetValue<string>(out var text) ? text : value.ToString();
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
    }
}
