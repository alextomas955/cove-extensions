/**
 * How the sync section's numbers read, and what its count control says at each state.
 *
 * Relative imports only, so this module runs with no environment.
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
  SYNC_ALSO_LINKS_WHAT_YOU_OWN,
  SYNC_MONITORS_NOTHING,
  SYNC_NEEDS_A_COUNT_FIRST,
  SYNC_NOTHING_LEFT_TO_SYNC,
  SYNC_OFFERS_ONE_SCENE,
  SYNC_OFFERS_ONE_SITE,
  SYNC_REGISTERS_THE_SCENES_YOU_OWN,
  SYNC_REGISTERS_THE_STUDIOS_YOU_OWN,
  SYNC_SITE_ALSO_LINKS_WHAT_YOU_OWN,
  SYNC_SITE_ALSO_MONITORS_THE_SCENES_ON_THEM,
  SYNC_SITE_DOWNLOADS_NOTHING,
  SYNC_SITE_NEEDS_A_COUNT_FIRST,
  SYNC_SITE_SKIPPED_CANNOT_BE_REGISTERED,
  SYNC_SKIPPED_CANNOT_BE_REGISTERED,
  SYNC_SKIPS_ONE,
  syncOffersScenes,
  syncOffersSites,
  syncSkipsMany,
  syncStillToHandOver,
} from "../common/ui/copy";

/**
 * `n` with a separator every three digits.
 *
 * Hand-written rather than `Intl.NumberFormat` or `toLocaleString`: a locale-dependent rendering
 * makes the same value read differently in two places. The server's own side of these sentences
 * uses the invariant culture, which produces this same grouping.
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
 * A resolved set rather than a flag, so there is one place the noun is chosen.
 */
export interface SyncSentences {
  readonly description: string;
  readonly skippedRemedy: string;
  readonly needsACountFirst: string;
  /**
   * Why there is nothing left for the run to do, or null where a run always has some.
   *
   * Null on the site pass. What the instance publishes per site is whether it recorded any file,
   * not how many of the site's files are still to hand over, so a site holding five of the reader's
   * files with one recorded reads as done. An exact figure costs a read per site, which grows with
   * the library. The run there also rebuilds the tree, follows a file that changed drive and takes
   * back a name whose library file is gone, so it has work on most runs whatever the figure says.
   */
  readonly nothingLeftToSync: string | null;
  readonly downloadsNothing: string;
  readonly offersOne: string;
  /** What the confirmation covers at any other size, given the figure already grouped. */
  readonly offersMany: (grouped: string) => string;
  readonly alsoMonitors: string;
  /** What the run does with the files the reader owns. Both runs link, in their own words. */
  readonly alsoLinks: string;
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
  alsoLinks: SYNC_ALSO_LINKS_WHAT_YOU_OWN,
};

const SITE_SENTENCES: SyncSentences = {
  description: SYNC_REGISTERS_THE_STUDIOS_YOU_OWN,
  skippedRemedy: SYNC_SITE_SKIPPED_CANNOT_BE_REGISTERED,
  needsACountFirst: SYNC_SITE_NEEDS_A_COUNT_FIRST,
  nothingLeftToSync: null,
  downloadsNothing: SYNC_SITE_DOWNLOADS_NOTHING,
  offersOne: SYNC_OFFERS_ONE_SITE,
  offersMany: syncOffersSites,
  alsoMonitors: SYNC_SITE_ALSO_MONITORS_THE_SCENES_ON_THEM,
  alsoLinks: SYNC_SITE_ALSO_LINKS_WHAT_YOU_OWN,
};

/**
 * What a run registers, as the preview read answers it.
 *
 * Declared here rather than imported from the wire module, so this module stays free of every other
 * module. The read's own value satisfies it.
 */
export type SyncRegisters = "scenes" | "sites";

/**
 * @param registers what the preview read said the run registers, or null before any read has
 *   answered, which leaves the scene set standing
 */
export function syncSentences(registers: SyncRegisters | null): SyncSentences {
  return registers === "sites" ? SITE_SENTENCES : SCENE_SENTENCES;
}

export interface CountControl {
  readonly name: string;
  /** Why the control is unavailable, or null when it is available. */
  readonly reason: string | null;
}

/**
 * The name is one of two fixed constants and interpolates neither, so the control has no
 * variable-length text. A failed count leaves it pressable: the failure belongs in the preview
 * region, not on the control that would retry it.
 */
export function countControl(counting: boolean, hasResult: boolean): CountControl {
  return {
    name: hasResult ? ACTION_REFRESH : SYNC_COUNT,
    reason: counting ? SYNC_IS_COUNTING : null,
  };
}

/**
 * The identified entries the instance does not hold, the ones it does, and the ones carrying no
 * metadata id. What an entry is follows what the run registers there.
 *
 * Declared here rather than imported from the wire module, so this module stays free of every other
 * module. The read's own view satisfies it.
 */
export interface SyncCounts {
  readonly notYetThere: number;
  readonly alreadyThere: number;
  readonly skipped: number;
  /** How many of the entries the instance holds it records no file against. */
  readonly withNoFileRecorded: number;
}

export interface SyncControlState {
  /** The page's own reason nothing here can act on the stored connection, or null. */
  readonly sharedReason: string | null;
  readonly noConnection: boolean;
  readonly syncRunning: boolean;
  /** Whether the enqueue request itself is in flight. */
  readonly starting: boolean;
  /** The counts held, or null when none are. */
  readonly counts: SyncCounts | null;
  readonly monitorAlso: boolean;
  readonly sentences: SyncSentences;
}

/** Every entry in the reader's library that carries a metadata id. */
function offered(counts: SyncCounts): number {
  return counts.notYetThere + counts.alreadyThere;
}

/**
 * What the confirmation states before the run starts.
 *
 * The clause saying registering downloads nothing is the reason the dialog exists, so it is stated
 * at every size: a gesture reaching a whole library reads as a download of that size otherwise.
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
        ? SYNC_SKIPS_ONE
        : syncSkipsMany(groupThousands(counts.skipped));

  // Stated before what the run does, because it is the reason to press where everything is already
  // registered. Left out at zero, where it would read as a report that nothing is outstanding and
  // then be followed by a run that hands files over anyway.
  const outstanding =
    counts.withNoFileRecorded === 0
      ? ""
      : `${syncStillToHandOver(groupThousands(counts.withNoFileRecorded))} `;

  const monitoring = monitorAlso
    ? `${sentences.alsoMonitors} ${MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF}`
    : SYNC_MONITORS_NOTHING;

  return (
    `${covers}${skips}. ${outstanding}${monitoring} ` +
    `${sentences.alsoLinks} ${sentences.downloadsNothing}`
  );
}

/**
 * The one reason the sync control cannot be pressed, or null.
 *
 * Exactly one sentence, in the declared order: a control with several reasons states one rather
 * than three.
 *
 * The last reason reads all three of what a run does. It registers what is not yet there, it hands
 * over the files of what is, and with the monitor choice on it marks every scene the reader owns
 * monitored, including one the instance already holds. Any one of the three outstanding is work,
 * so nothing is left only where all three are spent. It is stated only where the run can tell that
 * all three are spent, which the site pass cannot.
 */
export function syncDisabledReason(state: SyncControlState): string | null {
  if (state.sharedReason !== null) return state.sharedReason;
  if (state.noConnection) return CONNECT_NOT_CONFIGURED;
  if (state.syncRunning) return SYNC_ALREADY_RUNNING;
  if (state.starting) return SYNC_IS_STARTING;
  if (state.counts === null) return state.sentences.needsACountFirst;
  if (
    !state.monitorAlso &&
    state.sentences.nothingLeftToSync !== null &&
    state.counts.notYetThere === 0 &&
    state.counts.withNoFileRecorded === 0
  )
    return state.sentences.nothingLeftToSync;
  return null;
}

/**
 * The one reason the monitor choice cannot be made, or null.
 *
 * A run in flight is the only thing the choice could contradict: it is read at press time. A scene
 * the run could not mark is counted in the run's own ending rather than refused here.
 */
export function monitorToggleReason(state: SyncControlState): string | null {
  if (state.syncRunning) return SYNC_ALREADY_RUNNING;
  if (state.starting) return SYNC_IS_STARTING;
  return null;
}
