using Cove.Core.Auth;
using Cove.Plugins;
using WhisparrSync.Contracts;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    /// <summary>The settings tab this extension mounts, and the tab its one section targets.</summary>
    private const string SettingsTabKey = "whisparr-sync";

    /// <summary>The tab this extension mounts on the studio, performer and tag pages.</summary>
    private const string MissingTabKey = "whisparr-missing";

    /// <summary>The name the bundle registers this extension's catalogue tab under.</summary>
    /// <remarks>
    /// Byte-identical to the key in the bundle's own component map. The host resolves one to the
    /// other by exact string and renders nothing, with no error, when they differ.
    /// </remarks>
    private const string MissingTabComponentName = "WhisparrMissingTab";

    /// <summary>How the catalogue tab reads on every page it is mounted on.</summary>
    private const string MissingTabLabel = "Missing";

    /// <summary>Where the catalogue tab sits among a page's own tabs.</summary>
    private const int MissingTabOrder = 150;

    /// <summary>The count route the host fetches for a <paramref name="kind"/> page.</summary>
    /// <remarks>
    /// Composed from the same base the route is mapped under, so the endpoint the host calls and the
    /// endpoint this extension mounts cannot drift apart.
    /// </remarks>
    private string MissingCountEndpointFor(string kind)
        => RouteBase + "/entity/" + kind + "/{entityId}/missing/count";

    /// <summary>The tab this extension mounts on the video detail page.</summary>
    private const string SceneTabKey = "whisparr-scene";

    /// <inheritdoc cref="MissingTabComponentName"/>
    private const string SceneTabComponentName = "WhisparrSceneTab";

    /// <summary>How the scene tab reads on the video detail page.</summary>
    private const string SceneTabLabel = "Whisparr";

    /// <summary>Where the scene tab sits among the video page's own tabs.</summary>
    private const int SceneTabOrder = 150;

    /// <summary>The name the bundle registers this extension's bulk action handler under.</summary>
    /// <remarks>
    /// Byte-identical to the key in the bundle's own handler map. The host resolves one to the other
    /// by exact string and dispatches nothing, with no error, when they differ.
    /// </remarks>
    private const string BulkHandlerName = "whisparrMonitorSelected";

    /// <summary>
    /// The spelling the host's selection bar passes for a studio selection.
    /// </summary>
    /// <remarks>
    /// The bar normalizes only the two media plurals; every studio and performer call site passes the
    /// RAW PLURAL, and the host matches an action's declared types by exact string membership. A
    /// singular spelling makes the button simply not appear, with no error anywhere, which is why the
    /// registration and the route's own parse read the same constant.
    /// </remarks>
    private const string StudiosSelectionType = "studios";

    /// <inheritdoc cref="StudiosSelectionType"/>
    private const string PerformersSelectionType = "performers";

    /// <summary>
    /// The spelling the host's selection bar passes for a video selection.
    /// </summary>
    /// <remarks>
    /// SINGULAR, unlike the studio and performer spellings: the bar's own normalizer maps the videos
    /// plural to the singular and every video call site passes the singular already. The host matches
    /// an action's declared types by exact string membership, so the plural here would make the
    /// button simply not appear, with no error anywhere.
    /// </remarks>
    private const string VideosSelectionType = "video";

    /// <summary>The id the host resolves this extension's scene selection action by.</summary>
    private const string SceneBatchActionId = "whisparr-scene-batch";

    /// <summary>
    /// The name the bundle registers this extension's scene selection handler under.
    /// </summary>
    /// <inheritdoc cref="BulkHandlerName" path="/remarks"/>
    private const string SceneBatchHandlerName = "whisparrSceneBatch";

    /// <summary>
    /// The surfaces the host mounts: one dedicated settings tab, one control in each of the studio
    /// and performer pages' own action rows, one catalogue tab per entity page type, one scene tab
    /// on the video detail page, and one bulk action per selection bar, the scene one on v3 alone.
    /// </summary>
    /// <remarks>
    /// Page layout, so the host renders the panel full-width with no card chrome and this extension
    /// draws its own. Every <c>componentName</c> must be byte-identical to the key in the bundle's
    /// <c>defineExtension</c> component map: the host resolves one to the other by exact string and
    /// renders nothing, with no error, when they differ.
    /// <para>
    /// The action-row slot is the only position an extension can reach on either page. The host's
    /// own entity-action contribution point answers with an empty list for anything but a video or an
    /// image, so a control registered there would never render at all.
    /// </para>
    /// <para>
    /// The bulk action is registered ONCE PER ENTITY KIND rather than once carrying both types: the
    /// host allows a single required permission per action and filters visibility by both the entity
    /// type in context and that permission, so one action covering both kinds would still be one
    /// visibility gate. Each declares a handler and NO api endpoint, because the handler has to ask
    /// for a verb and a scope before anything is sent.
    /// </para>
    /// <para>
    /// The manifest is rebuilt on every aggregation and reads the generation the extension last
    /// stored, so the videos-view registrations are absent on v2 and no surface
    /// renders empty there. The browser fetches the manifest, so a generation change takes effect on
    /// the next page load.
    /// </para>
    /// <para>
    /// The studio and performer bulk actions stay registered on both generations: an action's
    /// PRESENCE is a manifest fact and a verb's AVAILABILITY is a runtime one, enforced in the
    /// handler and again at the route. The scene selection action departs from that principle and is
    /// registered on v3 alone, because no verb it offers reaches anything on v2. The reader on v2
    /// therefore meets a studio or performer selection
    /// that offers a Whisparr button explaining itself, and a scene selection that offers no button
    /// at all.
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
            // nothing else, so the kind cannot travel as a second placeholder. It is fetched in an
            // effect on page load, before the tab is opened, and a badge is drawn only for a numeric
            // count.
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
                // The work reports into the host's own Job Drawer, so its queued-success alert would
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

        // Whisparr v2 publishes no per-scene identity and holds no performer entity, so
        // these surfaces have no meaning there and are hidden by omission. The full-width row below
        // a list toolbar goes with the badges it counts, so it is registered wherever they are.
        //
        // The scene tab carries neither a countEndpoint nor an icon: the video detail page maps a
        // contributed tab into its own list keeping only the key, the label and the manual contexts,
        // so either would be fetched and drawn by nothing. Nothing else is registered on that page,
        // so opening a video costs no Whisparr request until the tab is pressed.
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

                // The label is the whole of what this extension supplies to the button. The host
                // owns its layout and draws its own glyph there whatever an action declares, so a
                // glyph named here would be a claim nothing renders, and the selected count beside
                // it is the host's own.
                //
                // No api endpoint, because the handler asks which verb before anything is sent.
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
                    // The work reports into the host's own Job Drawer, so its queued-success alert
                    // would say the same thing twice.
                    suppressSuccessAlert: true);
        }

        return manifest.Build();
    }

    /// <summary>Whether the stored generation is positively v2.</summary>
    /// <remarks>
    /// False for anything else, a generation nothing established included, so a store that could not
    /// be read and a blob the model could not bind both keep every surface.
    /// </remarks>
    private bool SelectedGenerationIsOlder
        => string.Equals(
            _selectedGeneration,
            nameof(WhisparrGeneration.V2),
            StringComparison.OrdinalIgnoreCase);
}
