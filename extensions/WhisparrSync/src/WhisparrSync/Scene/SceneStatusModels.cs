using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisparrSync.Scene;

/// <summary>
/// A scene's Whisparr status. The status legend surfaces four PRIMARY states —
/// <see cref="Downloaded"/> / <see cref="Monitored"/> / <see cref="NotAdded"/> / <see cref="Excluded"/> — and
/// this enum adds <see cref="Unmonitored"/> as a distinct HONEST state (a scene present in Whisparr but with
/// <c>monitored:false</c>). Per the CONTEXT status-derivation rule, an unmonitored-but-present scene is NEVER
/// folded into <see cref="NotAdded"/>: "not added" means Whisparr has no row at all, whereas "unmonitored"
/// means the row exists and is deliberately not tracked — conflating them would misreport the library.
/// </summary>
/// <remarks>
/// WIRE CASING IS PINNED to the enum's <em>camelCase</em> name — <c>downloaded</c> /
/// <c>monitored</c> / <c>unmonitored</c> / <c>notAdded</c> / <c>excluded</c> — matching the shipped
/// camelCase status store so the scene panel/toolbar and the reconciliation status column
/// can compare the state string byte-for-byte. This type-level converter is the DEFAULT; because a
/// converter in an <c>JsonSerializerOptions.Converters</c> collection out-ranks a type-level attribute in
/// System.Text.Json, every response field that carries this enum on options that already register a plain
/// <c>JsonStringEnumConverter()</c> (the monitor/reconciliation options) ALSO annotates the field with a
/// <em>property-level</em> <see cref="SceneWhisparrStateJsonConverter"/> (highest precedence) — see
/// <see cref="SceneDetail.State"/> and the reconciliation row's <c>WhisparrState</c>. Do NOT rely on a plain
/// <c>JsonStringEnumConverter()</c> to case this enum (it emits PascalCase).
/// </remarks>
[JsonConverter(typeof(SceneWhisparrStateJsonConverter))]
internal enum SceneWhisparrState
{
    /// <summary>No Whisparr movie row matches the scene's StashDB id(s), and it is not excluded.</summary>
    NotAdded,

    /// <summary>The scene's StashDB id is in Whisparr's import-list exclusion set — exclusion-first.</summary>
    Excluded,

    /// <summary>A Whisparr movie row exists, has no file yet, and is monitored (Whisparr will try to acquire it).</summary>
    Monitored,

    /// <summary>A Whisparr movie row exists, has no file, and is NOT monitored — present but deliberately untracked.</summary>
    Unmonitored,

    /// <summary>A Whisparr movie row exists and has a file on disk (<c>hasFile:true</c>).</summary>
    Downloaded,
}

/// <summary>
/// Pins <see cref="SceneWhisparrState"/> to a camelCase string on the wire. Applied via the
/// type-level <see cref="JsonConverterAttribute"/> on the enum so the casing is guaranteed everywhere the
/// enum serializes — scene-detail, the toolbar summary, and the reconciliation row's <c>whisparrState</c> —
/// even when the surrounding options carry a naming-policy-free <c>JsonStringEnumConverter</c> (a type
/// attribute takes precedence over an options-collection converter, so there is no conflict). The emitted
/// values are exactly <c>downloaded</c> / <c>monitored</c> / <c>unmonitored</c> / <c>notAdded</c> /
/// <c>excluded</c>.
/// </summary>
internal sealed class SceneWhisparrStateJsonConverter : JsonStringEnumConverter<SceneWhisparrState>
{
    public SceneWhisparrStateJsonConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}

/// <summary>
/// The scene-panel projection — Whisparr-OWNED facts only. It deliberately carries NO Cove-owned
/// field (no title/date/path/size/runtime/resolution): those are already shown elsewhere on the Cove scene
/// page, and re-stating them here would both duplicate the UI and widen the information surface.
/// </summary>
/// <param name="State">The derived 4-state status (exclusion-first).</param>
/// <param name="Added">Whether a Whisparr movie row matches the scene's StashDB id(s).</param>
/// <param name="Monitored">The matched movie's <c>monitored</c> flag (false when not added).</param>
/// <param name="HasFile">The matched movie's <c>hasFile</c> flag (false when not added).</param>
/// <param name="Quality">The matched movie file's quality name (<c>movieFile.quality.quality.name</c>), or null.</param>
/// <param name="CutoffMet">
/// Whether the quality cutoff is met: the inverse of the movie's <c>qualityCutoffNotMet</c> when Whisparr
/// reports it, else null (unknown) — never a guessed value.
/// </param>
/// <param name="ActionsSupported">Whether the version offers per-scene actions; false on v2 (Sonarr).</param>
internal sealed record SceneDetail(
    [property: JsonConverter(typeof(SceneWhisparrStateJsonConverter))] SceneWhisparrState State,
    bool Added,
    bool Monitored,
    bool HasFile,
    string? Quality,
    bool? CutoffMet,
    bool ActionsSupported = true);

/// <summary>
/// The toolbar-summary partition: how many scenes in a view fall into each Whisparr state, plus the
/// <see cref="Total"/> scenes classified. Sums to <see cref="Total"/> (every scene lands in exactly one state).
/// </summary>
internal sealed record SceneStatusCounts(
    int Downloaded,
    int Monitored,
    int Unmonitored,
    int NotAdded,
    int Excluded,
    int Total);
