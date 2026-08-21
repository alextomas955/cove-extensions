/**
 * Pure, DOM-free derivation of a row's warning badges. A badge comes from the row's `status` STRING
 * enum PLUS its advisory bools (`suffixed`, `sanitized`, `inFlightPathOverflow`,
 * `offLibraryDestination`) — there is NO `flags[]` array on /preview.
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
 * What a badge is derived from: the row's outcome, plus the advisories that qualify it. Declared
 * structurally rather than as `PreviewItemView` so a `/preview` item and a leaner `/scan-rows` row
 * both qualify without either wire shape gaining a field only the badges would read.
 *
 * No count here, deliberately — the interface below states how many there are by existing, and a
 * number restating it is a second place to edit that nobody remembers. This one was already one short
 * before the field after it was added.
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
  /**
   * Required for the same reason as the flag above, and it is the ONE warning here the user cannot
   * derive from anything else on the row: whether a destination sits inside a Cove library path is a
   * fact about the host's configuration, which no client holds.
   */
  offLibraryDestination: boolean;
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
 * So: no index signature and no optional keys — either would restore the exact silence the type exists
 * to remove, because either lets a new status compile with no decision made about it.
 *
 * A runtime fallback is a different question, and the distinction matters: the type protects against a
 * status this bundle was never built for, while the fallback protects against one the RUNNING server
 * grew after this bundle shipped. Only CI's regeneration can enforce the first; nothing at build time
 * can see the second, which is reachable whenever a locally rebuilt DLL meets a stale bundle. So the
 * fallback below is deliberately NOT a default entry in the map (that would hide a missing decision at
 * compile time) but a lookup guard, and it surfaces rather than hides — matching `ScanBucket.Of`'s rule
 * that an unrecognised status must be visible for review, never hidden AND never thrown. Dereferencing
 * the lookup unguarded would throw inside a virtualised list row, and an uncaught throw in render tears
 * down the whole extension surface the host mounted, not just this pill.
 */
const STATUS_BADGING: Record<RenamerStatus, StatusBadging> = {
  // The two acting statuses: no badge of their own, and the only readers of the advisory flags.
  rename: { badge: null, readsAdvisoryFlags: true },
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
  // Split out of `skipCollision`, which badged all three of these as a name conflict. The labels name
  // what the user must change, because that is the whole reason they are three rather than one: a
  // conflict clears when the other file moves, a permission has to be widened, and a length has to be
  // shortened.
  skipNotAllowed: {
    badge: { label: "Skipped — destination not allowed", variant: "amber" },
    readsAdvisoryFlags: false,
  },
  skipTooLong: {
    badge: { label: "Skipped — path too long", variant: "amber" },
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
  // Planner-produced, so it does reach a row. The label names the file's situation rather than the
  // template's, because that is the half the user can act on.
  skipUnanchored: {
    badge: { label: "Skipped — outside every Cove library path", variant: "amber" },
    readsAdvisoryFlags: false,
  },
  skipRootMissing: {
    badge: { label: "Skipped — destination root no longer exists", variant: "amber" },
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
  // Also executor-only: the write-boundary guard refuses a destination at move time.
  skipBlocked: { badge: null, readsAdvisoryFlags: false },
};

/**
 * The lookup guard {@link STATUS_BADGING} describes: reached only on version skew, never on a missing
 * decision. It carries a LABEL rather than `null` because a row the user is about to approve must not
 * be silently uncounted.
 */
const UNKNOWN_STATUS_BADGING: StatusBadging = {
  badge: { label: "Skipped — unrecognised status", variant: "amber" },
  readsAdvisoryFlags: false,
};

/**
 * The one place the wire's word is taken over the type's.
 *
 * `RenamerStatus` is a claim about what the server sends, checked by the compiler against itself and
 * never against the server. So the map is exhaustive by TYPE while the lookup can still miss at RUNTIME,
 * and the widening here is what lets that fact be expressed: typed as declared, `no-unnecessary-condition`
 * correctly reports the guard as dead, because to the compiler it is. Narrow and commented rather than
 * loosening `Badgeable.status`, which would cost every call site its compile-time check to describe a
 * case only this lookup meets.
 */
function badgingFor(status: string): StatusBadging {
  return (
    (STATUS_BADGING as Record<string, StatusBadging | undefined>)[status] ?? UNKNOWN_STATUS_BADGING
  );
}

/**
 * Map an item to its badges (one per warning kind, with user-facing labels).
 * Rename/Move with no extra signal returns [] (the positive default, no badge). suffixed/sanitized and
 * an off-library destination add amber advisory badges even on a will-rename row; an in-flight path
 * overflow adds a red one, because that row's move cannot complete rather than merely completing
 * differently.
 */
export function badgesFor(item: Badgeable): Badge[] {
  const badges: Badge[] = [];
  const badging = badgingFor(item.status);
  if (badging.badge !== null) badges.push(badging.badge);
  if (badging.readsAdvisoryFlags) {
    if (item.suffixed) badges.push({ label: "Numbered to avoid a clash", variant: "amber" });
    if (item.sanitized) badges.push({ label: "Cleaned for the filesystem", variant: "amber" });
  }
  // Amber, and before the overflow badge: this row's rename WILL happen and will happen exactly as
  // previewed. What it costs is stated in the docs rather than in the pill, because the pill has room
  // for the fact and not for its consequence.
  if (item.offLibraryDestination) {
    badges.push({ label: "Lands outside your Cove library", variant: "amber" });
  }
  // No status guard: the server sets this flag only on an acting cross-volume item, and re-testing the
  // status here would let a flag the server DID set go unrendered whenever the two vocabularies drifted.
  // Red rather than amber — the other two on a will-rename row are advisory, this one means the move
  // cannot complete.
  const overflow = inFlightOverflowLabel(item);
  if (overflow !== null) badges.push({ label: overflow, variant: "red" });
  return badges;
}
