/**
 * Bundle entry.
 *
 * The `components` key `RenamerPage` must equal the C# manifest's componentName for the settings
 * section, and the `actionHandlers` key `renamerSelected` must equal the bulk action's HandlerName.
 * Both are read off this default export by the host loader.
 */

// Imported for its effect: it registers the interceptor that turns a failed request from the
// generated client into an ApiError carrying the status, which every error branch below reads.
import "./common/lib/wireErrors";
import { RenamePage } from "./settings/RenamePage";
import { renameSelected } from "./rename-action/renameSelected";

/**
 * What the host loader reads off an extension bundle.
 *
 * Declared here rather than taken from a package: the only thing an SDK offered for this was an
 * identity function, whose module type did not declare `actionHandlers` and so had to be cast
 * around at the one call site it had.
 */
interface ExtensionModule {
  components: Record<string, unknown>;
  actionHandlers: Record<string, unknown>;
}

export default {
  components: { RenamerPage: RenamePage },
  actionHandlers: { renamerSelected: renameSelected },
} satisfies ExtensionModule;
