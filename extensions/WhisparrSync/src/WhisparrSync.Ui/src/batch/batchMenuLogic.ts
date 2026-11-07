/**
 * What the videos selection bar offers over a selection of scenes.
 *
 * The rows are a constant because the action reaches the manifest on v3 alone, so nothing has to
 * be read before the overlay opens. The order is the invariant: safest first, the only row that
 * can download fourth, and the row that changes what Whisparr accepts in future last.
 */
import {
  MENU_ADD,
  MENU_EXCLUDE,
  MENU_MONITOR,
  MENU_UNMONITOR,
  SCENE_SEARCH,
} from "../common/ui/copy";
import type { SceneBatchVerb } from "../wire/api";

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
