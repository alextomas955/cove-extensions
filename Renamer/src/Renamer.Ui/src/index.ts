/**
 * Bundle entry. The default export is the extension module; the
 * `components` map KEY `RenameSettingsPanel` MUST equal the C# manifest `componentName`
 * so the host resolves the registered component to render in the settings section.
 *
 * The default export ALSO carries an `actionHandlers` map. The SDK `ExtensionModule`
 * type does NOT declare `actionHandlers` (see sdk/frontend/dist/types.d.ts), but the host
 * loader reads `mod.default.actionHandlers` UNTYPED. So we attach it via a local cast — NOT by
 * editing the SDK. The handler key `renameSelected` MUST match the action's HandlerName.
 */
import { defineExtension } from "@cove/extension-sdk";
import { RenameSettingsPanel } from "./RenameSettingsPanel";
import { RenamePage } from "./RenamePage";
import { renameSelected } from "./renameSelected";

interface WithActionHandlers {
  actionHandlers: Record<string, unknown>;
}

// `RenamePage` key MUST be byte-identical to the C# manifest componentName (Renamer.Api.cs AddPage)
// and the host resolveComponent lookup — one literal, three places that must all agree.
const mod = defineExtension({ components: { RenameSettingsPanel, RenamePage } });
(mod as typeof mod & WithActionHandlers).actionHandlers = { renameSelected };

export default mod;
