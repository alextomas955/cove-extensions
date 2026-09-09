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
  BATCH_ADD_STATES,
  BATCH_EXCLUDE_STATES,
  BATCH_MONITOR_STATES,
  BATCH_SEARCH_STATES,
  BATCH_UNMONITOR_STATES,
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
  /** What is stated beneath it, in the order it reads. */
  readonly sentences: readonly string[];
  readonly verb: NonNullable<SceneBatchVerb>;
}

export const BATCH_MENU_ROWS: readonly BatchMenuRow[] = [
  { key: "add", label: SCENE_ADD, sentences: [BATCH_ADD_STATES], verb: "add" },
  {
    key: "monitor",
    label: MONITOR_IN_WHISPARR,
    sentences: [BATCH_MONITOR_STATES],
    verb: "monitor",
  },
  {
    key: "unmonitor",
    label: STOP_MONITORING_IN_WHISPARR,
    sentences: [BATCH_UNMONITOR_STATES],
    verb: "unmonitor",
  },
  { key: "search", label: SCENE_SEARCH, sentences: [BATCH_SEARCH_STATES], verb: "search" },
  { key: "exclude", label: SCENE_EXCLUDE, sentences: [BATCH_EXCLUDE_STATES], verb: "exclude" },
];
