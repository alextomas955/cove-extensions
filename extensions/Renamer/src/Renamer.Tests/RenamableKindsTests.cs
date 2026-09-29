using Cove.Core.Auth;
using Cove.Core.Events;
using Renamer.Execution;

namespace Renamer.Tests;

public sealed class RenamableKindsTests
{
    [Theory]
    [InlineData(RenamerFileKind.Video, Permissions.VideosRead, Permissions.VideosWrite)]
    [InlineData(RenamerFileKind.Image, Permissions.ImagesRead, Permissions.ImagesWrite)]
    [InlineData(RenamerFileKind.Audio, Permissions.AudiosRead, Permissions.AudiosWrite)]
    [InlineData(RenamerFileKind.Text, Permissions.TextsRead, Permissions.TextsWrite)]
    public void EveryRenamableKind_IsGatedOnItsOwnPermissionPair(RenamerFileKind kind, string read, string write)
    {
        Assert.Equal((read, write), global::Renamer.Renamer.PermissionsFor(kind));
    }

    [Theory]
    [InlineData(RenamerFileKind.Video, "Video", EventType.VideoUpdated)]
    [InlineData(RenamerFileKind.Image, "Image", EventType.ImageUpdated)]
    [InlineData(RenamerFileKind.Audio, "Audio", EventType.AudioUpdated)]
    [InlineData(RenamerFileKind.Text, "Text", EventType.TextUpdated)]
    public void EveryRenamableKind_AnnouncesItselfWithItsOwnEvent(RenamerFileKind kind, string entityType, EventType type)
    {
        Assert.Equal(entityType, KindEvents.EntityTypeName(kind));
        Assert.Equal(type, KindEvents.EventTypeFor(kind));
    }

    // A kind added to the set without a row above leaves that kind's permissions and events unpinned.
    [Fact]
    public void TheRowsAbove_CoverEveryRenamableKind()
    {
        Assert.Equal(
            [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio, RenamerFileKind.Text],
            RenamableKinds.All);
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
