/**
 * What the videos selection bar offers over a selection of scenes.
 *
 * The rows are a constant rather than a derivation over what the connected instance can do. This
 * action reaches the manifest on the newer generation alone, so there is nothing to read before the
 * overlay opens and no row that can turn out not to be offered.
 *
 * The order is the invariant this module holds: safest first, the only row that can download fourth,
 * and the row that changes what Whisparr accepts in future last. Nothing here sorts.
 *
 * Every row is labelled from the menu set rather than from the scene tab's, because the panel above
 * the rows is already headed with the product's name.
 */
import {
  MENU_ADD,
  MENU_EXCLUDE,
  MENU_MONITOR,
  MENU_UNMONITOR,
  SCENE_SEARCH,
} from "../common/ui/copy";
import type { SceneBatchVerb } from "../wire/api";

/** One row the batch overlay offers, already decided. */
export interface BatchMenuRow {
  readonly key: string;
  readonly label: string;
  readonly verb: NonNullable<SceneBatchVerb>;
}

export const BATCH_MENU_ROWS: readonly BatchMenuRow[] = [
  { key: "add", label: MENU_ADD, verb: "add" },
  { key: "monitor", label: MENU_MONITOR, verb: "monitor" },
  { key: "unmonitor", label: MENU_UNMONITOR, verb: "unmonitor" },
  { key: "search", label: SCENE_SEARCH, verb: "search" },
  { key: "exclude", label: MENU_EXCLUDE, verb: "exclude" },
];
