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
  SYNC_COUNT,
  SYNC_DOWNLOADS_NOTHING,
  SYNC_IS_COUNTING,
  SYNC_IS_STARTING,
  SYNC_NEEDS_A_COUNT_FIRST,
  SYNC_NOTHING_LEFT_TO_SYNC,
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
 * The three counts a preview answers with, structurally: the identified scenes the instance does not
 * hold, the ones it does, and the ones carrying no metadata id.
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
}

/** How many scenes the run offers: every scene the reader owns that carries a metadata id. */
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
 */
export function syncConfirmation(counts: SyncCounts, monitorAlso: boolean): string {
  const count = offered(counts);
  const covers =
    count === 1
      ? "This offers the 1 scene you own to Whisparr"
      : `This offers all ${groupThousands(count)} scenes you own to Whisparr`;

  const skips =
    counts.skipped === 0
      ? ""
      : counts.skipped === 1
        ? ", and skips 1 that carries no metadata id"
        : `, and skips ${groupThousands(counts.skipped)} that carry no metadata id`;

  const monitoring = monitorAlso
    ? `It also marks each of them monitored. ${MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF}`
    : "It monitors nothing.";

  return `${covers}${skips}. ${monitoring} ${SYNC_DOWNLOADS_NOTHING}`;
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
  if (state.counts === null) return SYNC_NEEDS_A_COUNT_FIRST;
  if (!state.monitorAlso && state.counts.notYetThere === 0) return SYNC_NOTHING_LEFT_TO_SYNC;
  return null;
}

/**
 * The one reason the monitor choice cannot be made, or null.
 *
 * A run in flight is the only thing the choice could contradict: it is read at press time, so
 * nothing else about the page's state makes it unavailable. It carries no version refusal - the
 * choice is honoured on both generations.
 */
export function monitorToggleReason(state: SyncControlState): string | null {
  if (state.syncRunning) return SYNC_ALREADY_RUNNING;
  if (state.starting) return SYNC_IS_STARTING;
  return null;
}
