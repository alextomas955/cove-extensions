using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace Renamer;

/// <summary>
/// The media-file kinds this extension can rename. Drives entity-type-aware token degradation in the
/// <c>MetadataProjector</c>: only the media tokens a kind actually carries are projected. Gallery is
/// not renamed but is listed for completeness.
/// </summary>
/// <remarks>
/// Every layer names it: the settings map keys on it, the wire document declares it, the planner
/// walks it and the executor announces it. The numeric values are the scan's walk order and a stored
/// <c>ScanCursor</c> resumes by them, so they are written out rather than left to declaration order.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum RenamerFileKind
{
    Video = 0,
    Image = 1,
    Audio = 2,
    Gallery = 3,
    Text = 4,
}

/// <summary>Which kinds this extension actually renames.</summary>
/// <remarks>
/// The scan, the row pager, the whole-library rename and the tests all read the set here, so a kind
/// added to the enum reaches every one of them together or none of them.
/// </remarks>
public static class RenamableKinds
{
    /// <summary>
    /// Every renamable kind, in the fixed order every whole-library path walks. Gallery is absent: it
    /// is not renamable, <c>LoadEntityAsync</c> returns null for it, and no endpoint accepts it.
    /// </summary>
    public static readonly RenamerFileKind[] All =
        [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio, RenamerFileKind.Text];

    /// <summary>Whether this extension renames <paramref name="kind"/> at all.</summary>
    public static bool Includes(RenamerFileKind kind) => Array.IndexOf(All, kind) >= 0;
}
