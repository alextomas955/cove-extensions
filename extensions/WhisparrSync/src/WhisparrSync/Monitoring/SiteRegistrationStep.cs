using WhisparrSync.Library;
using WhisparrSync.Linking;
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
        Func<int, string, string?, CancellationToken, Task<WhisparrResponse?>>? relocate,
        Func<int, CancellationToken, Task<WhisparrResponse?>> refreshSiteCatalogue,
        EntityPlacement agreed,
        LibrarySiteIdentity site,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agreed);
        ArgumentNullException.ThrowIfNull(readSite);
        ArgumentNullException.ThrowIfNull(registerSite);
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
                    relocate, refreshSiteCatalogue, agreed, held!, ct)
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
        Func<int, string, string?, CancellationToken, Task<WhisparrResponse?>>? relocate,
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

        if (siteId is not { } instanceId)
        {
            return alreadyThere;
        }

        var relocated = await TreeRelocationStep.RelocateAsync(
            heldAt,
            agreed,
            relocate is null
                ? null
                : (root, folder, moveCt) => relocate(instanceId, root, folder, moveCt),
            ct).ConfigureAwait(false);

        switch (relocated.Act)
        {
            case RelocationAct.AlreadyThere:
                // A site at the agreed root with no file linked is what a move whose catalogue
                // re-read never arrived leaves behind. The root reads as correct from then on, so
                // the re-read has to be reachable without moving the site again. A site is
                // registered only when it owns files under an agreed root, so a stated zero is an
                // unread catalogue.
                if (MonitoringProjector.FileCountIn(held.Body) is 0)
                {
                    await refreshSiteCatalogue(instanceId, ct).ConfigureAwait(false);
                }

                return alreadyThere;
            case RelocationAct.Moved:
                return new SyncRegistration(SceneRegistration.Moved, relocated.Answer, siteId);

            // A move the instance declined is a failure a reader acts on, not a site left already
            // held: the site is still registered where none of its files sit.
            case RelocationAct.Declined:
                return new SyncRegistration(SceneRegistration.Refused, relocated.Answer, siteId);
            default:
                return alreadyThere;
        }
    }
}
