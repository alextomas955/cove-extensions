/**
 * The full-screen "Dry run" modal: scans the whole library, then renders what the scan found with the
 * search and the status filter answered by the server. The footer "Rename N files" button calls the
 * same rename-trigger callback the panel-level "Rename all files" button calls - this modal never
 * talks to the rename-library endpoint through a separate code path.
 *
 * It composes three pieces and holds only what they share: {@link useLibraryScan} runs the scan,
 * {@link ScanProgress} shows it running, and {@link DryRunRows} owns the table and its own row walk.
 * What stays here is the dialog shell, the filter controls, and the two reasons a rename is refused.
 *
 * Prop contract: the modal is self-contained - it enqueues its own scan on mount and manages its own
 * job-polling lifecycle. The parent only supplies `onClose` and `onRenameAll` (the shared rename
 * handler) plus whether a rename triggered from elsewhere is in flight, so the footer button's
 * disabled/spinner state matches the panel-level button exactly.
 */
import { useEffect, useState } from "react";
import { Search } from "lucide-react";

import { Dialog, ErrorBox } from "./Dialog";
import { Button, ProgressBar, Spinner } from "@cove-extensions/ui-shared";
import type { RenamerOptions } from "../options";
import type { RenameProgress } from "../useRenameLibrary";
import { DryRunRows } from "./DryRunRows";
import { ScanProgress } from "./ScanProgress";
import { useLibraryScan, type ScanDisplay } from "./useLibraryScan";
import {
  bucketTotal,
  formatEta,
  progressPercent,
  summaryCounts,
  type DryRunFilter,
} from "./dryRunLogic";

const TITLE_ID = "rename-dry-run-title";
const DESC_ID = "rename-dry-run-summary";

// Keystrokes are held back this long before the search reaches the server, because each change starts
// a fresh walk from the beginning of the library.
const SEARCH_DEBOUNCE_MS = 350;

const SEGMENTS = [
  { key: "all", label: "All" },
  { key: "will-change", label: "Will change" },
  { key: "attention", label: "Needs attention" },
  { key: "no-change", label: "No change" },
] as const;

export function DryRunModal({
  options,
  dirty,
  onClose,
  onRenameAll,
  renaming,
  renameProgress,
}: Readonly<{
  /** The panel's CURRENT (possibly unsaved) options - sent so the scan previews unsaved edits. */
  options: RenamerOptions;
  /**
   * Whether the panel holds edits that are not saved. The rename runs the saved options, so a dry
   * run of unsaved ones previews a different operation than the button below would perform.
   */
  dirty: boolean;
  onClose: () => void;
  /** The SHARED rename-trigger handler - also called by the panel-level button. */
  onRenameAll: () => void;
  /** True while a rename triggered from either entry point is in flight. */
  renaming: boolean;
  /**
   * Live rename-job progress from the panel's single existing poll. Absent (panel-direct path, or
   * before the first sample) falls back to the button spinner. The modal creates no poller of its
   * own for the rename job.
   */
  renameProgress?: RenameProgress | null;
}>) {
  const [filter, setFilter] = useState<DryRunFilter>("all");
  const [search, setSearch] = useState("");
  // The debounced copy of `search` that actually reaches the server (see SEARCH_DEBOUNCE_MS).
  const [query, setQuery] = useState("");
  // The exact blob the scan was enqueued with, captured once at open. The row pages are planned with
  // this same value, so the rows and the summary always describe one dry run; re-reading `options` per
  // page would let a later panel edit desynchronise the two.
  const [scanOptionsBlob] = useState(() => JSON.stringify(options));
  // The rows describe the settings the scan was enqueued with. The panel stays live behind this
  // modal, so those settings can stop being the ones a rename would use while the rows still show
  // them: Discard is the sharpest case, because it clears `dirty` without touching the rows.
  const scanIsStale = scanOptionsBlob !== JSON.stringify(options);

  const scan = useLibraryScan(scanOptionsBlob);

  // Hold the keystrokes back from the server (see SEARCH_DEBOUNCE_MS).
  useEffect(() => {
    const timer = setTimeout(() => {
      setQuery(search);
    }, SEARCH_DEBOUNCE_MS);
    return () => {
      clearTimeout(timer);
    };
  }, [search]);

  // Counts come from the aggregate, so the segment labels do not move when the filter changes - they
  // describe the whole scan, not the rows that happen to be loaded.
  const counts = scan.summary ? summaryCounts(scan.summary) : null;

  return (
    <Dialog titleId={TITLE_ID} describedById={DESC_ID} pending={renaming} onCancel={onClose}>
      <h2 id={TITLE_ID} className="mb-2 text-lg font-semibold text-foreground">
        Dry run
      </h2>

      {scan.error !== null && (
        <div className="mb-4">
          <ErrorBox>Couldn&apos;t scan your library: {scan.error}. Close and try again.</ErrorBox>
        </div>
      )}

      {scan.error === null && counts === null && <Scanning display={scan.progress} />}

      {scan.error === null && counts !== null && (
        <>
          {counts.scanned === 0 ? (
            <p id={DESC_ID} className="py-8 text-center text-sm text-secondary">
              No items match your current settings. Nothing to rename.
            </p>
          ) : (
            <>
              {/* Segmented filter: isolate "what's actually happening" from the noise. Counts are
                  from the scan's own aggregate, so they stay put when the filter changes, and they
                  are the only place the modal states them. `All` always renders — the row exists
                  only once something was scanned. */}
              <div id={DESC_ID} className="mb-4 flex flex-wrap gap-2">
                {SEGMENTS.map((seg) => {
                  const n = bucketTotal(counts, seg.key);
                  const active = filter === seg.key;
                  if (n === 0 && seg.key !== "all") return null;
                  return (
                    <button
                      key={seg.key}
                      type="button"
                      onClick={() => {
                        setFilter(seg.key);
                      }}
                      aria-pressed={active}
                      className={`rounded-lg border px-3 py-1 text-xs font-medium ${
                        active
                          ? "border-accent bg-accent/15 text-foreground"
                          : "border-border bg-card text-secondary hover:text-foreground"
                      }`}
                    >
                      {seg.label} ({n})
                    </button>
                  );
                })}
              </div>

              {/* Path search, answered by the server as each page is fetched. */}
              <div className="mb-3 flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-1.5">
                <Search className="h-4 w-4 shrink-0 text-muted" aria-hidden />
                <input
                  type="text"
                  value={search}
                  onChange={(e) => {
                    setSearch(e.target.value);
                  }}
                  placeholder={filterPlaceholder(bucketTotal(counts, filter))}
                  aria-label="Filter the dry-run rows"
                  className="w-full bg-transparent text-sm text-foreground outline-none placeholder:text-muted"
                />
                {search ? (
                  <button
                    type="button"
                    onClick={() => {
                      setSearch("");
                    }}
                    className="shrink-0 text-xs text-muted hover:text-foreground"
                  >
                    Clear
                  </button>
                ) : null}
              </div>

              <DryRunRows
                optionsBlob={scanOptionsBlob}
                enabled={scan.summary !== null}
                query={query}
                filter={filter}
                bucketTotal={bucketTotal(counts, filter)}
              />
            </>
          )}
        </>
      )}

      {renaming && renameProgress ? (
        <div className="mt-6 flex flex-col gap-2 text-sm text-secondary">
          <ProgressBar percent={progressPercent(renameProgress.progress)} label="Rename progress" />
          <div className="flex items-center justify-between gap-3">
            <span>
              Renaming… {renameProgress.subTask ?? `${progressPercent(renameProgress.progress)}%`}
            </span>
            <RenameEta etaSeconds={renameProgress.etaSeconds} />
          </div>
        </div>
      ) : null}

      {/* The rename reads the saved settings, while this scan previewed whatever was on screen when
          it opened. Starting a rename from rows that describe different settings runs an operation
          nobody previewed — a kind excluded in these rows is renamed anyway. The panel-level button
          refuses on the same terms. */}
      {dirty || scanIsStale ? (
        <p className="mt-6 text-sm text-amber-400">
          {dirty
            ? "Previewing unsaved settings. Renaming uses the saved ones."
            : "Your settings changed after these rows were scanned. Run the dry run again."}
        </p>
      ) : null}

      <div className="mt-6 flex justify-end gap-3">
        <Button variant="ghost" onClick={onClose} disabled={renaming}>
          Close
        </Button>
        <Button
          onClick={onRenameAll}
          disabled={dirty || scanIsStale || renaming || !counts || counts.willChange === 0}
        >
          {renaming ? <Spinner /> : null}
          Rename {counts?.willChange ?? 0} files
        </Button>
      </div>
    </Dialog>
  );
}

/** The search field's prompt, naming how many rows the selected segment holds. */
function filterPlaceholder(rows: number): string {
  return `Filter ${rows} row${rows === 1 ? "" : "s"}`;
}

function Scanning({ display }: Readonly<{ display: ScanDisplay | null }>) {
  if (display) return <ScanProgress display={display} />;
  return (
    <div className="flex items-center gap-2 py-8 text-sm text-secondary">
      <Spinner />
      Scanning your library…
    </div>
  );
}

function RenameEta({ etaSeconds }: Readonly<{ etaSeconds?: number | null }>) {
  const eta = formatEta(etaSeconds);
  return eta ? <span className="text-muted">{eta}</span> : null;
}
