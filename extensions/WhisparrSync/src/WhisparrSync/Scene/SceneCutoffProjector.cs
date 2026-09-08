using WhisparrSync.Whisparr;

namespace WhisparrSync.Scene;

/// <summary>The two names one scene's quality profile answers for.</summary>
/// <param name="ProfileName">The profile's own name, or null where none was established.</param>
/// <param name="CutoffName">
/// The name the profile resolves its own cutoff to, or null where it resolves it to nothing.
/// </param>
internal readonly record struct SceneProfileNames(string? ProfileName, string? CutoffName);

/// <summary>What one scene's quality profile is called, and where it stops taking better files.</summary>
/// <remarks>
/// The cutoff is an id on the profile and not a name, and not a member of the scene at all, so it
/// resolves against the profile's own items or it resolves to nothing. An id there names either one
/// quality or a whole group of them.
/// </remarks>
internal static class SceneCutoffProjector
{
    /// <summary>
    /// What the profile <paramref name="qualityProfileId"/> names in <paramref name="profiles"/> is
    /// called, and what its cutoff resolves to.
    /// </summary>
    internal static SceneProfileNames Project(WhisparrResponse profiles, int? qualityProfileId)
        => throw new NotImplementedException();
}
