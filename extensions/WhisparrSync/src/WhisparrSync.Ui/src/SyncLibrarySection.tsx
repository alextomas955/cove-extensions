/**
 * SyncLibrarySection — the settings-tab "Sync my library to Whisparr" section (mounted by SettingsPage,
 * a child of the already-registered WhisparrSyncPage — no manifest/index.ts entry of its own, like
 * WhisparrFileSettingsSection). It previews the whole-library counts before the user commits, then the
 * primary Sync action registers what Cove owns as PRESENT in Whisparr (never a grab) through the
 * configure-gated /sync-library endpoint.
 *
 * Add and monitor are DECOUPLED: the "Also monitor" toggle is off by default and, when on, reveals a
 * user-picked scope (New releases only / All releases, default New) — the request carries both. On v2 the
 * section states studio/site-level only, and the server's preview zeroes the performer/scene buckets so the
 * summary reads studios-only on its own.
 *
 * Sync enqueues a background Job; the host's top-right Job Drawer surfaces its progress + summary, so the
 * section only shows an inline "Sync started" line and NEVER a window.alert (per the bulk-action feedback
 * rule). Styling uses host Tailwind token classes only (no hex, no dangerouslySetInnerHTML).
 */
import { useCallback, useEffect, useState } from "react";
import { RefreshCw } from "lucide-react";
import { request, ApiError } from "@cove/extension-sdk";
import { Button, SectionCard, Select, Spinner, StatusText, Toggle } from "@cove-extensions/ui-shared";
import { notLoadedMessage } from "./connectionAvailabilityLogic";
import {
  DEFAULT_SYNC_SCOPE,
  SYNC_SCOPE_OPTIONS,
  previewSummary,
  syncLibraryBody,
  type MonitorScope,
  type SyncPreviewCounts,
} from "./syncLibraryLogic";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const SYNC_PREVIEW_PATH = `/extensions/${EXTENSION_ID}/sync-preview`;
const SYNC_LIBRARY_PATH = `/extensions/${EXTENSION_ID}/sync-library`;

export interface SyncLibrarySectionProps {
  /** A live connection is available (the page's live read succeeded) — required to preview or sync. */
  connected: boolean;
  /** The connected version is v2: studio/site-level only, no performer/scene sync. */
  isV2: boolean;
  /** A saved connection that's currently unreachable — the not-loaded copy says "retry", not "set up". */
  unreachable: boolean;
}

const SCOPE_OPTIONS = SYNC_SCOPE_OPTIONS.map((o) => ({ value: o.value, label: o.label }));

export function SyncLibrarySection({ connected, isV2, unreachable }: SyncLibrarySectionProps) {
  const [counts, setCounts] = useState<SyncPreviewCounts | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [previewError, setPreviewError] = useState(false);
  const [alsoMonitor, setAlsoMonitor] = useState(false);
  const [scope, setScope] = useState<MonitorScope>(DEFAULT_SYNC_SCOPE);
  const [syncing, setSyncing] = useState(false);
  const [started, setStarted] = useState(false);
  const [syncError, setSyncError] = useState<string | null>(null);

  // Preview the whole-library counts once a live connection is available — they gate the user's decision.
  // Re-runs on a version switch (isV2 changes) because v2 counts studios only. The IIFE awaits the request
  // first so no state is set synchronously during the effect (the no-cascade rule).
  useEffect(() => {
    if (!connected) return;
    void (async () => {
      try {
        const c = await request<SyncPreviewCounts>(SYNC_PREVIEW_PATH);
        setCounts(c);
        setPreviewError(false);
      } catch {
        setCounts(null);
        setPreviewError(true);
      }
    })();
  }, [connected, isV2]);

  // The user-invoked re-read (event handler, so a synchronous state set is fine here). Its own `refreshing`
  // spinner is distinct from the derived first-load "counting" state below.
  const loadPreview = useCallback(async () => {
    setRefreshing(true);
    setPreviewError(false);
    try {
      setCounts(await request<SyncPreviewCounts>(SYNC_PREVIEW_PATH));
    } catch {
      setCounts(null);
      setPreviewError(true);
    } finally {
      setRefreshing(false);
    }
  }, []);

  // First-load state: connected, but the preview hasn't arrived and hasn't errored yet.
  const counting = connected && counts === null && !previewError && !refreshing;

  async function sync() {
    setSyncing(true);
    setSyncError(null);
    setStarted(false);
    try {
      await request(SYNC_LIBRARY_PATH, {
        method: "POST",
        body: JSON.stringify(syncLibraryBody({ alsoMonitor, scope })),
      });
      setStarted(true);
    } catch (err) {
      // A real ApiError is a genuine failure; a non-ApiError after a 2xx enqueue (e.g. an empty-body
      // parse) still means the job queued, so treat it as started.
      if (err instanceof ApiError) {
        setSyncError(err.message);
      } else {
        setStarted(true);
      }
    } finally {
      setSyncing(false);
    }
  }

  const scopeDescription = SYNC_SCOPE_OPTIONS.find((o) => o.value === scope)?.description;

  return (
    <SectionCard
      title="Sync my library to Whisparr"
      description="Register the studios, performers, and scenes Cove already owns as present in Whisparr — without grabbing anything."
      headerRight={
        connected ? (
          <Button
            variant="ghost"
            onClick={() => {
              void loadPreview();
            }}
            disabled={refreshing || counting}
          >
            {refreshing ? <Spinner /> : <RefreshCw className="h-4 w-4" />}
            Refresh
          </Button>
        ) : undefined
      }
    >
      {!connected ? (
        <StatusText kind="muted">
          {notLoadedMessage(unreachable, "your library preview")}
        </StatusText>
      ) : (
        <>
          {counting || refreshing ? (
            <div className="flex items-center gap-2 text-sm text-secondary">
              <Spinner />
              Counting your library…
            </div>
          ) : previewError ? (
            <StatusText kind="error">
              Couldn&apos;t read your library preview — Refresh to retry.
            </StatusText>
          ) : counts ? (
            <p className="text-sm text-foreground">{previewSummary(counts)}</p>
          ) : null}

          {isV2 ? (
            <StatusText kind="muted">
              On Whisparr v2 this registers your studios (sites) only — performer and per-scene sync
              are on Whisparr v3 (Eros).
            </StatusText>
          ) : null}

          <div className="space-y-3 rounded-lg border border-border p-3">
            <Toggle
              label="Also monitor what I sync"
              checked={alsoMonitor}
              onChange={setAlsoMonitor}
              helper="Off by default. Syncing only registers what you own; turn this on to also monitor those studios and performers for future releases."
            />
            {alsoMonitor ? (
              <div className="space-y-1">
                <Select<MonitorScope> value={scope} onChange={setScope} options={SCOPE_OPTIONS} />
                {scopeDescription ? (
                  <p className="text-xs text-secondary">{scopeDescription}</p>
                ) : null}
              </div>
            ) : null}
          </div>

          <div className="flex items-center gap-3">
            <Button
              onClick={() => {
                void sync();
              }}
              disabled={syncing || counting || refreshing}
            >
              {syncing ? <Spinner /> : null}
              Sync my library to Whisparr
            </Button>
            {started ? (
              <StatusText kind="success">Sync started — track it in the Job Drawer.</StatusText>
            ) : syncError ? (
              <StatusText kind="error">Couldn&apos;t start the sync — {syncError}.</StatusText>
            ) : null}
          </div>
        </>
      )}
    </SectionCard>
  );
}
