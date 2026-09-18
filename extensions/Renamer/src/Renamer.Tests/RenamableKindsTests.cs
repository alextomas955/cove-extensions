using Cove.Core.Events;
using Renamer.Execution;

namespace Renamer.Tests;

/// <summary>
/// That every kind the extension claims to rename is mapped everywhere a rename needs it, and that a
/// kind it does not rename is refused rather than answered for.
/// </summary>
/// <remarks>
/// Driven from <see cref="RenamableKinds.All"/>, so a kind added to the enum and missed in one
/// switch fails here rather than checking the wrong permission or publishing the wrong event.
/// </remarks>
public sealed class RenamableKindsTests
{
    public static TheoryData<RenamerFileKind> Renamable()
    {
        var data = new TheoryData<RenamerFileKind>();
        foreach (var kind in RenamableKinds.All)
        {
            data.Add(kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Renamable))]
    public void EveryRenamableKind_HasItsOwnPermissionPair(RenamerFileKind kind)
    {
        var (read, write) = global::Renamer.Renamer.PermissionsFor(kind);

        Assert.False(string.IsNullOrWhiteSpace(read));
        Assert.False(string.IsNullOrWhiteSpace(write));
        Assert.NotEqual(read, write);
        // Named for the kind, so a kind silently borrowing another's permission fails here rather
        // than at an endpoint that lets the wrong caller through.
        Assert.StartsWith(kind.ToString(), read, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(kind.ToString(), write, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Renamable))]
    public void EveryRenamableKind_AnnouncesItself(RenamerFileKind kind)
    {
        Assert.Equal(kind.ToString(), KindEvents.EntityTypeName(kind));
        Assert.Equal(
            Enum.Parse<EventType>(kind + "Updated"),
            KindEvents.EventTypeFor(kind));
    }

    [Fact]
    public void AKindTheExtensionDoesNotRename_IsRefused()
    {
        Assert.False(RenamableKinds.Includes(RenamerFileKind.Gallery));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => global::Renamer.Renamer.PermissionsFor(RenamerFileKind.Gallery));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => KindEvents.EventTypeFor(RenamerFileKind.Gallery));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => KindEvents.EntityTypeName(RenamerFileKind.Gallery));
    }
}
