/**
 * How the sync section's numbers read, and what its count control says at each state.
 *
 * Relative imports only, and there are none: this module runs with no environment.
 */
import {
  ACTION_REFRESH,
  CONNECT_NOT_CONFIGURED,
  MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF,
  SYNC_ALREADY_RUNNING,
  SYNC_ALSO_MONITORS_EACH,
  SYNC_COUNT,
  SYNC_DOWNLOADS_NOTHING,
  SYNC_IS_COUNTING,
  SYNC_IS_STARTING,
  SYNC_MONITORS_NOTHING,
  SYNC_NEEDS_A_COUNT_FIRST,
  SYNC_NOTHING_LEFT_TO_SYNC,
  SYNC_OFFERS_ONE_SCENE,
  SYNC_OFFERS_ONE_SITE,
  SYNC_REGISTERS_THE_SCENES_YOU_OWN,
  SYNC_REGISTERS_THE_STUDIOS_YOU_OWN,
  SYNC_SITE_ALSO_MONITORS_THE_SCENES_ON_THEM,
  SYNC_SITE_DOWNLOADS_NOTHING,
  SYNC_SITE_NEEDS_A_COUNT_FIRST,
  SYNC_SITE_NOTHING_LEFT_TO_SYNC,
  SYNC_SITE_SKIPPED_CANNOT_BE_REGISTERED,
  SYNC_SKIPPED_CANNOT_BE_REGISTERED,
  syncOffersScenes,
  syncOffersSites,
} from "../common/ui/copy";

/**
 * `n` with a separator every three digits.
 *
 * Hand-written rather than `Intl.NumberFormat` or `toLocaleString`, for the reason the relative-time
 * module already states: a locale-dependent rendering makes the same value read differently in two
 * places and gives a test nothing fixed to assert. The server's own side of these sentences uses the
 * invariant culture, which produces this same grouping.
 */
export function groupThousands(n: number): string {
  const whole = Math.trunc(Math.abs(n));
  const digits = String(whole);
  let grouped = "";
  for (let i = 0; i < digits.length; i++) {
    if (i > 0 && (digits.length - i) % 3 === 0) grouped += ",";
    grouped += digits[i];
  }
  return n < 0 ? `-${grouped}` : grouped;
}

/**
 * Every sentence the sync section states in the noun of whatever the run registers.
 *
 * A resolved set rather than a flag: the section renders the strings it is given, so there is one
 * place the noun is chosen and no second place that can disagree with it.
 */
export interface SyncSentences {
  /** What the card offers, stated under its title. */
  readonly description: string;
  /** What the skipped count means, and what a reader can do about it. */
  readonly skippedRemedy: string;
  /** Why the sync control cannot be pressed before a count exists. */
  readonly needsACountFirst: string;
  /** Why there is nothing left for the run to register. Claims nothing about monitoring. */
  readonly nothingLeftToSync: string;
  /** The clause the confirmation ends on at every size. */
  readonly downloadsNothing: string;
  /** What the confirmation covers where the run offers one. */
  readonly offersOne: string;
  /** What it covers at any other size, given the figure already grouped. */
  readonly offersMany: (grouped: string) => string;
  /** What the monitor choice adds to the run, with the choice on. */
  readonly alsoMonitors: string;
}

const SCENE_SENTENCES: SyncSentences = {
  description: SYNC_REGISTERS_THE_SCENES_YOU_OWN,
  skippedRemedy: SYNC_SKIPPED_CANNOT_BE_REGISTERED,
  needsACountFirst: SYNC_NEEDS_A_COUNT_FIRST,
  nothingLeftToSync: SYNC_NOTHING_LEFT_TO_SYNC,
  downloadsNothing: SYNC_DOWNLOADS_NOTHING,
  offersOne: SYNC_OFFERS_ONE_SCENE,
  offersMany: syncOffersScenes,
  alsoMonitors: SYNC_ALSO_MONITORS_EACH,
};

const SITE_SENTENCES: SyncSentences = {
  description: SYNC_REGISTERS_THE_STUDIOS_YOU_OWN,
  skippedRemedy: SYNC_SITE_SKIPPED_CANNOT_BE_REGISTERED,
  needsACountFirst: SYNC_SITE_NEEDS_A_COUNT_FIRST,
  nothingLeftToSync: SYNC_SITE_NOTHING_LEFT_TO_SYNC,
  downloadsNothing: SYNC_SITE_DOWNLOADS_NOTHING,
  offersOne: SYNC_OFFERS_ONE_SITE,
  offersMany: syncOffersSites,
  alsoMonitors: SYNC_SITE_ALSO_MONITORS_THE_SCENES_ON_THEM,
};

/**
 * What a run registers, as the preview read answers it.
 *
 * Declared here rather than imported from the wire module, so this module stays free of every other
 * module. The read's own value satisfies it.
 */
export type SyncRegisters = "scenes" | "sites";

/**
 * The sentences to state, chosen from the read's own value.
 *
 * @param registers what the preview read said the run registers, or null before any read has
 *   answered - which leaves the scene set standing, since nothing yet says otherwise
 */
export function syncSentences(registers: SyncRegisters | null): SyncSentences {
  return registers === "sites" ? SITE_SENTENCES : SCENE_SENTENCES;
}

/** What the count control is called, and why it cannot be pressed. */
export interface CountControl {
  readonly name: string;
  /** Why the control is unavailable, or null when it is available. */
  readonly reason: string | null;
}

/**
 * The count control's name and its one reason.
 *
 * The name is one of two fixed constants and interpolates neither, so the control has no
 * variable-length text. A failed count leaves it pressable: the failure belongs in the preview
 * region, not on the control that would retry it.
 *
 * @param counting whether a count is in flight
 * @param hasResult whether a count has already answered
 */
export function countControl(counting: boolean, hasResult: boolean): CountControl {
  return {
    name: hasResult ? ACTION_REFRESH : SYNC_COUNT,
    reason: counting ? SYNC_IS_COUNTING : null,
  };
}

/**
 * The three counts a preview answers with, structurally: the identified entries the instance does
 * not hold, the ones it does, and the ones carrying no metadata id. What an entry is follows what
 * the run registers there.
 *
 * Declared here rather than imported from the wire module, so this module stays free of every other
 * module. The read's own view satisfies it.
 */
export interface SyncCounts {
  readonly notYetThere: number;
  readonly alreadyThere: number;
  readonly skipped: number;
}

/** Everything the sync control and the monitor choice read to answer. */
export interface SyncControlState {
  /** The page's own reason nothing here can act on the stored connection, or null. */
  readonly sharedReason: string | null;
  /** Whether the read answered that no instance is connected. */
  readonly noConnection: boolean;
  /** Whether a library sync is already in flight. */
  readonly syncRunning: boolean;
  /** Whether the enqueue request itself is in flight. */
  readonly starting: boolean;
  /** The counts held, or null when none are. */
  readonly counts: SyncCounts | null;
  /** The monitor choice as it stands, which reason 6 below depends on. */
  readonly monitorAlso: boolean;
  /** The sentences to state, in the noun of whatever the run registers. */
  readonly sentences: SyncSentences;
}

/** How many entries the run offers: every one in the reader's library that carries a metadata id. */
function offered(counts: SyncCounts): number {
  return counts.notYetThere + counts.alreadyThere;
}

/**
 * What the confirmation states before the run starts: what it covers, what monitoring does, and what
 * registering does not do.
 *
 * The last clause is the reason the dialog exists, so it is stated at every size: a gesture reaching
 * a whole library reads as a download of that size otherwise.
 *
 * @param counts the three figures the preview answered with
 * @param monitorAlso the monitor choice as it stands at the press
 * @param sentences the set to compose from, which decides the noun every clause is stated in
 */
export function syncConfirmation(
  counts: SyncCounts,
  monitorAlso: boolean,
  sentences: SyncSentences,
): string {
  const count = offered(counts);
  const covers = count === 1 ? sentences.offersOne : sentences.offersMany(groupThousands(count));

  const skips =
    counts.skipped === 0
      ? ""
      : counts.skipped === 1
        ? ", and skips 1 that carries no metadata id"
        : `, and skips ${groupThousands(counts.skipped)} that carry no metadata id`;

  const monitoring = monitorAlso
    ? `${sentences.alsoMonitors} ${MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF}`
    : SYNC_MONITORS_NOTHING;

  return `${covers}${skips}. ${monitoring} ${sentences.downloadsNothing}`;
}

/**
 * The one reason the sync control cannot be pressed, or null.
 *
 * Exactly one sentence, in the declared order: a control with several reasons states one rather than
 * three. The no-connection reason is the connect surface's own, which says "above" and so points at
 * the form on this page; the monitor refusal that ends by naming this page would point at itself.
 *
 * The last reason reads the monitor choice, because the run marks every scene the reader owns
 * monitored, including one the instance already holds. With that choice on, a library Whisparr
 * already holds in full still has work to do.
 */
export function syncDisabledReason(state: SyncControlState): string | null {
  if (state.sharedReason !== null) return state.sharedReason;
  if (state.noConnection) return CONNECT_NOT_CONFIGURED;
  if (state.syncRunning) return SYNC_ALREADY_RUNNING;
  if (state.starting) return SYNC_IS_STARTING;
  if (state.counts === null) return state.sentences.needsACountFirst;
  if (!state.monitorAlso && state.counts.notYetThere === 0)
    return state.sentences.nothingLeftToSync;
  return null;
}

/**
 * The one reason the monitor choice cannot be made, or null.
 *
 * A run in flight is the only thing the choice could contradict: it is read at press time, so
 * nothing else about the page's state makes it unavailable. It carries no refusal beyond that one,
 * and a scene the run could not mark is counted in the run's own ending rather than refused here.
 */
export function monitorToggleReason(state: SyncControlState): string | null {
  if (state.syncRunning) return SYNC_ALREADY_RUNNING;
  if (state.starting) return SYNC_IS_STARTING;
  return null;
}
