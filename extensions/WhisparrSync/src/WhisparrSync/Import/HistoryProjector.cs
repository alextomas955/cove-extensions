using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;

namespace WhisparrSync.Import;

/// <summary>How one history record was classified before anything acted on it.</summary>
internal enum HistoryProjectionOutcome
{
    /// <summary>The record named an import and a readable path, and produced a candidate.</summary>
    Projected,

    /// <summary>The record named an event this product does not act on.</summary>
    Ignored,

    /// <summary>The record named an import and no path this product could read.</summary>
    NoReadablePath,
}

/// <summary>What one history record was read as.</summary>
/// <param name="Outcome">How the record was classified.</param>
/// <param name="EventType">The event type the record named, or null when it named none.</param>
/// <param name="Candidate">
/// The file the record reports, present only on <see cref="HistoryProjectionOutcome.Projected"/>.
/// </param>
internal sealed record HistoryReading(
    HistoryProjectionOutcome Outcome, string? EventType, ImportCandidate? Candidate);

/// <summary>
/// Reads a page of import history into the one candidate type the ingest core takes.
/// </summary>
/// <remarks>
/// Pure. Every member is read defensively off a <see cref="JsonObject"/> and a member that is absent
/// or of another type reads as absent: one generation publishes no contract, so an answer's shape is
/// established by parsing it rather than by binding it to a record.
/// <para>
/// The history surface spells its event types in camelCase where the webhook surface spells the same
/// events in PascalCase. The two vocabularies are separate, and this one is named here rather than
/// shared with the webhook's.
/// </para>
/// </remarks>
internal static class HistoryProjector
{
    /// <summary>
    /// The event type this product acts on, in the spelling the history route renders.
    /// </summary>
    /// <remarks>
    /// Transcribed by hand from an instance's own history answer. The webhook surface calls the same
    /// event <c>Download</c>.
    /// </remarks>
    internal const string ImportedEventType = "downloadFolderImported";

    /// <summary>The member of the paged envelope carrying the records.</summary>
    private const string RecordsMember = "records";

    /// <summary>The records one paged answer carries, or null when the answer was not one.</summary>
    /// <remarks>
    /// A body that is not an object carrying an array under its records member yields null, which the
    /// caller refuses on rather than reading as an empty page: an empty page ends a walk, and an
    /// answer nobody could read is not an end.
    /// </remarks>
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

    /// <summary>
    /// Each record's instant, in the order the page listed them, or null when one has none readable.
    /// </summary>
    /// <remarks>
    /// A page carrying a record whose instant cannot be read is refused whole: a walk that stops at a
    /// stored instant cannot place a record it cannot date, and guessing its position is how a walk
    /// stops early or replays.
    /// <para>
    /// The list is one page long, which the caller fixes, so it is bounded however much history the
    /// walk goes on to read.
    /// </para>
    /// </remarks>
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

    /// <summary>What <paramref name="record"/> reports, read as <paramref name="generation"/> renders it.</summary>
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

        // No size and no identifier. A history record has never been shown to carry either, and a
        // member read on the strength of a guess reads as absent whether or not the guess was right.
        return new HistoryReading(
            HistoryProjectionOutcome.Projected,
            eventType,
            new ImportCandidate(generation, eventType, path, null, null));
    }

    /// <summary>When <paramref name="record"/> says it happened, or null when it does not say.</summary>
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
