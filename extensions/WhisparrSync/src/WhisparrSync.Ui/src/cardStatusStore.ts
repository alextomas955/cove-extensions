/**
 * Per-card Whisparr status via a coalesced batch: every id requested in the same tick becomes ONE POST
 * /scene-status-batch, so a 40-card page costs one request. Gated by the toolbar pill (`enabled`); a failed
 * fetch resolves each id to `null` so a status read never crashes a card. The cache is per-session (not
 * invalidated on mutation), matching the toolbar summary — a bulk action's effect shows on the next full
 * reload. The cache/coalesce engine is the shared `createBatchCoalescer`; this file owns only the wire shape.
 */
import { useEffect, useState } from "react";
import { request } from "@cove/extension-sdk";
import { createBatchCoalescer } from "./batchCoalescerLogic";
import type { SceneCardStatus } from "./sceneStatusLogic";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const BATCH_PATH = `/extensions/${EXTENSION_ID}/scene-status-batch`;

// The coalescer keys on the string form of the Cove id; the server keys its `states` map the same way. Each
// value is now the { state, hasFile } card status (primary management state + the secondary file signal).
const store = createBatchCoalescer<SceneCardStatus>(async (keys) => {
  const res = await request<{ states?: Record<string, SceneCardStatus> }>(BATCH_PATH, {
    method: "POST",
    body: JSON.stringify({ CoveIds: keys.map(Number) }),
  });
  const states = res.states ?? {};
  return new Map(keys.map((key) => [key, states[key] ?? null]));
});

/**
 * Drop every cached per-scene card status. Called after the settings page saves a new connection — the cache
 * key is the Cove id alone (no connection identity), so a Whisparr URL/key/version change makes every entry
 * stale. Safe to clear outright: the badges ride library slots, never the settings tab, so nothing is
 * subscribed at save time and the next mount refetches.
 */
export function clearCardStatusCache(): void {
  store.clear();
}

/**
 * The Whisparr card status ({@link SceneCardStatus}) for one Cove video, resolved via a coalesced batch across
 * all visible cards. Returns null (no badge) until fetched, when `enabled` is false, or when the scene has no
 * resolvable status.
 */
export function useCardStatus(coveId: number, enabled: boolean): SceneCardStatus | null {
  const key = String(coveId);
  const [, bump] = useState(0);
  useEffect(() => {
    if (!enabled) {
      return;
    }
    store.request(key);
    return store.subscribe(() => {
      bump((n) => n + 1);
    });
  }, [key, enabled]);

  return enabled ? store.get(key) : null;
}
