using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Push;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Builds a <see cref="SceneActions"/> over a capability port reading the same faked transport the actions
/// use, so a fixture standing in for a v3 instance answers the API-description read like one.
/// </summary>
/// <remarks>
/// A test about capability ABSENCE builds its own port instead of taking this, and a fixture that does not
/// answer the description read gets the fail-closed answer — which is the correct outcome, not a harness gap.
/// </remarks>
internal static class SceneActionsFactory
{
    public static SceneActions Build(
        WhisparrClient client, WhisparrOptions options, ICoveLibraryPort library,
        WhisparrCapabilityPort? capability = null)
        => new(client, options, library, capability ?? new WhisparrCapabilityPort(client));

    /// <summary>
    /// A capability port over the SAME memo shape the extension supplies at its construction sites, so a test
    /// asserting how often the API description is read measures the shipped arrangement rather than the
    /// harness's own. A port built without one re-reads per call, which is the documented behaviour.
    /// </summary>
    public static WhisparrCapabilityPort MemoizedPort(WhisparrClient client)
    {
        var memo = new TtlCache<WhisparrCapabilities>(TimeSpan.FromSeconds(30));
        return new WhisparrCapabilityPort(client, (key, fetch, ct) => memo.GetAsync(key, fetch, ct));
    }
}
