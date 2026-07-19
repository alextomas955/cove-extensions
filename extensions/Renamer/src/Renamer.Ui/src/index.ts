/**
 * Bundle entry. The default export is the extension module; the
 * `components` map KEY `RenamerPage` MUST equal the C# manifest `componentName`
 * so the host resolves the registered component to render for the settings page.
 *
 * The default export ALSO carries an `actionHandlers` map. The SDK `ExtensionModule`
 * type does NOT declare `actionHandlers` (see sdk/frontend/dist/types.d.ts), but the host
 * loader reads `mod.default.actionHandlers` UNTYPED. So we attach it via a local cast — NOT by
 * editing the SDK. The handler key `renamerSelected` MUST match the action's HandlerName.
 */
import { defineExtension } from "@cove/extension-sdk";
import { RenamePage } from "./RenamePage";
import { renameSelected } from "./renameSelected";

interface WithActionHandlers {
  actionHandlers: Record<string, unknown>;
}

// `RenamerPage` key MUST be byte-identical to the C# manifest componentName (Renamer.Api.cs
// AddSettingsSection) and the host resolveComponent lookup — one literal, three places that must
// all agree.
const mod = defineExtension({ components: { RenamerPage: RenamePage } });
(mod as typeof mod & WithActionHandlers).actionHandlers = { renamerSelected: renameSelected };

export default mod;
