using System.Text.Json;

namespace WhisparrSync.Whisparr;

/// <summary>
/// The parts of a Whisparr <c>system/status</c> response this extension reads.
/// </summary>
/// <remarks>
/// Not a mirror of Whisparr's document: members nothing here reads are ignored, and the four count
/// fields are recorded as presence rather than as values, because what corroborates a generation is
/// whether the instance serves them at all.
/// <para>
/// Both v2 and v3 serve the document under <c>/api/v3</c>, so the path never tells them apart; the
/// version in the body does.
/// </para>
/// <para>
/// <c>appName</c> tells Whisparr from not-Whisparr: its Radarr and Sonarr siblings are forks of one
/// codebase and all declare it. A version string absent from the body reads as null.
/// </para>
/// </remarks>
public sealed record WhisparrStatusDocument(
    string? Version,
    string? Branch,
    string? AppName,
    bool MovieCountPresent,
    bool SceneCountPresent,
    bool PerformerCountPresent,
    bool StudioCountPresent)
{
    public bool AllCountFieldsPresent =>
        MovieCountPresent && SceneCountPresent && PerformerCountPresent && StudioCountPresent;

    public bool NoCountFieldsPresent =>
        !MovieCountPresent && !SceneCountPresent && !PerformerCountPresent && !StudioCountPresent;

    /// <summary>
    /// Parses <paramref name="json"/>, or returns null when it is absent, unparseable, or not a JSON
    /// object.
    /// </summary>
    /// <remarks>
    /// Never throws. A body that came from something other than the API is an input to the failure
    /// classification, which has a kind for it, not an error.
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

    // A member of any other JSON kind reads as absent rather than as its ToString(): a number where
    // a version string belongs is not a version that can be compared.
    private static string? TextOf(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
