using System.Text.Json;
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
    /// <summary>What the answers establish about the scene they answered for.</summary>
    /// <remarks>
    /// A null <paramref name="profiles"/> is a read that produced no whole answer, and an answer
    /// that cannot be read establishes as little, so both are reported as a profile read that did
    /// not complete and neither removes a scene fact.
    /// <para>
    /// <paramref name="excluded"/> is passed in because the scene's own row carries no exclusion
    /// member at all: whether the instance excludes a scene is on its exclusion list and nowhere
    /// else, and the caller is what read that list.
    /// </para>
    /// </remarks>
    internal static SceneDetailView Project(
        WhisparrResponse scene, WhisparrResponse? profiles, bool excluded)
    {
        var row = SceneStatusPort.ReadRow(scene);
        var held = HeldValuesIn(scene);
        var named = SceneCutoffProjector.Project(profiles, held.QualityProfileId);

        return new SceneDetailView(
            SceneRefusalKind.None,
            Excluded: excluded,
            Present: PresenceIn(row.State),
            Monitored: MonitoringIn(row.State),
            QualityName: held.QualityName,
            QualityProfileName: named.ProfileName,
            CutoffName: named.CutoffName,
            ProfileReadDidNotComplete: named.ReadDidNotComplete);
    }

    /// <summary>What the scene's own row carries, or nothing where it carried no row.</summary>
    private readonly record struct HeldValues(string? QualityName, int? QualityProfileId);

    /// <summary>
    /// The quality of the file the instance holds, and the profile the scene is under.
    /// </summary>
    /// <remarks>
    /// The quality name is nested three deep on the file: the file carries a quality model, which
    /// carries the quality, which carries the name. A scene the instance holds no file for carries
    /// no file member at all.
    /// </remarks>
    private static HeldValues HeldValuesIn(WhisparrResponse scene)
    {
        if (scene.StatusCode is not (>= 200 and < 300))
        {
            return default;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(scene.Body);
        }
        catch (JsonException)
        {
            return default;
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Array
                || parsed.RootElement.GetArrayLength() == 0)
            {
                return default;
            }

            var row = parsed.RootElement[0];
            if (row.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            return new HeldValues(QualityNameOn(row), NumberIn(row, "qualityProfileId"));
        }
    }

    private static string? QualityNameOn(JsonElement row)
        => ObjectIn(row, "movieFile") is { } file
            && ObjectIn(file, "quality") is { } model
            && ObjectIn(model, "quality") is { } quality
                ? TextIn(quality, "name")
                : null;

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

    private static JsonElement? ObjectIn(JsonElement holder, string member)
        => holder.TryGetProperty(member, out var named) && named.ValueKind == JsonValueKind.Object
            ? named
            : null;

    private static int? NumberIn(JsonElement holder, string member)
        => holder.TryGetProperty(member, out var named)
            && named.ValueKind == JsonValueKind.Number
            && named.TryGetInt32(out var held)
                ? held
                : null;

    private static string? TextIn(JsonElement holder, string member)
        => holder.TryGetProperty(member, out var named)
            && named.ValueKind == JsonValueKind.String
            && named.GetString() is { Length: > 0 } held
                ? held
                : null;
}
