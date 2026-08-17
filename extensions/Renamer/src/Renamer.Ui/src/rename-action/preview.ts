/**
 * The pure window.confirm summary builder for a real-selection `/preview`.
 *
 * CRITICAL: `/preview` returns `PreviewItemView[]`, NOT the `/preview-sample` `flags[]` array. The
 * warning taxonomy is derived from the `status` STRING enum (the endpoint's own result type writes
 * it as a camelCase string) PLUS the additive `suffixed` / `sanitized` bools the planner sets —
 * there is no `flags[]` field here.
 *
 * `buildConfirmSummary` is intentionally pure (no DOM, no fetch) so the confirm-dialog wording logic
 * can be unit-reasoned in isolation; the handler (renameSelected.ts) wraps it with window.confirm + fetch.
 * The wire shapes it consumes are generated from the extension's own OpenAPI document.
 */

import type { ConfirmLevel, PreviewItemView, PreviewSummary, RenamerStatus } from "../wire/api";
import { basename } from "../common/pathLogic";

const SAMPLE_LIMIT = 5;

/**
 * The two spellings one skip kind needs in the confirm text: the plural `clause` the multi-reason line
 * joins behind a count, and the singular `reason` the compact single-kind form parenthesises.
 */
interface SkipClause {
  readonly clause: string;
  readonly reason: string;
}

/**
 * Every status the wire can carry, and whether it counts as a skip in the confirm dialog.
 *
 * Total by TYPE, not by convention: keyed on the union generated from the extension's own OpenAPI
 * document, so a status the server grows fails this build (TS2741, naming the missing key) rather than
 * going uncounted. One map rather than a filter pass per status, because this is the number a user
 * approves a rename on and four independent passes could each miss the same one: `skipExcluded` was
 * absent from all of them, so every excluded item was left out of the total and the whole line
 * vanished when an exclude was the only reason anything was skipped.
 *
 * DECLARATION ORDER IS THE RENDERED CLAUSE ORDER. The five clause-bearing entries lead, in the order
 * their clauses appear in the sentence; the excluded entry was inserted after the collision one so no
 * sentence a user has already been reading changed order.
 */
const SKIP_CLAUSES: Record<RenamerStatus, SkipClause | null> = {
  skipGated: { clause: "need a required field", reason: "needs a required field" },
  skipCollision: { clause: "have a name conflict", reason: "name conflict" },
  // Split out of the collision entry, which counted all three as a name conflict. Appended after it so
  // no sentence a user has already been reading changes order, exactly as the excluded entry was.
  skipNotAllowed: {
    clause: "would land somewhere Renamer is not allowed to write",
    reason: "destination not allowed",
  },
  skipTooLong: { clause: "would make too long a path", reason: "path too long" },
  skipExcluded: { clause: "are excluded by a rule", reason: "excluded by a rule" },
  // Kept exactly as shipped even though the executor produces this one at move time, past this confirm
  // gate, so no preview item can carry it: retiring live user-facing copy is a decision of its own, and
  // is not the one being made here.
  skipLocked: { clause: "are in use", reason: "in use" },
  skipMissingSource: { clause: "are missing on disk", reason: "missing on disk" },
  // The planner produces this one, so it CAN reach this dialog, and it needs copy: the folder template
  // had no Cove library path to sit under because the file is under none.
  skipUnanchored: {
    clause: "sit outside every Cove library path",
    reason: "outside every Cove library path",
  },
  // The other half of that pair: the file is fine, the RULE is broken — its chosen root is no longer
  // one of Cove's library paths. Separate copy because the remedy is: re-pick the root in Renamer,
  // where the one above asks the user to widen Cove's library.
  skipRootMissing: {
    clause: "use a destination root that no longer exists",
    reason: "destination root no longer exists",
  },
  // Not a skip: these two are the items that WILL change, counted by `willRename` above.
  rename: null,
  move: null,
  noOp: null,
  // Executor-only, so a forward-run outcome cannot appear in a list assembled before the run: this gate
  // is what starts it. No copy is invented for any of them — a clause written here would be dead text.
  failed: null,
  skipPermissionDenied: null,
  skipVerifyFailed: null,
  skipCancelled: null,
  skipNoSpace: null,
  skipBlocked: null,
};

/**
 * Render a byte count the way Cove's own UI renders one: scaled across B/KB/MB/GB/TB, one decimal,
 * trailing zeros dropped.
 *
 * This used to force GB always, which read "0.5 GB" for a 500 MB move and "0.0 GB" for anything under
 * ~50 MB — a wrong-looking number on exactly the small batches a user checks most carefully.
 *
 * The host exports its own `formatFileSize` to extensions through `@cove/runtime/components`, and this
 * deliberately does NOT import it. That module has no resolution outside a running host, and this file
 * is pure so its wording can be unit-tested without one; importing it would trade a testable module for
 * a mocked one. The algorithm below is therefore kept identical to the host's on purpose — if Cove's
 * changes, this reads differently from the rest of the UI, which is the cost of that choice.
 */
function formatSize(bytes: number): string {
  if (bytes === 0) return "0 B";
  const k = 1024;
  const units = ["B", "KB", "MB", "GB", "TB"];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${parseFloat((bytes / Math.pow(k, i)).toFixed(1))} ${units[i]}`;
}

/**
 * The per-cross-volume blast-radius lines: one "↪ N items (X GB) move from A to B." line per pair.
 * A same-drive batch has no `volumePairs` and yields an empty array.
 */
function buildBlastLines(summary?: PreviewSummary): string[] {
  return (summary?.volumePairs ?? []).map(
    (p) =>
      `↪ ${p.count} item${p.count === 1 ? "" : "s"} (${formatSize(p.bytes)}) move from ${p.from} to ${p.to}.`,
  );
}

/**
 * The blast-radius call-to-action, scaled by `ConfirmLevel`: Heavy is the strongest cross-drive
 * warning, Standard a plainer cross-drive notice, Light the original reassuring line.
 *
 * The promise of an undo is unconditional: no file count makes a rename unrecordable, so a branch on
 * size here could only ever take one arm.
 */
function confirmCallToAction(level: ConfirmLevel): string {
  const reversibility = `You can undo this afterwards.`;
  return level === "heavy"
    ? `This is a LARGE cross-drive move — files will be COPIED across drives, which can take a while. ` +
        `Click OK only if you are sure; Cancel to stop. ${reversibility}`
    : level === "standard"
      ? `This moves files across drives. Click OK to proceed, or Cancel to stop. ${reversibility}`
      : `Click OK to rename, or Cancel to stop. ${reversibility}`;
}

/**
 * Build the exact text for the in-flow window.confirm gate shown before a bulk rename runs.
 *
 * - N = items that will actually change (status Rename | Move); M = total selected.
 * - One `⚠` line per non-zero warning kind: cross-drive copies whose temporary path would not fit,
 *   skips (one sub-count per reason kind {@link SKIP_CLAUSES} declares), numbered (suffixed),
 *   cleaned (sanitized).
 * - Up to 5 `old → new` basename examples drawn from will-rename items; "… and R more." when N > 5.
 * - When N == 0 the body states nothing will be renamed (the handler then cancels even on OK).
 *
 * Blast radius: when `summary` is supplied and the batch moves files across
 * drives, the confirm wording SCALES with `summary.confirmLevel` — an explicit "N items (X GB) move
 * from A to B" line per cross-volume pair is added, and the call-to-action is heavier for a Heavy
 * batch than a Light one. A same-drive-only batch (Light, no `volumePairs`) reads exactly as before.
 * Pure (no DOM/fetch) so it stays unit-reasonable.
 */
export function buildConfirmSummary(
  items: PreviewItemView[],
  summary?: PreviewSummary,
): {
  text: string;
  willRenameCount: number;
} {
  const willRename = items.filter((it) => it.status === "rename" || it.status === "move");
  const n = willRename.length;
  const m = items.length;

  const tally = new Map<string, number>();
  // Membership, not `!== null`: an undeclared status also satisfies `!== null`, so the looser test
  // counted it here and then lost it again below, where the clause list reads only declared keys. That
  // is the same disappearance `skipExcluded` suffered, re-created for a status the running server grew
  // after this bundle shipped. Counted separately instead, because a number the user approves a rename
  // on must not omit rows it could not classify.
  let unclassified = 0;
  for (const it of items) {
    // Widened deliberately, for the reason `warningBadgeLogic.badgingFor` states in full.
    const clause = (SKIP_CLAUSES as Record<string, SkipClause | null | undefined>)[it.status];
    if (clause === undefined) unclassified++;
    else if (clause !== null) tally.set(it.status, (tally.get(it.status) ?? 0) + 1);
  }
  // Read in the MAP's declaration order, never the tally's — that one follows whatever order the items
  // happened to arrive in, which would let the same selection word its sentence differently twice.
  const skipKinds = Object.entries(SKIP_CLAUSES).flatMap(([status, clause]) => {
    const count = tally.get(status) ?? 0;
    return clause !== null && count > 0 ? [{ ...clause, count }] : [];
  });
  // Includes the unclassified remainder: the headline number is what the user weighs the rename
  // against, so a row that reached no clause still has to be inside it.
  const skipped = skipKinds.reduce((sum, kind) => sum + kind.count, 0) + unclassified;
  const numbered = willRename.filter((it) => it.suffixed).length;
  const cleaned = willRename.filter((it) => it.sanitized).length;

  const warningLines: string[] = [];
  // First, and phrased as a failure rather than an advisory: every other line here describes a rename
  // that will happen differently, while this one describes files the executor will not be able to move at
  // all. It reads the aggregate COUNT, never a list of paths — the selection reaches library size, and
  // this text goes into a native confirm box that cannot scroll usefully.
  //
  // The cause is not stated in characters on purpose. A cross-drive move copies to a temporary name
  // longer than the final one, and the exact number belongs to the server that mints it; repeating it
  // here would be a second copy of a value that has already been narrowed once. What the user can act on
  // is the remedy, so that is what the line carries.
  const inFlightOverflow = summary?.inFlightPathOverflowCount ?? 0;
  if (inFlightOverflow > 0) {
    warningLines.push(
      `⚠ ${inFlightOverflow} cannot be copied across drives — the temporary copy's path would be too ` +
        `long. Shorten the destination folder or the filename template for ${inFlightOverflow === 1 ? "it" : "them"}.`,
    );
  }
  if (skipped > 0) {
    // If only one reason kind, collapse to the compact "(reason)" form.
    const onlyKind = skipKinds.length === 1 ? skipKinds[0] : undefined;
    if (onlyKind && unclassified === 0) {
      warningLines.push(`⚠ ${skipped} skipped (${onlyKind.reason}).`);
    } else {
      const clauses = skipKinds.map((kind) => `${kind.count} ${kind.clause}`);
      if (unclassified > 0) clauses.push(`${unclassified} for an unrecognised reason`);
      warningLines.push(`⚠ ${skipped} skipped — ${clauses.join(", ")}.`);
    }
  }
  if (cleaned > 0) {
    warningLines.push(`⚠ ${cleaned} had illegal characters cleaned up.`);
  }
  if (numbered > 0) {
    warningLines.push(`⚠ ${numbered} got a number added to avoid a name clash (e.g. "name (1)").`);
  }

  // Blast-radius lines (additive): one per cross-volume (from → to) pair, when the backend reports
  // any. A same-drive-only batch has no volumePairs and these lines are absent.
  const blastLines = buildBlastLines(summary);

  const warningBlock = warningLines.length > 0 ? `${warningLines.join("\n")}\n\n` : "";
  const blastBlock = blastLines.length > 0 ? `${blastLines.join("\n")}\n\n` : "";

  if (n === 0) {
    const text =
      `Nothing will be renamed — all ${m} selected item${m === 1 ? "" : "s"} ` +
      `are skipped or already named correctly.\n\n` +
      warningBlock +
      `Click OK to dismiss.`;
    return { text, willRenameCount: 0 };
  }

  const header =
    n === m
      ? `Rename ${n} selected item${n === 1 ? "" : "s"}?`
      : `Rename ${n} of ${m} selected items?`;

  const examples = willRename.slice(0, SAMPLE_LIMIT).map((it) => {
    const oldName = basename(it.oldFullPath);
    const newName = it.newBasename || basename(it.newFullPath);
    return `  ${oldName}  →  ${newName}`;
  });
  const remaining = n - examples.length;
  if (remaining > 0) examples.push(`  … and ${remaining} more.`);

  // The call-to-action scales with the blast radius. A Heavy cross-drive move (many files / many
  // bytes / several volumes) gets the strongest wording; Standard is a plainer cross-drive notice;
  // Light (same-drive only, or no summary) keeps the original reassuring line.
  const level: ConfirmLevel = summary?.confirmLevel ?? "light";
  const callToAction = confirmCallToAction(level);

  const text =
    `${header}\n\n` +
    warningBlock +
    blastBlock +
    `Examples:\n${examples.join("\n")}\n\n` +
    callToAction;

  return { text, willRenameCount: n };
}
