/**
 * How a failed request reads to a user, stated once for every panel surface that catches one.
 *
 * Lives in `common/` rather than as a `*Logic.ts` because it reaches the host request module for
 * `ApiError`, and the purity rule allows a `*Logic.ts` relative imports only — the name alone would
 * make that import a lint error. The pure path helpers are `pathLogic.ts` for the same reason read
 * the other way: importing them from here would put this host import in their consumers' graphs.
 */
import { ApiError } from "@cove-extensions/ui-shared/extensionRequest";

/** An unknown thrown value as user-facing text; an ApiError keeps its status and its body. */
export function errText(err: unknown): string {
  return err instanceof ApiError ? `${err.status} ${err.body}` : String(err);
}
