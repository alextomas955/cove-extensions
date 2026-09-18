using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace Renamer;

/// <summary>The media-file kinds this extension can rename.</summary>
/// <remarks>
/// The <c>MetadataProjector</c> projects only the media tokens a kind carries. Gallery is listed but
/// not renamed. The numeric values are the scan's walk order and a stored <c>ScanCursor</c> resumes
/// by them, so they are written out. The wire spelling is camelCase, from the converter on the type.
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

// The scan, the row pager, the whole-library rename and the tests all read this set, so a kind added
// to the enum reaches all of them or none.
public static class RenamableKinds
{
    // The fixed order every whole-library path walks. Gallery is absent: LoadEntityAsync returns null
    // for it and no endpoint accepts it.
    public static readonly ImmutableArray<RenamerFileKind> All =
        [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio, RenamerFileKind.Text];

    public static bool Includes(RenamerFileKind kind) => All.Contains(kind);
}
