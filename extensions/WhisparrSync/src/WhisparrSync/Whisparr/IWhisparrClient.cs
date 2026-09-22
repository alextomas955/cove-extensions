using System.Text.Json.Nodes;
using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

/// <summary>
/// The one seam through which this extension talks to a Whisparr instance.
/// </summary>
/// <remarks>
/// Narrow by design: no method takes a caller-supplied path and none takes an HTTP verb, so no call
/// site can express a request that makes Whisparr search for or download anything.
/// <para>
/// The verbs that change what an instance monitors are not here. They are the roles in
/// <c>WhisparrSync.Monitoring</c>, and the one verb that can make an instance download is alone on
/// <c>IWhisparrSearchGrabbing</c> there.
/// </para>
/// </remarks>
public interface IWhisparrClient
{
    /// <summary>Reads the status document from the instance at <paramref name="baseAddress"/>.</summary>
    /// <remarks>
    /// Returns whatever the instance answered, including a non-success status: classifying the answer
    /// belongs to the caller. Throws only when no answer arrived at all.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="baseAddress"/> is relative, or its scheme is neither http nor https.
    /// </exception>
    /// <exception cref="HttpRequestException">The request produced no response.</exception>
    /// <exception cref="IOException">
    /// The response ended before the length it declared. The body is read out of the response stream
    /// rather than buffered inside the send, so a dropped connection raises this rather than
    /// <see cref="HttpRequestException"/>. Both mean no whole answer arrived, so a caller containing
    /// one contains the other.
    /// </exception>
    /// <exception cref="TaskCanceledException">The request outlived the client's timeout.</exception>
    Task<WhisparrResponse> ReadStatusAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Reads the notification schema, which declares what a connection can be told.</summary>
    Task<WhisparrResponse> ReadNotificationSchemaAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Reads every notification the instance holds.</summary>
    Task<WhisparrResponse> ListNotificationsAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Reads the library roots the instance reports for itself.</summary>
    /// <remarks>
    /// The instance's own root folders are not carried on the import event it sends, so a consumer
    /// resolving a reported file path against its root has no other source for them.
    /// </remarks>
    Task<WhisparrResponse> ReadRootFoldersAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Reads the quality profiles the instance offers.</summary>
    /// <remarks>
    /// An add cannot be composed without one, and which profiles exist is the instance's own: a
    /// profile id it does not offer is refused by one generation and accepted by the other.
    /// </remarks>
    Task<WhisparrResponse> ReadQualityProfilesAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>
    /// Reads one page of the instance's import history, with each record's own metadata entity.
    /// </summary>
    /// <remarks>
    /// The newest-first order is asked for and not relied on: whether the route honours the request
    /// is unmeasured, so a caller reads the page's own order and refuses one it cannot walk. Pages
    /// count from one. The generation decides which metadata entity is embedded; the route and the
    /// order belong to the seam.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="page"/> or <paramref name="pageSize"/> is below one, or
    /// <paramref name="generation"/> is not a generation this reads.
    /// </exception>
    Task<WhisparrResponse> ReadHistoryAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>Reads the command <paramref name="commandId"/> names back off the instance.</summary>
    /// <remarks>
    /// Establishes that the instance holds the command and nothing about anything being downloaded.
    /// The identifier comes off the answer to the post rather than from a caller.
    /// </remarks>
    Task<WhisparrResponse> ReadCommandAsync(
        Uri baseAddress, string apiKey, int commandId, CancellationToken ct);

    /// <summary>Creates one notification.</summary>
    /// <remarks>
    /// Never re-issued on a failure. The instance refuses a duplicate name, but that refusal cannot
    /// be told from a real one, so no second attempt is made.
    /// </remarks>
    Task<WhisparrResponse> CreateNotificationAsync(
        Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct);

    /// <summary>Replaces the notification with <paramref name="id"/>.</summary>
    /// <inheritdoc cref="CreateNotificationAsync" path="/remarks"/>
    Task<WhisparrResponse> UpdateNotificationAsync(
        Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct);
}
