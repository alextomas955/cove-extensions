using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Library;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

/// <summary>One site's own step in the site pass: ask whether it is held, then register it once.</summary>
/// <remarks>
/// Pure. It drives the two delegates it is given and performs no I/O of its own.
/// <para>
/// Whether the instance holds the site is the instance's own answer about that one site, never
/// computed from a listing of what it holds. That is what makes a second pass over the same library
/// create no duplicate: a site already there is counted as already there and no add is composed for
/// it at all.
/// </para>
/// <para>
/// A read that answered neither presence nor absence is refused rather than registered. Registering
/// on an answer nothing could be read out of would add a site the instance may already hold, and the
/// generation this runs on publishes no contract for what that answer then is.
/// </para>
/// <para>
/// The instance's own id for the site comes off whichever answer the step already read - the held
/// row's where it was already there, the add's own where it was added - so nothing re-reads the site
/// to learn an id the instance has just stated.
/// </para>
/// <para>
/// A site already there at a root other than the agreed one is moved rather than added again. The
/// decision sits on the same per-site read, so correcting a root costs no listing of what the
/// instance holds and a second pass over a corrected library sends nothing.
/// </para>
/// <para>
/// With no agreed root nothing is sent and the site is left where it is. An entity owning no file
/// and an entity whose library root the instance agreed no spelling for are both states in which
/// this product knows nothing about where the site belongs, and moving it would be a guess written
/// to a live instance.
/// </para>
/// </remarks>
internal static class SiteRegistrationStep
{
    /// <summary>Registers <paramref name="site"/> unless <paramref name="readSite"/> says it is held.</summary>
    /// <param name="readSite">Reads what the instance holds for one site, by its identifier.</param>
    /// <param name="registerSite">Registers one site, monitoring nothing.</param>
    /// <param name="moveSiteRoot">
    /// Moves one site the instance already holds to another root, moving no file.
    /// </param>
    /// <param name="agreedRoot">
    /// The instance root this site's own files agree on, or null where there is none.
    /// </param>
    /// <param name="site">The site, carrying Cove's own id beside the identifier.</param>
    /// <param name="ct">Cancels the step.</param>
    internal static async Task<SyncRegistration> RegisterAsync(
        Func<string, CancellationToken, Task<WhisparrResponse?>> readSite,
        Func<string, CancellationToken, Task<WhisparrResponse?>> registerSite,
        Func<int, string, CancellationToken, Task<WhisparrResponse?>> moveSiteRoot,
        string? agreedRoot,
        LibrarySiteIdentity site,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readSite);
        ArgumentNullException.ThrowIfNull(registerSite);
        ArgumentNullException.ThrowIfNull(moveSiteRoot);
        ArgumentNullException.ThrowIfNull(site);

        var held = await readSite(site.RemoteId, ct).ConfigureAwait(false);
        var reading = held is null
            ? MonitoringProjector.EntityReading.Refused
            : MonitoringProjector.Classify(held).Reading;

        switch (reading)
        {
            case MonitoringProjector.EntityReading.Held:
                return await HeldAsync(moveSiteRoot, agreedRoot, held!, ct).ConfigureAwait(false);
            case MonitoringProjector.EntityReading.NotHeld:
                return SyncRegistration.Offered(
                    await registerSite(site.RemoteId, ct).ConfigureAwait(false));
            default:
                return new SyncRegistration(SceneRegistration.Refused, held, null);
        }
    }

    private static async Task<SyncRegistration> HeldAsync(
        Func<int, string, CancellationToken, Task<WhisparrResponse?>> moveSiteRoot,
        string? agreedRoot,
        WhisparrResponse held,
        CancellationToken ct)
    {
        var siteId = MonitoringProjector.EntityIdIn(held.Body);
        var heldRoot = MonitoringProjector.RootFolderPathIn(held.Body);
        var alreadyThere = new SyncRegistration(SceneRegistration.AlreadyHeld, held, siteId);

        if (siteId is not { } instanceId
            || agreedRoot is not { } agreed
            || heldRoot is not { } registeredAt
            || SameRoot(registeredAt, agreed))
        {
            return alreadyThere;
        }

        var moved = await moveSiteRoot(instanceId, agreed, ct).ConfigureAwait(false);

        // A move the instance declined is a failure a reader acts on, not a site left already held:
        // the site is still registered where none of its files sit.
        return moved is not null
            && MonitoringProjector.Accepted(moved) is MonitorRefusalKind.None
                ? new SyncRegistration(SceneRegistration.Moved, moved, siteId)
                : new SyncRegistration(SceneRegistration.Refused, moved, siteId);
    }

    // The agreed root reaches here through the addressing port, which spells every candidate with
    // forward slashes and verifies it against the instance's own listing without regard to case.
    // The instance answers its own verbatim spelling. Compared literally, a Windows instance
    // holding D:\Media never matches the agreed D:/Media, so every held site is moved again on
    // every run and the correction never converges.
    private static bool SameRoot(string registeredAt, string agreed)
        => string.Equals(
            PathCandidateGuard.Normalize(registeredAt),
            PathCandidateGuard.Normalize(agreed),
            StringComparison.OrdinalIgnoreCase);
}
