/**
 * The `/preview` (real-selection) contract + the pure window.confirm summary builder.
 *
 * CRITICAL: `/preview` returns `RenamePlanItem[]` (camelCase over the wire), NOT the
 * `/preview-sample` `flags[]` array. The warning taxonomy is derived from the `status` STRING enum
 * (the host serializes the enum as a string) PLUS the additive `suffixed` / `sanitized` bools the
 * planner sets — there is no `flags[]` field here.
 *
 * `buildConfirmSummary` is intentionally pure (no DOM, no fetch) so the confirm-dialog wording logic
 * can be unit-reasoned in isolation; the handler (renameSelected.ts) wraps it with window.confirm + fetch.
 */

/** The `RenamerStatus` enum (Planner/RenamerPlan.cs), serialized as a string. */
export type PreviewStatus =
  | "Renamer"
  | "Move"
  | "NoOp"
  | "SkipCollision"
  | "SkipGated"
  | "SkipLocked"
  | "SkipMissingSource"
  | "SkipBlocked"
  | "Failed";

/** Mirrors `RenamePlanItem` (camelCase over the wire). */
export interface PreviewItem {
  fileId: number;
  oldFullPath: string;
  newFullPath: string;
  status: PreviewStatus;
  newBasename: string;
  targetFolderPath: string;
  reason?: string | null;
  /** True when the final Rename/Move item got a duplicate-suffix added to dodge a name clash. */
  suffixed: boolean;
  /** True when the sanitize step changed the name (illegal chars removed/replaced). */
  sanitized: boolean;
  /** The routed destination-root template; null for a source-confine/in-place item. */
  resolvedDestinationRoot?: string | null;
  /** The resolver's matched-rule label (e.g. "Studio:42(direct)", "InPlace"); "" on skip/no-op. */
  matchedRule: string;
  /** The destination volume the routed item lands on; "" when not a routed move. */
  targetVolume: string;
}

/**
 * A `/scan-library` row: a `PreviewItem` plus the entity kind it belongs to. The whole-library scan
 * spans Video/Image/Audio in one response (unlike `/preview`, which is single-kind per request), so
 * the "Type" column needs an explicit per-item tag `PreviewItem` itself doesn't carry.
 */
export interface ScanItem extends PreviewItem {
  kind: "Video" | "Image" | "Audio";
  /** The Cove entity id the asset detail link is built from; distinct from the per-file `fileId`. */
  entityId: number;
}

/** How loud the pre-rename confirm must be — mirrors the C# `ConfirmLevel` (serialized string). */
export type ConfirmLevel = "Light" | "Standard" | "Heavy";

/** One "N items (X) from A to B" line of the blast radius — mirrors the C# `VolumePairDelta`. */
export interface VolumePairDelta {
  from: string;
  to: string;
  count: number;
  bytes: number;
}

/** The whole-batch blast-radius summary — mirrors the C# `PreviewSummary`. */
export interface PreviewSummary {
  totalCount: number;
  sameVolumeCount: number;
  crossVolumeCount: number;
  crossVolumeBytes: number;
  volumePairs: VolumePairDelta[];
  confirmLevel: ConfirmLevel;
}

/** The `/preview` response: the per-item plan plus the whole-batch summary. */
export interface PreviewResponse {
  items: PreviewItem[];
  summary: PreviewSummary;
}

/** Last path segment, tolerant of both `/` and `\` separators (Windows paths). */
function basename(p: string): string {
  if (!p) return p;
  const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
  return i >= 0 ? p.slice(i + 1) : p;
}

const SAMPLE_LIMIT = 5;

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
export function buildBlastLines(summary?: PreviewSummary): string[] {
  return (summary?.volumePairs ?? []).map(
    (p) =>
      `↪ ${p.count} item${p.count === 1 ? "" : "s"} (${formatGb(p.bytes)}) move from ${p.from} to ${p.to}.`,
  );
}

/**
 * The blast-radius call-to-action, scaled by `ConfirmLevel`: Heavy is the strongest cross-drive
 * warning, Standard a plainer cross-drive notice, Light the original reassuring line.
 * Single source shared by both rename confirm surfaces.
 */
export function confirmCallToAction(level: ConfirmLevel): string {
  return level === "Heavy"
    ? `This is a LARGE cross-drive move — files will be COPIED across drives, which can take a while. ` +
        `Click OK only if you are sure; Cancel to stop. You can undo this afterwards.`
    : level === "Standard"
      ? `This moves files across drives. Click OK to proceed, or Cancel to stop. You can undo this afterwards.`
      : `Click OK to rename, or Cancel to stop. You can undo this afterwards.`;
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
  items: PreviewItem[],
  summary?: PreviewSummary,
): {
  text: string;
  willRenameCount: number;
} {
  const willRename = items.filter((it) => it.status === "Renamer" || it.status === "Move");
  const n = willRename.length;
  const m = items.length;

  const gated = items.filter((it) => it.status === "SkipGated").length;
  const collision = items.filter((it) => it.status === "SkipCollision").length;
  const lockedSkipped = items.filter((it) => it.status === "SkipLocked").length;
  const missingSkipped = items.filter((it) => it.status === "SkipMissingSource").length;
  const skipped = gated + collision + lockedSkipped + missingSkipped;
  const numbered = willRename.filter((it) => it.suffixed).length;
  const cleaned = willRename.filter((it) => it.sanitized).length;

  const warningLines: string[] = [];
  if (skipped > 0) {
    const clauses: string[] = [];
    if (gated > 0) clauses.push(`${gated} need a required field`);
    if (collision > 0) clauses.push(`${collision} have a name conflict`);
    if (lockedSkipped > 0) clauses.push(`${lockedSkipped} are in use`);
    if (missingSkipped > 0) clauses.push(`${missingSkipped} are missing on disk`);
    // If only one reason kind, collapse to the compact "(reason)" form.
    if (clauses.length === 1) {
      const onlyReason =
        gated > 0
          ? "needs a required field"
          : collision > 0
            ? "name conflict"
            : lockedSkipped > 0
              ? "in use"
              : "missing on disk";
      warningLines.push(`⚠ ${skipped} skipped (${onlyReason}).`);
    } else {
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
  const level: ConfirmLevel = summary?.confirmLevel ?? "Light";
  const callToAction = confirmCallToAction(level);

  const text =
    `${header}\n\n` +
    warningBlock +
    blastBlock +
    `Examples:\n${examples.join("\n")}\n\n` +
    callToAction;

  return { text, willRenameCount: n };
}
