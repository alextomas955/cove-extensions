using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Scene;

/// <summary>What one scene's own tab states, derived from what the instance answered.</summary>
/// <remarks>
/// The state comes off the same row read the catalogue surface uses, so the two surfaces cannot
/// disagree about one scene.
/// </remarks>
internal static class SceneDetailProjector
{
    /// <summary>What <paramref name="scene"/> establishes about the scene it answered for.</summary>
    internal static SceneDetailView Project(WhisparrResponse scene)
    {
        var row = SceneStatusPort.ReadRow(scene);

        return new SceneDetailView(
            SceneRefusalKind.None,
            Excluded: false,
            Present: PresenceIn(row.State),
            Monitored: MonitoringIn(row.State),
            QualityName: null,
            QualityProfileName: null,
            CutoffName: null,
            ProfileReadDidNotComplete: false);
    }

    /// <summary>Whether the instance holds an entry, or that nothing was established.</summary>
    private static bool? PresenceIn(MissingSceneState state)
        => state switch
        {
            MissingSceneState.NotAdded => false,
            MissingSceneState.StatusUnknown => null,
            _ => true,
        };

    /// <summary>Whether the instance is looking for the scene, or that nothing was established.</summary>
    /// <remarks>
    /// An entry the instance holds no record of is not an unmonitored one, so an absence answers
    /// null. The browser's vocabulary tests presence first, and a false would read as a fact about a
    /// scene the instance never named.
    /// </remarks>
    private static bool? MonitoringIn(MissingSceneState state)
        => state switch
        {
            MissingSceneState.Monitored => true,
            MissingSceneState.Unmonitored => false,
            _ => null,
        };
}
