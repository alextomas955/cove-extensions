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
/// </remarks>
internal static class SiteRegistrationStep
{
    /// <summary>Registers <paramref name="site"/> unless <paramref name="readSite"/> says it is held.</summary>
    /// <param name="readSite">Reads what the instance holds for one site, by its identifier.</param>
    /// <param name="registerSite">Registers one site, monitoring nothing.</param>
    /// <param name="site">The site, carrying Cove's own id beside the identifier.</param>
    /// <param name="ct">Cancels the step.</param>
    internal static async Task<SyncRegistration> RegisterAsync(
        Func<string, CancellationToken, Task<WhisparrResponse?>> readSite,
        Func<string, CancellationToken, Task<WhisparrResponse?>> registerSite,
        LibrarySiteIdentity site,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readSite);
        ArgumentNullException.ThrowIfNull(registerSite);
        ArgumentNullException.ThrowIfNull(site);

        var held = await readSite(site.RemoteId, ct).ConfigureAwait(false);
        var reading = held is null
            ? MonitoringProjector.EntityReading.Refused
            : MonitoringProjector.Classify(held).Reading;

        switch (reading)
        {
            case MonitoringProjector.EntityReading.Held:
                return new SyncRegistration(
                    SceneRegistration.AlreadyHeld, held, MonitoringProjector.EntityIdIn(held!.Body));
            case MonitoringProjector.EntityReading.NotHeld:
                return SyncRegistration.Offered(
                    await registerSite(site.RemoteId, ct).ConfigureAwait(false));
            default:
                return new SyncRegistration(SceneRegistration.Refused, held, null);
        }
    }
}
