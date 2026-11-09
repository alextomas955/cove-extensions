/**
 * Bundle entry.
 *
 * The `components` key `RenamerPage` must equal the C# manifest's componentName for the settings
 * section, and the `actionHandlers` key `renamerSelected` must equal the bulk action's HandlerName.
 * Both are read off this default export by the host loader.
 */
import type { ExtensionAction } from "@cove/host/api/types";
import type { ExtensionComponent } from "@cove/host/extensions/ExtensionComponentRegistry";
import type { ExtensionRuntimeRegistration } from "@cove/host/extensions/ExtensionRuntimeReconciler";
import type { ActionPayload } from "@cove-extensions/ui-shared";

// Imported for its effect: it registers the interceptor that turns a failed request from the
// generated client into an ApiError carrying the status, which every error branch below reads.
import "./common/lib/wireErrors";
import { RenamePage } from "./settings/RenamePage";
import { renameSelected } from "./rename-action/renameSelected";

/**
 * What the host loader reads off an extension bundle, as the host declares it.
 *
 * `ExtensionRuntimeRegistration` defaults both maps to `unknown`, which is what the loader accepts
 * from any bundle. Naming what this one registers narrows them: a non-function value is what the
 * loader answers with a TypeError that fails the whole bundle.
 *
 * The payload is the one part Cove does not state. Its handler type takes `Record<string, unknown>`;
 * what a bulk action is actually handed is `ActionPayload`.
 */
type ExtensionModule = ExtensionRuntimeRegistration<
  ExtensionComponent,
  (action: ExtensionAction, payload: ActionPayload) => unknown
>;

export default {
  components: { RenamerPage: RenamePage },
  actionHandlers: { renamerSelected: renameSelected },
} satisfies ExtensionModule;
