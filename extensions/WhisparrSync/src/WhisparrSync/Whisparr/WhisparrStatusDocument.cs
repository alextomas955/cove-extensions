using System.Text.Json;

namespace WhisparrSync.Whisparr;

/// <summary>
/// The parts of a Whisparr <c>system/status</c> response this extension reads.
/// </summary>
/// <remarks>
/// Not a mirror of Whisparr's document: members nothing here reads are ignored, and the four count
/// fields are recorded as PRESENCE rather than as values, because what corroborates a generation is
/// whether the instance serves them at all.
/// <para>
/// Both generations serve the document under <c>/api/v3</c>, so the path never tells them apart; the
/// version in the body does.
/// </para>
/// </remarks>
/// <param name="Version">The instance's own version string, verbatim, or null when it declared none.</param>
/// <param name="Branch">The release branch the instance names.</param>
/// <param name="AppName">
/// Which application answered. Whisparr and its Radarr/Sonarr siblings are forks of one codebase and
/// all declare this, which is what makes it useful for telling Whisparr from not-Whisparr.
/// </param>
/// <param name="MovieCountPresent">Whether the document carries <c>movieCount</c>.</param>
/// <param name="SceneCountPresent">Whether the document carries <c>sceneCount</c>.</param>
/// <param name="PerformerCountPresent">Whether the document carries <c>performerCount</c>.</param>
/// <param name="StudioCountPresent">Whether the document carries <c>studioCount</c>.</param>
public sealed record WhisparrStatusDocument(
    string? Version,
    string? Branch,
    string? AppName,
    bool MovieCountPresent,
    bool SceneCountPresent,
    bool PerformerCountPresent,
    bool StudioCountPresent)
{
    /// <summary>Whether all four count fields are present.</summary>
    public bool AllCountFieldsPresent =>
        MovieCountPresent && SceneCountPresent && PerformerCountPresent && StudioCountPresent;

    /// <summary>Whether none of the four count fields is present.</summary>
    public bool NoCountFieldsPresent =>
        !MovieCountPresent && !SceneCountPresent && !PerformerCountPresent && !StudioCountPresent;

    /// <summary>
    /// Parses <paramref name="json"/>, or returns null when it is absent, unparseable, or not a JSON
    /// object.
    /// </summary>
    /// <remarks>
    /// Never throws. A body that came from something other than the API is an INPUT to the failure
    /// taxonomy, not an error: the classifier has a kind for it and an exception here would deny it
    /// the chance to say so.
    /// </remarks>
    public static WhisparrStatusDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new WhisparrStatusDocument(
                TextOf(root, "version"),
                TextOf(root, "branch"),
                TextOf(root, "appName"),
                root.TryGetProperty("movieCount", out _),
                root.TryGetProperty("sceneCount", out _),
                root.TryGetProperty("performerCount", out _),
                root.TryGetProperty("studioCount", out _));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A member of any other JSON kind reads as absent rather than as its ToString(): a number where a
    // version string belongs is not a version this product can compare against.
    private static string? TextOf(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
