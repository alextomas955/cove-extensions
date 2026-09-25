using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Library;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

// Presence is the instance's own answer about that one site, never computed from a listing, so a
// second pass creates no duplicate. An answer that stated neither presence nor absence is refused
// rather than registered, which would add a site the instance may already hold. The instance's id
// comes off that same answer. With no agreed root nothing is sent: nothing here knows where the
// site belongs.
internal static class SiteRegistrationStep
{
    internal static async Task<SyncRegistration> RegisterAsync(
        Func<string, CancellationToken, Task<WhisparrResponse?>> readSite,
        Func<string, CancellationToken, Task<WhisparrResponse?>> registerSite,
        Func<int, string, string?, CancellationToken, Task<WhisparrResponse?>> moveSiteRoot,
        Func<int, CancellationToken, Task<WhisparrResponse?>> refreshSiteCatalogue,
        EntityPlacement agreed,
        LibrarySiteIdentity site,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agreed);
        ArgumentNullException.ThrowIfNull(readSite);
        ArgumentNullException.ThrowIfNull(registerSite);
        ArgumentNullException.ThrowIfNull(moveSiteRoot);
        ArgumentNullException.ThrowIfNull(refreshSiteCatalogue);
        ArgumentNullException.ThrowIfNull(site);

        var held = await readSite(site.RemoteId, ct).ConfigureAwait(false);
        var reading = held is null
            ? MonitoringProjector.EntityReading.Refused
            : MonitoringProjector.Classify(held).Reading;

        switch (reading)
        {
            case MonitoringProjector.EntityReading.Held:
                return await HeldAsync(
                    moveSiteRoot, refreshSiteCatalogue, agreed, held!, ct)
                    .ConfigureAwait(false);
            case MonitoringProjector.EntityReading.NotHeld:
                return SyncRegistration.Offered(
                    await registerSite(site.RemoteId, ct).ConfigureAwait(false));
            default:
                return new SyncRegistration(SceneRegistration.Refused, held, null);
        }
    }

    // Where a folder was composed, it is the folder the site is compared against and moved to, not
    // the root: two sites under one root are both at that root and only their folders differ, so a
    // root comparison would read a site still at another entity's folder as correctly placed.
    private static async Task<SyncRegistration> HeldAsync(
        Func<int, string, string?, CancellationToken, Task<WhisparrResponse?>> moveSiteRoot,
        Func<int, CancellationToken, Task<WhisparrResponse?>> refreshSiteCatalogue,
        EntityPlacement agreed,
        WhisparrResponse held,
        CancellationToken ct)
    {
        var siteId = MonitoringProjector.EntityIdIn(held.Body);
        var heldAt = agreed.EntityFolderPath is null
            ? MonitoringProjector.RootFolderPathIn(held.Body)
            : MonitoringProjector.PathIn(held.Body);
        var alreadyThere = new SyncRegistration(SceneRegistration.AlreadyHeld, held, siteId);

        if (siteId is not { } instanceId
            || agreed.RootFolderPath is not { } agreedRoot
            || heldAt is not { } registeredAt)
        {
            return alreadyThere;
        }

        if (SamePlace(registeredAt, agreed.EntityFolderPath ?? agreedRoot))
        {
            // A site at the agreed root with no file linked is what a move whose catalogue re-read
            // never arrived leaves behind. The root reads as correct from then on, so the re-read
            // has to be reachable without moving the site again. A site is registered only when it
            // owns files under an agreed root, so a stated zero is an unread catalogue.
            if (MonitoringProjector.FileCountIn(held.Body) is 0)
            {
                await refreshSiteCatalogue(instanceId, ct).ConfigureAwait(false);
            }

            return alreadyThere;
        }

        var moved = await moveSiteRoot(
            instanceId, agreedRoot, agreed.EntityFolderPath, ct).ConfigureAwait(false);

        // A move the instance declined is a failure a reader acts on, not a site left already held:
        // the site is still registered where none of its files sit.
        return moved is not null
            && MonitoringProjector.Accepted(moved) is MonitorRefusalKind.None
                ? new SyncRegistration(SceneRegistration.Moved, moved, siteId)
                : new SyncRegistration(SceneRegistration.Refused, moved, siteId);
    }

    // The intended path is built on the agreed root, which reaches here through the addressing
    // port, and that port spells every candidate with forward slashes and verifies it against the
    // instance's own listing without regard to case. The instance answers its own verbatim
    // spelling. Compared literally, a Windows instance holding D:\Media never matches the agreed
    // D:/Media, so every held site is moved again on every run and the correction never converges.
    private static bool SamePlace(string registeredAt, string intended)
        => string.Equals(
            PathCandidateGuard.Normalize(registeredAt),
            PathCandidateGuard.Normalize(intended),
            StringComparison.OrdinalIgnoreCase);
}
