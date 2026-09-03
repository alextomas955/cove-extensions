/**
 * Bundle entry. The default export is the extension module, and every `components` map key MUST
 * equal the C# manifest `componentName` it is advertised under (WhisparrSync.Api.cs
 * AddSettingsSection for the settings page, AddSlot for each detail page's action-row control): the
 * host resolves one to the other by exact string and renders nothing, with no error, when they
 * differ.
 */
import { defineExtension } from "@cove/extension-sdk";
import { WhisparrSyncPage } from "./settings/WhisparrSyncPage";
import { WhisparrPerformerActions, WhisparrStudioActions } from "./monitoring/EntityMonitorButton";

export default defineExtension({
  components: { WhisparrSyncPage, WhisparrStudioActions, WhisparrPerformerActions },
});
