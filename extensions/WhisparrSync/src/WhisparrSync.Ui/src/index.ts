/**
 * Bundle entry. Each `components` map KEY MUST be byte-identical to the matching C# manifest `componentName`
 * (WhisparrSync.Api.cs) so the host resolves the registered component:
 *   - `WhisparrSyncPage`        → the full-page settings tab (AddSettingsSection).
 *   - `WhisparrMonitorButton`   → the action-row monitor toggle on studio/performer pages (AddSlot
 *                                 `*-detail-actions`).
 *   - `WhisparrStatusLine`      → the quiet monitor status line (AddSlot `*-detail-bottom`).
 *   - `WhisparrScenePanel`      → the per-scene Whisparr tab in the video detail rail (AddTab `video`).
 *   - `WhisparrLibraryToggle`   → the off-by-default library status pill (AddSlot `videos-list-toolbar-end`).
 *   - `WhisparrLibraryRow`      → the per-state count summary on its own row below the toolbar, revealed by the
 *                                 pill (AddSlot `videos-list-row`).
 *   - `WhisparrCardBadge`       → the per-scene status glyph in each card's content area (AddSlot
 *                                 `video-card-content`), revealed by the same pill.
 *   - `WhisparrEntityCardBadge` → the "Monitored · X/Y" badge in studio/performer card footers (AddSlot
 *                                 `studio-card-footer` / `performer-card-footer`), shown only when monitored.
 *   - `WhisparrEntityLibraryRow`→ the library-wide "N monitored" row below the studios/performers toolbar (AddSlot
 *                                 `studios-list-row` / `performers-list-row`), revealed by the same pill.
 *
 * The default export ALSO carries an `actionHandlers` map for the videos-list "Whisparr" bulk action.
 * The SDK `ExtensionModule` type does NOT declare `actionHandlers`, but the host loader reads
 * `mod.default.actionHandlers` UNTYPED — so we attach it via a local cast (NOT by editing the SDK), exactly as
 * Renamer does. The handler key `whisparrBatchSelected` MUST equal the C# manifest HandlerName.
 */
import { defineExtension } from "@cove/extension-sdk";
import { SettingsPage } from "./settings/SettingsPage";
import { WhisparrMonitorButton } from "./monitor/WhisparrMonitorButton";
import { WhisparrStatusLine } from "./monitor/WhisparrStatusLine";
import { WhisparrScenePanel } from "./scene/WhisparrScenePanel";
import { WhisparrLogo } from "./common/ui/WhisparrLogo";
import { WhisparrLibraryToggle } from "./library/WhisparrLibraryToggle";
import { WhisparrLibraryRow } from "./scene/WhisparrLibraryRow";
import { WhisparrCardBadge } from "./scene/WhisparrCardBadge";
import { WhisparrEntityCardBadge } from "./entity/WhisparrEntityCardBadge";
import { WhisparrEntityLibraryRow } from "./entity/WhisparrEntityLibraryRow";
import { whisparrBatchSelected } from "./batch/whisparrBatchSelected";
import { whisparrEntitiesBatchSelected } from "./entity/whisparrEntitiesBatchSelected";
import {
  WhisparrStudioMissingTab,
  WhisparrPerformerMissingTab,
  WhisparrTagMissingTab,
} from "./discovery/WhisparrMissingTab";
import { WhisparrDiscoveryPage } from "./wanted/WhisparrDiscoveryPage";

interface WithActionHandlers {
  actionHandlers: Record<string, unknown>;
}

// These keys MUST be byte-identical to the C# manifest componentNames and the host
// resolveComponent lookup — one literal each, in both places, that must agree.
const mod = defineExtension({
  components: {
    WhisparrSyncPage: SettingsPage,
    WhisparrMonitorButton,
    WhisparrStatusLine,
    WhisparrScenePanel,
    // Also registered as the video detail-rail tab icon (AddTab icon: "WhisparrLogo").
    WhisparrLogo,
    WhisparrLibraryToggle,
    WhisparrLibraryRow,
    WhisparrCardBadge,
    WhisparrEntityCardBadge,
    WhisparrEntityLibraryRow,
    // Per-entity "Missing" tabs (AddTab studio/performer/tag) — each wrapper fixes its kind. All three are
    // registered on both generations (WhisparrSync.Api.cs:81, :85, :89 — above the v2-omission block), because
    // discovery reads the metadata source directly. Keys MUST match the C# manifest componentNames byte-for-byte.
    WhisparrStudioMissingTab,
    WhisparrPerformerMissingTab,
    WhisparrTagMissingTab,
    // The nested "Wanted, queue & history" settings sub-page (AddSettingsSection componentName). Key MUST match
    // the C# manifest componentName byte-for-byte.
    WhisparrDiscoveryPage,
  },
});

// Each key MUST be byte-identical to the matching C# manifest HandlerName (WhisparrSync.Api.cs
// VideosBatchHandlerName / EntitiesBatchHandlerName) — both sides must agree.
(mod as typeof mod & WithActionHandlers).actionHandlers = {
  whisparrBatchSelected,
  whisparrEntitiesBatchSelected,
};

export default mod;
