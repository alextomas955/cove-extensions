using Cove.Core.Auth;
using Cove.Plugins;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests;

/// <summary>
/// The "Rename selected" bulk action is contributed through the extension's
/// <c>GetUIManifest()</c> (not <c>GetActions()</c> — <c>FullExtensionBase</c> does not implement
/// <c>IActionExtension</c>), and the <c>renamer-batch</c> job is registered via <c>DefineJobs()</c>.
/// These assert the exact contributed shape the host renders/dispatches against.
/// </summary>
public sealed class ActionDeclarationTests
{
    private static global::Renamer.Renamer NewExtension() => RenamerFixture.Create();

    [Fact]
    public void GetUIManifest_ContributesPerKindBulkActions_EachWithItsMatchingPermission()
    {
        var ext = NewExtension();

        var manifest = ext.GetUIManifest();

        // The bulk action is registered once per kind (video, image, text) so each carries the matching
        // RequiredPermission — the host's action model allows only a single permission per action and
        // filters visibility by both entity-type context and that permission.
        Assert.Equal(3, manifest.Actions.Count);

        var video = Assert.Single(manifest.Actions, a => a.Id == "renamer-selected-video");
        Assert.Equal("Rename selected", video.Label);
        Assert.Equal("com.alextomas955.renamer", video.ExtensionId);
        Assert.Equal("bulk", video.ActionType);
        Assert.Equal(["video"], video.EntityTypes);
        // The action dispatches the JS handler instead of POSTing directly, so the host can gate
        // execution behind a preview → confirm. HandlerName is set; no ApiEndpoint.
        Assert.Equal("renamerSelected", video.HandlerName);
        Assert.Null(video.ApiEndpoint);
        Assert.Equal(Permissions.VideosWrite, video.RequiredPermission);

        var image = Assert.Single(manifest.Actions, a => a.Id == "renamer-selected-image");
        Assert.Equal("Rename selected", image.Label);
        Assert.Equal(["image"], image.EntityTypes);
        Assert.Equal("renamerSelected", image.HandlerName);
        Assert.Null(image.ApiEndpoint);
        Assert.Equal(Permissions.ImagesWrite, image.RequiredPermission);

        var text = Assert.Single(manifest.Actions, a => a.Id == "renamer-selected-text");
        Assert.Equal("Rename selected", text.Label);
        // Both spellings: the host singularizes only "videos" and "images" before matching an action's
        // entity types, so a texts list hands it the plural.
        Assert.Equal(["text", "texts"], text.EntityTypes);
        Assert.Equal("renamerSelected", text.HandlerName);
        Assert.Null(text.ApiEndpoint);
        Assert.Equal(Permissions.TextsWrite, text.RequiredPermission);
    }

    [Fact]
    public void Jobs_RegistersTheRenamerBatchDefinition()
    {
        var ext = NewExtension();

        var job = Assert.Single(((IJobExtension)ext).Jobs);
        Assert.Equal("renamer-batch", job.Id);
        Assert.True(job.SupportsParameters);
        Assert.True(job.ShowInTaskList);
    }
}
