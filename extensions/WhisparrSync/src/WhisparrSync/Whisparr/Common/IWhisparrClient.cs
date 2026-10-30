using System.Text.Json.Nodes;

namespace WhisparrSync.Whisparr;

/// <summary>The one seam through which this extension talks to one Whisparr instance.</summary>
/// <remarks>
/// Narrow by design: no method takes a caller-supplied path and none takes an HTTP verb, so no call
/// site can express a request that makes Whisparr search for or download anything.
/// <para>
/// An implementation is bound to one instance, so no member takes an address, a key or a generation
/// and a read cannot name a different instance from the write after it. Which instance that is comes
/// from the binding it was obtained for, not from this contract.
/// </para>
/// <para>
/// The verbs that change what an instance monitors are not here. They are the roles in
/// <c>WhisparrSync.Monitoring</c>, and the one verb that can make an instance download is alone on
/// <c>IWhisparrSearchGrabbing</c> there. Each generation's implementation declares only the roles
/// that generation holds, so a role it does not hold is one a caller cannot obtain.
/// </para>
/// <para>
/// The status read is not here either: it is what establishes which generation answered, so it runs
/// before one is known and lives on the transport.
/// </para>
/// </remarks>
public interface IWhisparrClient
{
    /// <summary>Reads the notification schema, which declares what a connection can be told.</summary>
    /// <exception cref="HttpRequestException">The request produced no response.</exception>
    /// <exception cref="IOException">
    /// The response ended before the length it declared. A body is read out of the response stream
    /// rather than buffered inside the send, so a dropped connection raises this rather than
    /// <see cref="HttpRequestException"/>. Both mean no whole answer arrived, so a caller containing
    /// one contains the other.
    /// </exception>
    /// <exception cref="TaskCanceledException">The request outlived the bound on one attempt.</exception>
    Task<WhisparrResponse> ReadNotificationSchemaAsync(CancellationToken ct);

    /// <summary>Reads every notification the instance holds.</summary>
    /// <inheritdoc cref="ReadNotificationSchemaAsync" path="/exception"/>
    Task<WhisparrResponse> ListNotificationsAsync(CancellationToken ct);

    /// <summary>Reads the library roots the instance reports for itself.</summary>
    /// <remarks>
    /// The instance's own root folders are not carried on the import event it sends, so a consumer
    /// resolving a reported file path against its root has no other source for them.
    /// </remarks>
    Task<WhisparrResponse> ReadRootFoldersAsync(CancellationToken ct);

    /// <summary>Reads the quality profiles the instance offers.</summary>
    /// <remarks>
    /// An add cannot be composed without one, and which profiles exist is the instance's own: a
    /// profile id it does not offer is refused by one generation and accepted by the other.
    /// </remarks>
    Task<WhisparrResponse> ReadQualityProfilesAsync(CancellationToken ct);

    /// <summary>
    /// Reads one page of the instance's import history, with each record's own metadata entity.
    /// </summary>
    /// <remarks>
    /// The newest-first order is asked for and not relied on: whether the route honours the request
    /// is unmeasured, so a caller reads the page's own order and refuses one it cannot walk. Pages
    /// count from one. Which metadata entity is embedded is the instance's own; the route and the
    /// order belong to the seam.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="page"/> or <paramref name="pageSize"/> is below one.
    /// </exception>
    Task<WhisparrResponse> ReadHistoryAsync(int page, int pageSize, CancellationToken ct);

    /// <summary>Reads the command <paramref name="commandId"/> names back off the instance.</summary>
    /// <remarks>
    /// Establishes that the instance holds the command and nothing about anything being downloaded.
    /// The identifier comes off the answer to the post rather than from a caller.
    /// </remarks>
    Task<WhisparrResponse> ReadCommandAsync(int commandId, CancellationToken ct);

    /// <summary>Creates one notification.</summary>
    /// <remarks>
    /// Never re-issued on a failure. The instance refuses a duplicate name, but that refusal cannot
    /// be told from a real one, so no second attempt is made.
    /// </remarks>
    Task<WhisparrResponse> CreateNotificationAsync(JsonNode body, CancellationToken ct);

    /// <summary>Replaces the notification with <paramref name="id"/>.</summary>
    /// <inheritdoc cref="CreateNotificationAsync" path="/remarks"/>
    Task<WhisparrResponse> UpdateNotificationAsync(int id, JsonNode body, CancellationToken ct);
}
