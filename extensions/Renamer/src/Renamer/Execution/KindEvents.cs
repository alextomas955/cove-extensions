using Cove.Core.Events;

namespace Renamer.Execution;

// How a renamed entity's kind is announced to the rest of Cove: the event the bus carries, and the
// entity-type name that travels with it. One mapping serves the rename and the undo path, because a
// kind added to one copy of the switch and not the other is not a build error and would make a rename
// and its undo announce different kinds.
//
// A kind this extension does not rename throws. A fallback would ship a wrong event for a kind added
// to the enum and missed here, under a build that looked correct.
internal static class KindEvents
{
    internal static EventType EventTypeFor(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Video => EventType.VideoUpdated,
        RenamerFileKind.Image => EventType.ImageUpdated,
        RenamerFileKind.Audio => EventType.AudioUpdated,
        RenamerFileKind.Text => EventType.TextUpdated,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a renamable kind"),
    };

    internal static string EntityTypeName(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Video => "Video",
        RenamerFileKind.Image => "Image",
        RenamerFileKind.Audio => "Audio",
        RenamerFileKind.Text => "Text",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a renamable kind"),
    };
}
