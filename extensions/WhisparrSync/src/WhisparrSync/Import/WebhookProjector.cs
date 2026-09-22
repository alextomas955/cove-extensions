using System.Globalization;
using System.Text.Json.Nodes;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Import;

/// <summary>How a delivery was classified before anything acted on it.</summary>
public enum WebhookProjectionOutcome
{
    /// <summary>The body named an act-list event and a readable path, and produced a candidate.</summary>
    Projected,

    /// <summary>The body named an event this product does not act on.</summary>
    Ignored,

    /// <summary>The body was not a JSON object, or named no event type at all.</summary>
    Unreadable,

    /// <summary>
    /// The body named an act-list event and no path this product could read. Named apart from an
    /// ignore, because reporting it as ignored would hide a shape this product does not understand.
    /// </summary>
    NoReadablePath,
}

// The candidate is present only on Projected.
internal sealed record WebhookReading(
    WebhookProjectionOutcome Outcome,
    string? EventType,
    ImportCandidate? Candidate);

// Pure. The body is an anonymous caller's, so every member is read defensively and a member that
// is absent or of another type reads as absent.
// Whether to act is decided on the event type, and where to read from on the generation. The
// generations carry different key sets for the same event, so a body's own keys would classify a
// v2 delivery as an unrecognised v3 one.
internal static class WebhookProjector
{
    // The event type both generations send, spelled as the body carries it. The trigger flag that
    // subscribes to it is a separate vocabulary and is spelled onDownload.
    // An upgrade arrives as this same event with isUpgrade set, not as an event type of its own.
    internal const string DownloadEventType = "Download";

    private const string UserAgentPrefix = "Whisparr/";

    internal static WebhookReading Read(WhisparrGeneration generation, JsonObject? body)
    {
        if (body is null || ValueOf(body, "eventType") is not { } eventType)
        {
            return new WebhookReading(WebhookProjectionOutcome.Unreadable, null, null);
        }

        if (!string.Equals(eventType, DownloadEventType, StringComparison.Ordinal))
        {
            return new WebhookReading(WebhookProjectionOutcome.Ignored, eventType, null);
        }

        var reading = WhisparrInstanceFactory.ReadingFor(generation);
        var file = ObjectAt(body, reading.WebhookFileMember);
        if (file is null || ValueOf(file, "path") is not { } path)
        {
            return new WebhookReading(WebhookProjectionOutcome.NoReadablePath, eventType, null);
        }

        return new WebhookReading(
            WebhookProjectionOutcome.Projected,
            eventType,
            new ImportCandidate(
                generation,
                eventType,
                path,
                LongAt(file, "size"),
                reading.WebhookRemoteId(body)));
    }

    // Read from the user agent, not the body: it is available before the body is read, and it
    // decides where in that body to read.
    internal static WhisparrGeneration? GenerationOf(string? userAgent)
    {
        if (userAgent is null || !userAgent.StartsWith(UserAgentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = userAgent[UserAgentPrefix.Length..].Split(' ', 2)[0];
        return GenerationDetector.GenerationOf(version);
    }

    private static JsonObject? ObjectAt(JsonObject? parent, string name)
        => parent?[name] as JsonObject;

    // A member carried as a JSON number renders as its invariant text, so a body spelling one as a
    // number and another as a string reaches the core in one form.
    private static string? ValueOf(JsonObject? parent, string name)
    {
        if (parent?[name] is not JsonValue value)
        {
            return null;
        }

        var rendered = value.TryGetValue<string>(out var text)
            ? text
            : value.ToString();
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
    }

    private static long? LongAt(JsonObject parent, string name)
    {
        if (parent[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }
}
