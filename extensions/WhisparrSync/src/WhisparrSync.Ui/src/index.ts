/**
 * Bundle entry. The default export is the extension module; the `components` map KEY
 * `WhisparrSyncPage` MUST equal the C# manifest `componentName` (WhisparrSync.Api.cs
 * AddSettingsSection) so the host resolves the registered component to render for the settings page.
 *
 * Unlike Renamer, this extension contributes no bulk action, so there is no `actionHandlers` block.
 */
import { defineExtension } from "@cove/extension-sdk";
import { SettingsPage } from "./SettingsPage";

// `WhisparrSyncPage` key MUST be byte-identical to the C# manifest componentName and the host
// resolveComponent lookup — one literal, three places that must all agree.
const mod = defineExtension({ components: { WhisparrSyncPage: SettingsPage } });

export default mod;
