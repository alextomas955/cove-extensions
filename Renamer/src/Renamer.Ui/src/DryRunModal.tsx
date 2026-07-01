/**
 * The full-screen "Dry run" modal: scans the whole library via the job-backed scan-library
 * endpoint, polls the host's generic job-status endpoint to completion, then renders the
 * paginated old→new preview table. The footer "Rename N files" button calls the SAME
 * rename-trigger callback the panel-level "Rename all files" button calls (D-09) — this modal
 * never talks to the rename-library endpoint through a separate code path.
 *
 * Prop contract: the modal is self-contained — it POSTs scan-library itself on mount and manages
 * its own job-polling lifecycle. The parent only supplies `onClose` and `onRenameAll` (the shared
 * rename handler) plus whether a rename triggered from elsewhere is in flight, so the footer
 * button's disabled/spinner state matches the panel-level button exactly.
 *
 * SECURITY: every filename/path is a React text node (auto-escaped); no dangerouslySetInnerHTML.
 */
import { useEffect, useRef, useState } from "react";
import { request, ApiError } from "@cove/extension-sdk";

import { Dialog, ErrorBox } from "./dialog";
import { GhostButton, PrimaryButton, Spinner } from "./primitives";
import { WarningBadges } from "./WarningBadge";
import type { ScanItem } from "./preview";
import { countByStatus, paginate, totalPages } from "./dryRunLogic";

const EXTENSION_ID = "com.alextomas955.renamer";
const SCAN_LIBRARY_PATH = `/extensions/${EXTENSION_ID}/scan-library`;
const LAST_SCAN_PATH = `/extensions/${EXTENSION_ID}/last-scan`;

const TITLE_ID = "rename-dry-run-title";
const DESC_ID = "rename-dry-run-summary";
const PAGE_SIZE = 50;
const POLL_INTERVAL_MS = 1000;

/**
 * Mirrors `Cove.Core.Interfaces.JobInfo` — only the fields this modal reads. The host's minimal-API
 * JSON options apply `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`, which lowercases the
 * leading character of the C# `JobStatus` enum's PascalCase member names (`Completed` → `"completed"`),
 * not just the field names — so the string values here must be camelCase too, not just `status` itself.
 */
interface JobInfo {
  id: string;
  status: "pending" | "running" | "completed" | "failed" | "cancelled";
  progress: number;
  error?: string | null;
}

function errText(err: unknown): string {
  return err instanceof ApiError ? `${err.status} ${err.body}` : String(err);
}

function basename(p: string): string {
  if (!p) return p;
  const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
  return i >= 0 ? p.slice(i + 1) : p;
}

/**
 * Polls `GET /jobs/{jobId}` every second until the job leaves Pending/Running, then calls
 * `onDone` once. No polling hook exists anywhere in `@cove/extension-sdk` — this is new code
 * (first job-polling UI in this codebase). Clears its interval on unmount or job change so no
 * timer leaks and no state updates fire after unmount.
 */
function usePollJob(jobId: string | null, onDone: (job: JobInfo) => void) {
  useEffect(() => {
    if (!jobId) return;
    let cancelled = false;
    const interval = setInterval(() => {
      request<JobInfo>(`/jobs/${jobId}`)
        .then((job) => {
          if (cancelled) return;
          if (job.status === "completed" || job.status === "failed" || job.status === "cancelled") {
            clearInterval(interval);
            onDone(job);
          }
        })
        .catch(() => {
          // Transient poll failure — keep polling; a real failure surfaces via job.status.
        });
    }, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(interval);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- onDone is a stable ref from the caller
  }, [jobId]);
}

export function DryRunModal({
  onClose,
  onRenameAll,
  renaming,
}: {
  onClose: () => void;
  /** The SHARED rename-trigger handler (D-09) — also called by the panel-level button. */
  onRenameAll: (items: ScanItem[]) => void;
  /** True while a rename triggered from either entry point is in flight. */
  renaming: boolean;
}) {
  const [scanJobId, setScanJobId] = useState<string | null>(null);
  const [items, setItems] = useState<ScanItem[] | null>(null);
  const [scanError, setScanError] = useState<string | null>(null);
  const [page, setPage] = useState(0);
  // Guards against StrictMode's dev-only mount->unmount->remount cycle enqueueing the scan job
  // twice. A plain boolean ref (rather than a per-effect `cancelled` local) survives the
  // synthetic unmount, so it suppresses the SECOND mount's POST without also discarding the
  // FIRST mount's in-flight response — a `cancelled`-in-cleanup guard would do both, since
  // StrictMode's synthetic unmount fires the cleanup before the network round-trip resolves.
  const scanRequested = useRef(false);

  // Kick off the scan on mount so the modal opens immediately in a loading state.
  useEffect(() => {
    if (scanRequested.current) return;
    scanRequested.current = true;
    request<{ jobId: string }>(SCAN_LIBRARY_PATH, { method: "POST" })
      .then((res) => {
        setScanJobId(res.jobId);
      })
      .catch((err: unknown) => {
        setScanError(errText(err));
      });
  }, []);

  usePollJob(scanJobId, (job) => {
    if (job.status !== "completed") {
      setScanError(job.error ?? "the scan job did not complete");
      return;
    }
    request<ScanItem[]>(LAST_SCAN_PATH)
      .then((res) => {
        setItems(res);
      })
      .catch((err: unknown) => {
        setScanError(errText(err));
      });
  });

  const counts = items ? countByStatus(items) : null;
  const pageCount = items ? totalPages(items.length, PAGE_SIZE) : 1;
  const pageItems = items ? paginate(items, page, PAGE_SIZE) : [];

  return (
    <Dialog
      titleId={TITLE_ID}
      describedById={DESC_ID}
      pending={renaming}
      onCancel={onClose}
      size="xl"
    >
      <h2 id={TITLE_ID} className="mb-2 text-lg font-semibold text-foreground">
        Dry run
      </h2>

      {scanError ? (
        <div className="mb-4">
          <ErrorBox>Couldn&apos;t scan your library — {scanError}. Close and try again.</ErrorBox>
        </div>
      ) : items === null || counts === null ? (
        <div className="flex items-center gap-2 py-8 text-sm text-secondary">
          <Spinner />
          Scanning your library…
        </div>
      ) : (
        <>
          <p id={DESC_ID} className="mb-4 text-sm text-secondary">
            <span className="text-foreground">{counts.renamed}</span> will be renamed ·{" "}
            {counts.skipped} skipped · {counts.scanned} scanned
          </p>

          {counts.scanned === 0 ? (
            <p className="py-8 text-center text-sm text-secondary">
              No items match your current settings — nothing to rename.
            </p>
          ) : (
            <>
              <div className="max-h-96 overflow-y-auto rounded border border-border text-sm">
                <table className="w-full border-collapse">
                  <thead>
                    <tr className="sticky top-0 bg-card text-left">
                      <th className="w-20 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted">
                        Type
                      </th>
                      <th className="min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted">
                        Current name
                      </th>
                      <th className="min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted">
                        New name
                      </th>
                      <th className="min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted">
                        Destination
                      </th>
                      <th className="px-3 py-2" />
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-border">
                    {pageItems.map((it) => {
                      const isSkip = it.status !== "Renamer" && it.status !== "Move";
                      const oldName = basename(it.oldFullPath);
                      const newName = it.newBasename || basename(it.newFullPath);
                      return (
                        <tr key={it.fileId} className={isSkip ? "opacity-70" : undefined}>
                          <td className="w-20 px-3 py-2 text-sm text-secondary">{it.kind}</td>
                          <td
                            className="min-w-0 max-w-0 truncate px-3 py-2 font-mono text-sm text-muted"
                            title={it.oldFullPath}
                          >
                            {oldName}
                          </td>
                          <td
                            className={`min-w-0 max-w-0 truncate px-3 py-2 font-mono text-sm ${isSkip ? "text-muted" : "text-foreground"}`}
                            title={it.newFullPath}
                          >
                            {isSkip ? "— will be skipped" : newName}
                          </td>
                          <td
                            className="min-w-0 max-w-0 truncate px-3 py-2 font-mono text-xs text-muted"
                            title={it.targetFolderPath}
                          >
                            {it.targetFolderPath}
                          </td>
                          <td className="px-3 py-2">
                            <WarningBadges item={it} />
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
                <div className="flex items-center justify-between border-t border-border bg-card px-3 py-2">
                  <GhostButton
                    onClick={() => {
                      setPage((p) => p - 1);
                    }}
                    disabled={page === 0}
                  >
                    Prev
                  </GhostButton>
                  <span className="text-xs text-muted">
                    Page {page + 1} of {pageCount}
                  </span>
                  <GhostButton
                    onClick={() => {
                      setPage((p) => p + 1);
                    }}
                    disabled={page === pageCount - 1}
                  >
                    Next
                  </GhostButton>
                </div>
              </div>
            </>
          )}
        </>
      )}

      <div className="mt-6 flex justify-end gap-3">
        <GhostButton onClick={onClose} disabled={renaming}>
          Close
        </GhostButton>
        <PrimaryButton
          onClick={() => {
            if (items) onRenameAll(items);
          }}
          disabled={renaming || !counts || counts.renamed === 0}
        >
          {renaming ? <Spinner /> : null}
          Rename {counts?.renamed ?? 0} files
        </PrimaryButton>
      </div>
    </Dialog>
  );
}
