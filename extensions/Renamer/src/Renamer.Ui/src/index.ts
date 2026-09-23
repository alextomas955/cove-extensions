/**
 * Bundle entry. The `components` key `RenamerPage` must equal the C# manifest's componentName for the
 * settings section, and the `actionHandlers` key `renamerSelected` must equal the bulk action's
 * HandlerName. The host loader reads `actionHandlers` although the SDK's module type does not declare
 * it, so it is attached through a local cast.
 */
import { defineExtension } from "@cove/extension-sdk";
import { RenamePage } from "./settings/RenamePage";
import { renameSelected } from "./rename-action/renameSelected";

interface WithActionHandlers {
  actionHandlers: Record<string, unknown>;
}

const mod = defineExtension({ components: { RenamerPage: RenamePage } });
(mod as typeof mod & WithActionHandlers).actionHandlers = { renamerSelected: renameSelected };

export default mod;
