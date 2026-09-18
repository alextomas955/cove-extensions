using Cove.Core.Events;

namespace Renamer.Execution;

/// <summary>
/// How a renamed entity's kind is announced to the rest of Cove: the event the bus carries, and the
/// entity-type name that travels with it.
/// </summary>
/// <remarks>
/// One mapping for both the rename and the undo path. They published the same pair from two copies of
/// the same switch, and a kind added to one copy and not the other is not a build error: the rename
/// announces the new kind while its undo announces a video, and every listener downstream believes it.
/// <para>
/// A kind this extension does not rename throws rather than falling back. The fallback these replaced
/// answered <see cref="EventType.VideoUpdated"/> for anything unlisted, so a kind added to the enum
/// and missed here would have shipped a wrong event under a right-looking build.
/// </para>
/// </remarks>
internal static class KindEvents
{
    /// <summary>The event published when an entity of <paramref name="kind"/> has been renamed.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not one this extension renames.</exception>
    internal static EventType EventTypeFor(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Video => EventType.VideoUpdated,
        RenamerFileKind.Image => EventType.ImageUpdated,
        RenamerFileKind.Audio => EventType.AudioUpdated,
        RenamerFileKind.Text => EventType.TextUpdated,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a renamable kind"),
    };

    /// <summary>The entity-type name carried on that event.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not one this extension renames.</exception>
    internal static string EntityTypeName(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Video => "Video",
        RenamerFileKind.Image => "Image",
        RenamerFileKind.Audio => "Audio",
        RenamerFileKind.Text => "Text",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a renamable kind"),
    };
}
