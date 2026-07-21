/**
 * The studios/performers toolbar-row summary: one GET /entity-library-summary per kind, fetched once when the
 * toolbar pill turns on (shared with the card badges' on/off state, not their per-card cache). One entry per
 * kind — the row asks for its own kind's library-wide monitored-of-total count. A version mismatch (v2) is the
 * `unsupported` state; any other failure, or the server's `available:false`, is `unavailable` so the row can
 * distinguish "Whisparr unreachable" from a genuine "0 monitored".
 */
import { useEffect, useState } from "react";
import { ApiError, request } from "@cove/extension-sdk";
import { isVersionUnsupportedBody } from "./errorCodeLogic";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const SUMMARY_PATH = `/extensions/${EXTENSION_ID}/entity-library-summary`;

/** The resolved entity-summary view one row renders from. */
export interface EntityLibrarySummaryState {
  total: number;
  monitored: number;
  unsupported: boolean;
  unavailable: boolean;
  loading: boolean;
}

const INITIAL: EntityLibrarySummaryState = Object.freeze({
  total: 0,
  monitored: 0,
  unsupported: false,
  unavailable: false,
  loading: true,
});

const DISABLED: EntityLibrarySummaryState = Object.freeze({
  total: 0,
  monitored: 0,
  unsupported: false,
  unavailable: false,
  loading: false,
});

interface Entry {
  state: EntityLibrarySummaryState;
  listeners: Set<() => void>;
  inflight: Promise<void> | null;
}

const entries = new Map<string, Entry>();

/**
 * Drop every cached per-kind library summary. Called after the settings page saves a new connection — the
 * summary is keyed by kind alone (no connection identity), so a Whisparr URL/key/version change makes it stale.
 * Safe to clear: the rows ride library toolbars, not the settings tab, so the next mount refetches.
 */
export function clearEntityLibrarySummaryCache(): void {
  entries.clear();
}

/**
 * Re-read one kind's toolbar-row summary from Whisparr after a bulk mutation. Resets the entry to
 * loading (clearing any in-flight guard so fetchOnce actually re-fires) and refetches, emitting to
 * the row's subscribers so the monitored-of-total count re-renders.
 */
export function refreshEntityLibrarySummary(kind: string): void {
  const entry = getEntry(kind);
  entry.inflight = null;
  emit(entry, INITIAL);
  fetchOnce(kind);
}

function getEntry(kind: string): Entry {
  let entry = entries.get(kind);
  if (!entry) {
    entry = { state: INITIAL, listeners: new Set(), inflight: null };
    entries.set(kind, entry);
  }
  return entry;
}

function emit(entry: Entry, next: EntityLibrarySummaryState): void {
  entry.state = next;
  for (const listen of entry.listeners) listen();
}

function isVersionUnsupported(err: unknown): boolean {
  return err instanceof ApiError && isVersionUnsupportedBody(err.body);
}

interface SummaryResponse {
  available: boolean;
  total: number;
  monitored: number;
}

function fetchOnce(kind: string): void {
  const entry = getEntry(kind);
  if (entry.inflight) {
    return;
  }
  entry.inflight = (async () => {
    try {
      const res = await request<SummaryResponse>(
        `${SUMMARY_PATH}?kind=${encodeURIComponent(kind)}`,
      );
      emit(entry, {
        total: res.total,
        monitored: res.monitored,
        unsupported: false,
        unavailable: !res.available,
        loading: false,
      });
    } catch (err) {
      emit(entry, {
        total: 0,
        monitored: 0,
        unsupported: isVersionUnsupported(err),
        unavailable: !isVersionUnsupported(err),
        loading: false,
      });
    } finally {
      entry.inflight = null;
    }
  })();
}

/**
 * The live {@link EntityLibrarySummaryState} for one kind's toolbar row. Fetches once per session when
 * `enabled` (the toolbar pill is on); returns the frozen disabled snapshot while off, so the row renders
 * nothing until the user opts in.
 */
export function useEntityLibrarySummary(kind: string, enabled: boolean): EntityLibrarySummaryState {
  const [, bump] = useState(0);
  useEffect(() => {
    if (!enabled) {
      return;
    }
    const entry = getEntry(kind);
    const listener = () => {
      bump((n) => n + 1);
    };
    entry.listeners.add(listener);
    fetchOnce(kind);
    return () => {
      entry.listeners.delete(listener);
    };
  }, [kind, enabled]);

  return enabled ? getEntry(kind).state : DISABLED;
}
