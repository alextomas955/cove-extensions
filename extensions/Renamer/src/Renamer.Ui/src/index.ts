/**
 * Bundle entry. The default export is the extension module; the
 * `components` map KEY `RenamerPage` MUST equal the C# manifest `componentName`
 * so the host resolves the registered component to render for the settings page.
 *
 * The default export ALSO carries an `actionHandlers` map, which the SDK types as
 * `Record<string, ExtensionActionHandler>`. That handler type erases the payload to
 * `Record<string, unknown>`, because one signature has to cover every action kind. The narrowing
 * below rests on both host payload builders — the bulk-selection one and the per-entity one —
 * emitting `entityType` plus an `entityIds` array (`ActionPayload`). It is an unchecked assertion
 * about the host, not something the compiler can check, so host drift surfaces at runtime rather
 * than here. The handler key `renamerSelected` MUST match the action's HandlerName.
 */
import { defineExtension } from "@cove/extension-sdk";
import type { ActionPayload } from "@cove-extensions/ui-shared";
import { RenamePage } from "./settings/RenamePage";
import { renameSelected } from "./rename-action/renameSelected";

// `RenamerPage` key MUST be byte-identical to the C# manifest componentName (Renamer.Api.cs
// AddSettingsSection) and the host resolveComponent lookup — one literal, three places that must
// all agree.
const mod = defineExtension({ components: { RenamerPage: RenamePage } });
mod.actionHandlers = {
  renamerSelected: (action, payload) => renameSelected(action, payload as unknown as ActionPayload),
};

export default mod;
