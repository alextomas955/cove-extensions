/**
 * Pure rules for the panel that adds an entity: which rows exist, what each one is called, and
 * which of them marks the back catalogue.
 *
 * Nothing here is decided from which generation is connected. The offer carries the modes the
 * connected instance can carry out for the entity, so a mode it cannot is absent rather than drawn
 * and refused on press.
 */
import {
  ADD_TO_WHISPARR_TRACK_ONLY,
  CAP_UNAVAILABLE_ON_THIS_GENERATION,
  NO_INSTANCE_CONNECTED,
  SCOPE_ALL_SCENES,
  SCOPE_FUTURE_SCENES,
} from "../common/ui/copy";
import { membersOf } from "../common/lib/totalTableLogic";
import type { MissingEntryMode, MissingEntryOffer } from "../wire/api";

/** What each mode is called, in the order the rows render. */
const ENTRY_LABEL: Record<MissingEntryMode, string> = {
  trackOnly: ADD_TO_WHISPARR_TRACK_ONLY,
  monitorNewReleases: SCOPE_FUTURE_SCENES,
  monitorAllScenes: SCOPE_ALL_SCENES,
};

/**
 * Which modes mark every scene the instance already lists as wanted.
 *
 * Total by type, so a mode added to the wire enum fails this build rather than reaching the server
 * with no confirmation in front of it.
 */
const MARKS_THE_BACK_CATALOGUE: Record<MissingEntryMode, boolean> = {
  trackOnly: false,
  monitorNewReleases: false,
  monitorAllScenes: true,
};

// The order the rows render in, which is the label table's own rather than the offer's, so the
// cheap option does not move under the cursor when the server names the modes in another order.
const ENTRY_MODE_ORDER: readonly MissingEntryMode[] = membersOf(ENTRY_LABEL);

/** One row the panel offers. */
export interface MissingEntryRow {
  readonly mode: MissingEntryMode;
  readonly label: string;
  /** Whether choosing it marks every scene Whisparr already lists as wanted. */
  readonly marksTheBackCatalogue: boolean;
}

/** What the panel offers for one entity against one connection. */
export interface MissingEntryChoice {
  readonly rows: readonly MissingEntryRow[];
  /** The one sentence saying why nothing is offered, or null when something is. */
  readonly reason: string | null;
  /** Whether the wider scope cannot be taken back on the connected generation. */
  readonly oneWayDoor: boolean;
}

/**
 * The rows `offer` leaves to choose from.
 *
 * An offer naming no mode is read apart two ways. A scope question settled neither way is an answer
 * that reached no instance; a settled one is an instance that declares no role reaching this
 * entity's kind.
 *
 * @param offer what the entity's own offer read answered
 */
export function missingEntryChoice(offer: MissingEntryOffer): MissingEntryChoice {
  const named = new Set(offer.modes);
  const oneWayDoor = offer.scopeChangeIsRetroactive === false;
  const rows = ENTRY_MODE_ORDER.filter((mode) => named.has(mode)).map((mode) => ({
    mode,
    label: ENTRY_LABEL[mode],
    marksTheBackCatalogue: MARKS_THE_BACK_CATALOGUE[mode],
  }));

  if (rows.length > 0) {
    return { rows, reason: null, oneWayDoor };
  }

  return {
    rows,
    reason:
      offer.scopeChangeIsRetroactive === null
        ? NO_INSTANCE_CONNECTED
        : CAP_UNAVAILABLE_ON_THIS_GENERATION,
    oneWayDoor,
  };
}
