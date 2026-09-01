using System.Globalization;
using System.Text.Json.Nodes;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;

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
    /// The body named an act-list event and no path this product could read. A named refusal rather
    /// than an ignore: an event this product acts on that carries nothing to act on is a shape it
    /// does not understand, and reporting it as ignored would hide that.
    /// </summary>
    NoReadablePath,
}

/// <summary>What one delivery body was read as.</summary>
/// <param name="Outcome">How the body was classified.</param>
/// <param name="EventType">The event type the body named, or null when it named none.</param>
/// <param name="Candidate">
/// The file the body reported, present only on <see cref="WebhookProjectionOutcome.Projected"/>.
/// </param>
internal sealed record WebhookReading(
    WebhookProjectionOutcome Outcome,
    string? EventType,
    ImportCandidate? Candidate);

/// <summary>
/// Reads one inbound delivery body into the one candidate type the ingest core takes.
/// </summary>
/// <remarks>
/// Pure. The body is an anonymous caller's, so nothing here binds it to a record that assumes a
/// shape: every member is read defensively off a <see cref="JsonObject"/> and a member that is
/// absent or of another type reads as absent.
/// <para>
/// Whether to act is decided on the event type. Where to read from is decided on the generation.
/// Those are two different questions and they take two different inputs: the generations carry
/// different key sets for the same event, so a body's own keys would classify a v2 delivery as an
/// unrecognised v3 one.
/// </para>
/// </remarks>
internal static class WebhookProjector
{
    /// <summary>
    /// The event types this product acts on, in the spelling the instance sends.
    /// </summary>
    /// <remarks>
    /// One value, and the same one on both generations. It is the string a delivery really carried,
    /// transcribed by hand from the committed payload fixtures rather than derived from the
    /// trigger-flag name that subscribed to it - those are two vocabularies, and the flag is
    /// <c>onDownload</c> while the body says <c>Download</c>.
    /// <para>
    /// An upgrade arrives as this same event with <c>isUpgrade</c> set, not as an event type of its
    /// own, so there is no second string to list. Every other type an instance sends - a grab, a
    /// rename, every delete variant - is an ignore.
    /// </para>
    /// </remarks>
    internal const string DownloadEventType = "Download";

    /// <summary>The form both generations' inbound user agent takes.</summary>
    private const string UserAgentPrefix = "Whisparr/";

    /// <summary>What <paramref name="body"/> reports, read as <paramref name="generation"/> sends it.</summary>
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

        var file = ObjectAt(body, FileMemberOf(generation));
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
                RemoteIdOf(generation, body)));
    }

    /// <summary>Which generation sent a delivery, or null when its user agent does not say.</summary>
    /// <remarks>
    /// Read from the user agent rather than from the body, because it is what an inbound consumer
    /// sees BEFORE it reads a body, and because it is what decides where in that body to read.
    /// </remarks>
    internal static WhisparrGeneration? GenerationOf(string? userAgent)
    {
        if (userAgent is null || !userAgent.StartsWith(UserAgentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = userAgent[UserAgentPrefix.Length..].Split(' ', 2)[0];
        return GenerationDetector.GenerationOf(version);
    }

    /// <summary>Which member of a delivery body carries the imported file.</summary>
    private static string FileMemberOf(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => "movieFile",
            WhisparrGeneration.V2 => "episodeFile",
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    /// <summary>
    /// The shared remote identifier a delivery carried, or null when it carried none.
    /// </summary>
    /// <remarks>
    /// In a different place on each generation, and of a different JSON type: v3 names the scene's
    /// own identifier as a string beside the entity, and v2 names it as a number on the scene rows
    /// the delivery lists. The first scene's is taken, because a delivery reports one imported file.
    /// </remarks>
    private static string? RemoteIdOf(WhisparrGeneration generation, JsonObject body)
        => generation switch
        {
            WhisparrGeneration.V3 => RemoteIdGuard.Identifying(ValueOf(ObjectAt(body, "movie"), "stashId")),
            WhisparrGeneration.V2 => RemoteIdGuard.Identifying(ValueOf(FirstObjectIn(body, "episodes"), "tvdbId")),
            _ => null,
        };

    private static JsonObject? ObjectAt(JsonObject? parent, string name)
        => parent?[name] as JsonObject;

    private static JsonObject? FirstObjectIn(JsonObject parent, string name)
        => (parent[name] as JsonArray)?.FirstOrDefault() as JsonObject;

    /// <summary>
    /// The value of <paramref name="name"/> rendered as text, or null when it is absent or blank.
    /// </summary>
    /// <remarks>
    /// A number renders as its invariant text, so an identifier carried as a JSON number on one
    /// generation and a JSON string on the other reaches the core in one form.
    /// </remarks>
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
