/**
 * The controls above the grid.
 *
 * Refresh alone for now. The search field, the ordering menu and the facet menus render here beside
 * it, and each grows inside this file rather than in the tab shell.
 */
import { ACTION_REFRESH } from "../common/ui/copy";

export function MissingToolbar({ onRefresh }: { onRefresh: () => void }) {
  return (
    <div className="mb-3 flex items-center gap-2">
      <button
        type="button"
        onClick={onRefresh}
        className="rounded border border-border px-2.5 py-1.5 text-sm text-foreground hover:text-accent"
      >
        {ACTION_REFRESH}
      </button>
    </div>
  );
}
