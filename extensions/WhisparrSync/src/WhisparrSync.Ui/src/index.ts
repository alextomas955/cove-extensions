/**
 * Bundle entry. The default export is the extension module, and the `components` map key
 * `WhisparrSyncPage` MUST equal the C# manifest `componentName` (WhisparrSync.Api.cs
 * AddSettingsSection): the host resolves one to the other by exact string and renders nothing, with
 * no error, when they differ.
 */
import { defineExtension } from "@cove/extension-sdk";
import { WhisparrSyncPage } from "./settings/WhisparrSyncPage";

export default defineExtension({ components: { WhisparrSyncPage } });
