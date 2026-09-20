using Cove.Core.Auth;
using Cove.Plugins;
using WhisparrSync.Contracts;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private const string SettingsTabKey = "whisparr-sync";

    private const string MissingTabKey = "whisparr-missing";

    private const string MissingTabComponentName = "WhisparrMissingTab";

    private const string MissingTabLabel = "Missing";

    private const int MissingTabOrder = 150;

    // Composed from the same base the route is mapped under, so the endpoint the host calls and the
    // endpoint this extension mounts cannot drift apart.
    private string MissingCountEndpointFor(string kind)
        => RouteBase + "/entity/" + kind + "/{entityId}/missing/count";

    private const string SceneTabKey = "whisparr-scene";

    private const string SceneTabComponentName = "WhisparrSceneTab";

    private const string SceneTabLabel = "Whisparr";

    private const int SceneTabOrder = 150;

    private const string BulkHandlerName = "whisparrMonitorSelected";

    // The spelling the host's selection bar passes. The bar normalizes only the two media plurals,
    // so a studio or performer selection arrives as the raw plural, and the host matches an action's
    // declared types by exact string membership: a singular spelling makes the button not appear,
    // with no error anywhere.
    private const string StudiosSelectionType = "studios";

    private const string PerformersSelectionType = "performers";

    // Singular, unlike the studio and performer spellings: the bar's normalizer maps the videos
    // plural to the singular. The plural here would make the button not appear, with no error.
    private const string VideosSelectionType = "video";

    private const string SceneBatchActionId = "whisparr-scene-batch";

    private const string SceneBatchHandlerName = "whisparrSceneBatch";

    /// <summary>
    /// The surfaces the host mounts: a settings tab, a control in the studio and performer action
    /// rows, a catalogue tab per entity page type, a scene tab on the video detail page, and one
    /// bulk action per selection bar, the scene one on v3 alone.
    /// </summary>
    /// <remarks>
    /// Every <c>componentName</c> and <c>handlerName</c> must be byte-identical to the key in the
    /// bundle's <c>defineExtension</c> map: the host resolves one to the other by exact string and
    /// renders or dispatches nothing, with no error, when they differ.
    /// <para>
    /// The action-row slot is the only position an extension can reach on either page. The host's
    /// own entity-action contribution point answers with an empty list for anything but a video or an
    /// image, so a control registered there would never render at all.
    /// </para>
    /// <para>
    /// A bulk action is registered once per entity kind rather than once carrying both types: the
    /// host allows a single required permission per action and filters visibility by both the entity
    /// type in context and that permission. Each declares a handler and no api endpoint, because the
    /// handler asks for a verb and a scope before anything is sent.
    /// </para>
    /// <para>
    /// The manifest is rebuilt on every aggregation and reads the generation the extension last
    /// stored. The browser fetches the manifest, so a generation change takes effect on the next page
    /// load.
    /// </para>
    /// <para>
    /// The studio and performer bulk actions stay registered on both generations, and a verb's
    /// availability is enforced in the handler and again at the route. The scene selection action is
    /// registered on v3 alone, because no verb it offers reaches anything on v2.
    /// </para>
    /// </remarks>
    public override UIManifest GetUIManifest()
    {
        var manifest = ManifestBuilder()
            .AddSettingsTab(
                key: SettingsTabKey,
                label: "Whisparr Sync",
                description: "Keep Cove in step with the Whisparr instance you configure.",
                order: 100,
                layout: SettingsTabLayout.Page)
            .AddSettingsSection(
                targetTab: SettingsTabKey,
                label: "Whisparr Sync",
                componentName: "WhisparrSyncPage")
            .AddSlot("studio-detail-actions", componentName: "WhisparrStudioActions", order: 100)
            .AddSlot("performer-detail-actions", componentName: "WhisparrPerformerActions", order: 100)

            // Both unconditional. A studio monitors as a series matched by ThePornDB on v2 too, and
            // MonitorStudio is in both capability tables, so the studio surfaces work whichever
            // generation is connected.
            .AddSlot("studios-list-toolbar-end", componentName: "WhisparrLibraryToggle", order: 100)
            .AddSlot("studio-card-footer", componentName: "WhisparrStudioCardBadge", order: 100)
            .AddSlot("studios-list-row", componentName: "WhisparrStudioLibraryRow", order: 100)

            // One component, registered once per page type. The host passes a tab component only the
            // entity id and a navigate callback, so the component reads its own kind from its route.
            //
            // Each countEndpoint bakes its own kind: the host substitutes the literal {entityId} and
            // nothing else, so the kind cannot travel as a second placeholder.
            .AddTab(
                pageType: "studio",
                key: MissingTabKey,
                label: MissingTabLabel,
                componentName: MissingTabComponentName,
                order: MissingTabOrder,
                countEndpoint: MissingCountEndpointFor("studio"))
            .AddTab(
                pageType: "performer",
                key: MissingTabKey,
                label: MissingTabLabel,
                componentName: MissingTabComponentName,
                order: MissingTabOrder,
                countEndpoint: MissingCountEndpointFor("performer"))
            .AddTab(
                pageType: "tag",
                key: MissingTabKey,
                label: MissingTabLabel,
                componentName: MissingTabComponentName,
                order: MissingTabOrder,
                countEndpoint: MissingCountEndpointFor("tag"))
            .AddAction(
                id: "whisparr-monitor-selected-studios",
                label: "Whisparr",
                actionType: "bulk",
                entityTypes: [StudiosSelectionType],
                icon: "eye",
                apiEndpoint: null,
                handlerName: BulkHandlerName,
                order: 100,
                requiredPermission: Permissions.ExtensionsConfigure,
                // The work reports into the host's own job drawer, so its queued-success alert would
                // say the same thing twice.
                suppressSuccessAlert: true)
            .AddAction(
                id: "whisparr-monitor-selected-performers",
                label: "Whisparr",
                actionType: "bulk",
                entityTypes: [PerformersSelectionType],
                icon: "eye",
                apiEndpoint: null,
                handlerName: BulkHandlerName,
                order: 100,
                requiredPermission: Permissions.ExtensionsConfigure,
                suppressSuccessAlert: true)
            .WithJsBundle("index.mjs");

        // Whisparr v2 publishes no per-scene identity and holds no performer entity, so these
        // surfaces have no meaning there and are hidden by omission.
        //
        // The scene tab carries neither a countEndpoint nor an icon: the video detail page maps a
        // contributed tab into its own list keeping only the key, the label and the manual contexts,
        // so either would be fetched and drawn by nothing.
        if (!SelectedGenerationIsOlder)
        {
            manifest
                .AddSlot("videos-list-toolbar-end", componentName: "WhisparrLibraryToggle", order: 100)
                .AddSlot("video-card-content", componentName: "WhisparrVideoCardBadge", order: 100)
                .AddSlot("videos-list-row", componentName: "WhisparrVideoLibraryRow", order: 100)
                .AddSlot(
                    "performers-list-toolbar-end", componentName: "WhisparrLibraryToggle", order: 100)
                .AddSlot(
                    "performer-card-footer", componentName: "WhisparrPerformerCardBadge", order: 100)
                .AddSlot(
                    "performers-list-row", componentName: "WhisparrPerformerLibraryRow", order: 100)
                .AddTab(
                    pageType: "video",
                    key: SceneTabKey,
                    label: SceneTabLabel,
                    componentName: SceneTabComponentName,
                    order: SceneTabOrder)

                // No icon: the host draws its own glyph on this button whatever an action declares,
                // so a glyph named here would be a claim nothing renders.
                .AddAction(
                    id: SceneBatchActionId,
                    label: "Whisparr",
                    actionType: "bulk",
                    entityTypes: [VideosSelectionType],
                    icon: null,
                    apiEndpoint: null,
                    handlerName: SceneBatchHandlerName,
                    order: 100,
                    requiredPermission: Permissions.ExtensionsConfigure,
                    suppressSuccessAlert: true);
        }

        return manifest.Build();
    }

    // False for anything but a positive v2 reading, a generation nothing established included, so an
    // unreadable store and an unbindable blob both keep every surface.
    private bool SelectedGenerationIsOlder
        => string.Equals(
            _selectedGeneration,
            nameof(WhisparrGeneration.V2),
            StringComparison.OrdinalIgnoreCase);
}
