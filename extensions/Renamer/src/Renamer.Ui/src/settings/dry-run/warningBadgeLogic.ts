/**
 * Pure, DOM-free derivation of a row's warning badges. A badge comes from the row's `status` STRING
 * enum PLUS its advisory bools (`suffixed`, `sanitized`, `inFlightPathOverflow`) — there is NO
 * `flags[]` array on /preview.
 *
 * Deliberately a sibling of dryRunLogic.ts rather than an addition to it: that module's header claims
 * it is import-free, and the derivation here needs the generated wire union.
 */
import type { RenamerStatus } from "../../wire/api";
import { inFlightOverflowLabel } from "./dryRunLogic";

type Variant = "amber" | "gray" | "red";

/**
 * One rendered pill. Read-only because a status badge below is a shared constant rather than a fresh
 * object per row, so a caller that wrote through one would rewrite the copy every later row reads.
 */
export interface Badge {
  readonly label: string;
  readonly variant: Variant;
}

/**
 * The three fields a badge is derived from. Declared structurally rather than as `PreviewItemView` so
 * a `/preview` item and a leaner `/scan-rows` row both qualify without either wire shape gaining a
 * field only the badges would read.
 */
export interface Badgeable {
  status: RenamerStatus;
  suffixed: boolean;
  sanitized: boolean;
  /**
   * Required, because both wire shapes that reach a badge now carry it — `/preview`'s item view and the
   * paged `/scan-rows` row. Required rather than optional so a caller that forgets to pass it fails to
   * compile, instead of silently rendering a row whose warning was never asked for.
   */
  inFlightPathOverflow: boolean;
}

/**
 * What one status contributes to a row: the badge it earns, or `null` for a status that earns none,
 * plus whether it reads the row's advisory flags.
 *
 * Two fields rather than one because a bare status-to-badge map cannot express the shipped behaviour:
 * the two acting statuses earn no badge of their own and are the ONLY ones that read
 * `suffixed`/`sanitized` — a skipped row whose computed name was cleaned had nothing cleaned, because
 * nothing ran.
 */
export interface StatusBadging {
  readonly badge: Badge | null;
  readonly readsAdvisoryFlags: boolean;
}

/**
 * Every status the wire can carry, and the badge decision it was given.
 *
 * Total by TYPE, not by convention: keyed on the union generated from the extension's own OpenAPI
 * document, so a status the server grows fails THIS build (TS2741, naming the missing key) the moment
 * the wire types are regenerated. That is the guarantee, and its price is stated rather than hidden —
 * every new status now needs a decision at this site instead of defaulting to silence. Which is the
 * trade worth making here, because the silent-default failure has already happened in this very
 * function: `skipExcluded` reached both wire shapes with no case at all and rendered nothing.
 *
 * So: no index signature, no optional keys, no default entry and no runtime fallback. Any of them
 * would restore the exact silence the type exists to remove.
 */
const STATUS_BADGING: Record<RenamerStatus, StatusBadging> = {
  // The two acting statuses: no badge of their own, and the only readers of the advisory flags.
  renamer: { badge: null, readsAdvisoryFlags: true },
  move: { badge: null, readsAdvisoryFlags: true },
  noOp: { badge: { label: "No change needed", variant: "gray" }, readsAdvisoryFlags: false },
  skipGated: {
    badge: { label: "Skipped — needs a required field", variant: "amber" },
    readsAdvisoryFlags: false,
  },
  skipCollision: {
    badge: { label: "Skipped — name conflict", variant: "amber" },
    readsAdvisoryFlags: false,
  },
  skipExcluded: {
    badge: { label: "Skipped — an exclude rule matched", variant: "amber" },
    readsAdvisoryFlags: false,
  },
  skipLocked: {
    badge: { label: "Skipped — file in use", variant: "amber" },
    readsAdvisoryFlags: false,
  },
  skipMissingSource: {
    badge: { label: "Skipped — file missing on disk", variant: "amber" },
    readsAdvisoryFlags: false,
  },
  failed: { badge: { label: "Failed — rolled back", variant: "red" }, readsAdvisoryFlags: false },
  // The five below reach no preview or scan row, so none earns copy — inventing a label for one would
  // ship dead text, and the reason each is unreachable is recorded instead.
  //
  // Executor-only, and produced only AFTER the confirm gate: by the time the OS refuses a move, the
  // read-back mismatches or a host shutdown interrupts the copy, the user has already approved.
  skipPermissionDenied: { badge: null, readsAdvisoryFlags: false },
  skipVerifyFailed: { badge: null, readsAdvisoryFlags: false },
  skipCancelled: { badge: null, readsAdvisoryFlags: false },
  // Log-only: a disk-full skip is reported through the run log and never becomes an item result at all.
  skipNoSpace: { badge: null, readsAdvisoryFlags: false },
  // Executor-only on the same post-confirm path: the write-boundary guard refuses a destination at move
  // time, long past any preview.
  skipBlocked: { badge: null, readsAdvisoryFlags: false },
};

/**
 * Map an item to its badges (one per warning kind, with user-facing labels).
 * Rename/Move with no extra signal returns [] (the positive default, no badge). suffixed/sanitized
 * add amber advisory badges even on a will-rename row; an in-flight path overflow adds a red one,
 * because that row's move cannot complete rather than merely completing differently.
 */
export function badgesFor(item: Badgeable): Badge[] {
  const badges: Badge[] = [];
  const badging = STATUS_BADGING[item.status];
  if (badging.badge !== null) badges.push(badging.badge);
  if (badging.readsAdvisoryFlags) {
    if (item.suffixed) badges.push({ label: "Numbered to avoid a clash", variant: "amber" });
    if (item.sanitized) badges.push({ label: "Cleaned for the filesystem", variant: "amber" });
  }
  // No status guard: the server sets this flag only on an acting cross-volume item, and re-testing the
  // status here would let a flag the server DID set go unrendered whenever the two vocabularies drifted.
  // Red rather than amber — the other two on a will-rename row are advisory, this one means the move
  // cannot complete.
  const overflow = inFlightOverflowLabel(item);
  if (overflow !== null) badges.push({ label: overflow, variant: "red" });
  return badges;
}
