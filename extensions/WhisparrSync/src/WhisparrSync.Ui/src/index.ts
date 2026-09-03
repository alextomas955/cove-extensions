/**
 * Bundle entry. The default export is the extension module, and every `components` map key MUST
 * equal the C# manifest `componentName` it is advertised under (WhisparrSync.Api.cs
 * AddSettingsSection for the settings page, AddSlot for each detail page's action-row control): the
 * host resolves one to the other by exact string and renders nothing, with no error, when they
 * differ.
 *
 * The default export ALSO carries an `actionHandlers` map. The SDK's `ExtensionModule` type does not
 * declare it and the host loader reads `mod.default.actionHandlers` untyped, so it is attached
 * through a local cast rather than by editing the SDK. The handler key `whisparrMonitorSelected`
 * must equal the bulk actions' `HandlerName`, by the same exact-string rule.
 */
import { defineExtension } from "@cove/extension-sdk";
import { WhisparrSyncPage } from "./settings/WhisparrSyncPage";
import { WhisparrPerformerActions, WhisparrStudioActions } from "./monitoring/EntityMonitorButton";
import { monitorSelected } from "./monitoring/bulkMonitor";

interface WithActionHandlers {
  actionHandlers: Record<string, unknown>;
}

const mod = defineExtension({
  components: { WhisparrSyncPage, WhisparrStudioActions, WhisparrPerformerActions },
});
(mod as typeof mod & WithActionHandlers).actionHandlers = {
  whisparrMonitorSelected: monitorSelected,
};

export default mod;
