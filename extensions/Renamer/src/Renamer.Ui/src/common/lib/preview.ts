/**
 * The pure window.confirm summary builder for a real-selection `/preview`.
 *
 * CRITICAL: `/preview` returns `RenamePlanItem[]` (camelCase over the wire), NOT the
 * `/preview-sample` `flags[]` array. The warning taxonomy is derived from the `status` STRING enum
 * (the host serializes the enum as a string) PLUS the additive `suffixed` / `sanitized` bools the
 * planner sets — there is no `flags[]` field here.
 *
 * `buildConfirmSummary` is intentionally pure (no DOM, no fetch) so the confirm-dialog wording logic
 * can be unit-reasoned in isolation; the handler (renameSelected.ts) wraps it with window.confirm + fetch.
 */

import type { ConfirmLevel, PreviewItemView, PreviewSummary, RenamerStatus } from "../../wire/api";

/** Last path segment, tolerant of both `/` and `\` separators (Windows paths). */
function basename(p: string): string {
  if (!p) return p;
  const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
  return i >= 0 ? p.slice(i + 1) : p;
}

const SAMPLE_LIMIT = 5;

/**
 * The two spellings one skip kind needs: the plural `clause` the multi-reason line joins behind a
 * count, and the singular `reason` the compact single-kind form parenthesises.
 */
interface SkipClause {
  readonly clause: string;
  readonly reason: string;
}

/**
 * Every status the wire can carry, and whether it counts as a skip in the confirm dialog.
 *
 * Total by TYPE, keyed on the union generated from the extension's own OpenAPI document, so a status
 * the server grows fails this build (TS2741, naming the missing key) rather than going uncounted. One
 * map rather than a filter pass per status: this is the number a user approves a destructive operation
 * against, and independent passes each omitted the same five planner-produced statuses, so a selection
 * skipped entirely by an exclude rule reached the dialog with no reason given at all.
 *
 * DECLARATION ORDER IS THE RENDERED CLAUSE ORDER.
 *
 * The labels track the row pills in `warningBadgeLogic.ts`, because the two surfaces describe the same
 * outcome to the same user.
 */
const SKIP_CLAUSES: Record<RenamerStatus, SkipClause | null> = {
  skipGated: { clause: "need a required field", reason: "needs a required field" },
  skipCollision: { clause: "have a name conflict", reason: "name conflict" },
  skipExcluded: { clause: "are excluded by a rule", reason: "excluded by a rule" },
  skipMissingSource: { clause: "are missing on disk", reason: "missing on disk" },
  skipUnanchored: { clause: "sit outside your Cove library", reason: "outside your Cove library" },
  skipRootMissing: {
    clause: "use a rule whose destination is no longer a library path",
    reason: "destination is no longer a library path",
  },
  skipNotAllowed: {
    clause: "would land outside your allowed roots",
    reason: "destination outside your allowed roots",
  },
  skipTooLong: { clause: "would make too long a path", reason: "path too long" },
  // The executor produces this one at move time, past this gate, so no preview item carries it. The
  // copy stays because retiring live user-facing text is a decision of its own.
  skipLocked: { clause: "are in use", reason: "in use" },
  // Not a skip: the two statuses counted by `willRename`, and the item that needs no change.
  renamer: null,
  move: null,
  noOp: null,
  // Executor-only, and produced only AFTER this confirm: by the time a move fails, the OS refuses it,
  // the read-back mismatches or a shutdown interrupts the copy, the user has already approved.
  failed: null,
  skipPermissionDenied: null,
  skipVerifyFailed: null,
  skipCancelled: null,
  skipBlocked: null,
  // Log-only: a disk-full skip is reported through the run log and never becomes an item result.
  skipNoSpace: null,
};

/** Render a byte count as a compact GB string for the blast-radius lines (e.g. "1.5 GB"). */
function formatGb(bytes: number): string {
  const gb = bytes / (1024 * 1024 * 1024);
  // Show one decimal for sub-10 GB so a 1.5 GB move doesn't read as "2 GB"; whole numbers above.
  return gb >= 10 ? `${Math.round(gb)} GB` : `${gb.toFixed(1)} GB`;
}

/**
 * The per-cross-volume blast-radius lines: one "↪ N items (X GB) move from A to B." line per pair.
 * Single source shared by the bulk-action window.confirm and the settings-panel Review dialog, so
 * both rename entry points describe a cross-drive batch identically. A same-drive batch has no
 * `volumePairs` and yields an empty array.
 */
function buildBlastLines(summary?: PreviewSummary): string[] {
  return (summary?.volumePairs ?? []).map(
    (p) =>
      `↪ ${p.count} item${p.count === 1 ? "" : "s"} (${formatGb(p.bytes)}) move from ${p.from} to ${p.to}.`,
  );
}

/**
 * The reversibility sentence that closes the call-to-action. `undoable` comes from the server, which
 * knows its own row cap; a batch over that cap is not recorded at all, so promising an undo here
 * would be a promise nothing can keep.
 */
function undoNotice(undoable: boolean): string {
  return undoable
    ? `You can undo this afterwards.`
    : `This batch is too large to record an undo — it cannot be reversed. ` +
        `Cancel and use the dry run if you have not checked the new names yet.`;
}

/**
 * The blast-radius call-to-action, scaled by `ConfirmLevel`: Heavy is the strongest cross-drive
 * warning, Standard a plainer cross-drive notice, Light the original reassuring line.
 * Single source shared by both rename confirm surfaces.
 */
function confirmCallToAction(level: ConfirmLevel, undoable: boolean): string {
  const reversibility = undoNotice(undoable);
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
 * - One `⚠` line per non-zero warning kind: skips (split into gated / collision sub-counts),
 *   numbered (suffixed), cleaned (sanitized).
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
  const willRename = items.filter((it) => it.status === "renamer" || it.status === "move");
  const n = willRename.length;
  const m = items.length;

  const tally = new Map<string, number>();
  // Membership, not `!== null`: an undeclared status also satisfies `!== null`, so the looser test
  // would count it here and lose it again below, where the clause list reads only declared keys.
  // Counted separately instead, because a number the user approves a rename on must not omit rows it
  // could not classify.
  let unclassified = 0;
  for (const it of items) {
    const clause = (SKIP_CLAUSES as Record<string, SkipClause | null | undefined>)[it.status];
    if (clause === undefined) unclassified += 1;
    else if (clause !== null) tally.set(it.status, (tally.get(it.status) ?? 0) + 1);
  }
  // Read in the MAP's declaration order, never the tally's — that one follows whatever order the items
  // happened to arrive in, which would let the same selection word its sentence differently twice.
  const skipKinds = Object.entries(SKIP_CLAUSES).flatMap(([status, clause]) => {
    const count = tally.get(status) ?? 0;
    return clause !== null && count > 0 ? [{ ...clause, count }] : [];
  });
  const skipped = skipKinds.reduce((sum, kind) => sum + kind.count, 0) + unclassified;
  const numbered = willRename.filter((it) => it.suffixed).length;
  const cleaned = willRename.filter((it) => it.sanitized).length;

  const warningLines: string[] = [];
  // First, and phrased as a failure rather than an advisory: every other line here describes a rename
  // that will happen differently, while this one describes files the executor will not be able to move
  // at all. It reads the aggregate COUNT, never a list of paths — a selection reaches library size, and
  // this text goes into a native confirm box that cannot scroll usefully. The cause is not stated in
  // characters: what the user can act on is the remedy, so that is what the line carries.
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
  // Light (same-drive only, or no summary) keeps the original reassuring line. Absent a summary the
  // batch is assumed undoable — that is the pre-summary behaviour, and a selection small enough to
  // reach this path without one is far under the cap.
  const level: ConfirmLevel = summary?.confirmLevel ?? "light";
  const callToAction = confirmCallToAction(level, summary?.undoable ?? true);

  const text =
    `${header}\n\n` +
    warningBlock +
    blastBlock +
    `Examples:\n${examples.join("\n")}\n\n` +
    callToAction;

  return { text, willRenameCount: n };
}
