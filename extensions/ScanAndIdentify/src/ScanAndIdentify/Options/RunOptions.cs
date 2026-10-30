using System.Text.Json;

namespace ScanAndIdentify.Options;

/// <summary>What one run does: which passes it performs and how each of them behaves.</summary>
/// <remarks>
/// The member names are the host request-body field names, so a run copies them across with no
/// translation table.
/// <para>
/// Nothing a run does replaces an asset that already exists: the host's <c>overwrite</c> is passed as
/// false by both generate passes and is deliberately not a member here, which is what keeps a repeat
/// run cheap rather than a full re-encode of the library.
/// </para>
/// </remarks>
public sealed record RunOptions
{
    /// <summary>Generate video thumbnails and screenshots.</summary>
    public bool Thumbnails { get; init; } = true;

    /// <summary>Generate video previews.</summary>
    public bool Previews { get; init; } = true;

    /// <summary>Generate sprite sheets for the scrubbing strip.</summary>
    public bool Sprites { get; init; } = true;

    /// <summary>Generate the 3D covers and previews a VR video needs.</summary>
    /// <remarks>Off by default: it costs a second encode per VR video.</remarks>
    public bool VrStereo { get; init; }

    /// <summary>Generate image thumbnails.</summary>
    public bool ImageThumbnails { get; init; } = true;

    /// <summary>Generate gallery cover thumbnails.</summary>
    public bool GalleryThumbnails { get; init; } = true;

    /// <summary>Generate the fingerprints a later identify pass matches on, where they are missing.</summary>
    public bool BackfillFingerprints { get; init; } = true;

    /// <summary>Scan every file again rather than only those whose modification time moved.</summary>
    public bool Rescan { get; init; }

    /// <summary>Let identify create a performer, studio or tag the library does not hold yet.</summary>
    /// <remarks>One switch drives all three of the host's create flags.</remarks>
    public bool CreateMissingEntities { get; init; } = true;

    /// <summary>Mark a video organized once identify has matched it.</summary>
    public bool MarkOrganized { get; init; }

    /// <summary>Serializer settings shared by save and load, so the round-trip is symmetric.</summary>
    /// <remarks>
    /// No naming policy, so the stored blob carries this record's own PascalCase spelling, which is
    /// not the camelCase the routes answer in. Case-insensitive names keep a hand-edited blob readable.
    /// </remarks>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
