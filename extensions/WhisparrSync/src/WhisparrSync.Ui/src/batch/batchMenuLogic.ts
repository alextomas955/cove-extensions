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
 * Every row is labelled with the name the product already uses for that verb, because one verb keeps
 * one name across the product.
 */
import {
  MONITOR_IN_WHISPARR,
  SCENE_ADD,
  SCENE_EXCLUDE,
  SCENE_SEARCH,
  STOP_MONITORING_IN_WHISPARR,
} from "../common/ui/copy";
import type { SceneBatchVerb } from "../wire/api";

/** One row the batch overlay offers, already decided. */
export interface BatchMenuRow {
  readonly key: string;
  readonly label: string;
  readonly verb: NonNullable<SceneBatchVerb>;
}

export const BATCH_MENU_ROWS: readonly BatchMenuRow[] = [
  { key: "add", label: SCENE_ADD, verb: "add" },
  { key: "monitor", label: MONITOR_IN_WHISPARR, verb: "monitor" },
  { key: "unmonitor", label: STOP_MONITORING_IN_WHISPARR, verb: "unmonitor" },
  { key: "search", label: SCENE_SEARCH, verb: "search" },
  { key: "exclude", label: SCENE_EXCLUDE, verb: "exclude" },
];
