using System.Text.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Missing;

/// <summary>What one instance row says about one scene, read once.</summary>
/// <remarks>
/// Every answer comes off the same row, so the state a card shows, the identifier a per-scene verb
/// names and the folder a relocation compares against cannot disagree about which scene they
/// describe. The identifier and the folder are null where the answer carried no row or the row
/// carried nothing usable.
/// </remarks>
public sealed record SceneOnInstance(MissingSceneState State, int? InstanceId, string? Path = null);

internal static class SceneStatusPort
{
    // No row is an absence rather than a failure, which is the one distinction a reader acts on
    // differently.
    internal static SceneOnInstance ReadRow(WhisparrResponse answered)
    {
        ArgumentNullException.ThrowIfNull(answered);

        // The per-scene route answers a held scene and an unheld one alike with a list, so a
        // not-found here is the instance stating an absence rather than declining to answer.
        if (answered.StatusCode == 404)
        {
            return Absent;
        }

        if (answered.StatusCode is not (>= 200 and < 300))
        {
            return Unknown;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(answered.Body);
        }
        catch (JsonException)
        {
            return Unknown;
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Unknown;
            }

            if (parsed.RootElement.GetArrayLength() == 0)
            {
                return Absent;
            }

            var row = parsed.RootElement[0];
            if (!row.TryGetProperty("monitored", out var monitored)
                || monitored.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Unknown;
            }

            return new SceneOnInstance(
                monitored.GetBoolean()
                    ? MissingSceneState.Monitored
                    : MissingSceneState.Unmonitored,
                InstanceIdIn(row),
                PathIn(row));
        }
    }

    private static string? PathIn(JsonElement row)
        => row.TryGetProperty("path", out var path)
            && path.ValueKind == JsonValueKind.String
            && path.GetString() is { Length: > 0 } held
                ? held
                : null;

    private static int? InstanceIdIn(JsonElement row)
        => row.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt32(out var held)
            && held > 0
                ? held
                : null;

    private static SceneOnInstance Absent { get; } = new(MissingSceneState.NotAdded, null);

    private static SceneOnInstance Unknown { get; } = new(MissingSceneState.StatusUnknown, null);
}
