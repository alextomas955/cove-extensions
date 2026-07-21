/**
 * Per-card studio/performer Whisparr status via a coalesced batch, keyed by "kind:coveId". Every id a page
 * requests in the same tick becomes ONE POST /entity-status-batch per kind, so a studios/performers page costs
 * ONE Whisparr fetch (the entity/site list), not per card. A failed/deferred read (bad key, unreachable, or a
 * v2 performer — v2 has no performer entity) resolves each id to `null` so a status read never crashes a card.
 * Per-session cache (not invalidated on mutation), like the scene store. The cache/coalesce engine is the
 * shared `createBatchCoalescer`; this file owns the "kind:id" key format and the per-kind wire fan-out.
 */
import { useEffect, useState } from "react";
import { request } from "@cove/extension-sdk";
import { createBatchCoalescer } from "./batchCoalescerLogic";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const BATCH_PATH = `/extensions/${EXTENSION_ID}/entity-status-batch`;

/**
 * Whisparr status for one studio/performer: whether it exists in Whisparr, is monitored, and Whisparr's own
 * `scenesPresent`/`scenesTotal` (scenes present in the library over the entity's full StashDB catalog).
 */
export interface EntityCardStatus {
  added: boolean;
  monitored: boolean;
  scenesPresent: number;
  scenesTotal: number;
}

const cacheKey = (kind: string, id: number): string => `${kind}:${id}`;

// The coalescer hands back every un-cached "kind:id" key this tick; group them by kind → one POST per kind
// (parallel), each with its OWN try/catch so a failing studio fetch never nulls a succeeding performer fetch.
const store = createBatchCoalescer<EntityCardStatus>(async (keys) => {
  const byKind = new Map<string, number[]>();
  for (const key of keys) {
    const sep = key.indexOf(":");
    const kind = key.slice(0, sep);
    const id = Number(key.slice(sep + 1));
    const ids = byKind.get(kind);
    if (ids) {
      ids.push(id);
    } else {
      byKind.set(kind, [id]);
    }
  }

  const out = new Map<string, EntityCardStatus | null>();
  await Promise.all(
    [...byKind.entries()].map(async ([kind, ids]) => {
      try {
        const res = await request<{ states?: Record<string, EntityCardStatus> }>(BATCH_PATH, {
          method: "POST",
          body: JSON.stringify({ Kind: kind, CoveEntityIds: ids }),
        });
        const states = res.states ?? {};
        for (const id of ids) {
          out.set(cacheKey(kind, id), states[String(id)] ?? null);
        }
      } catch {
        for (const id of ids) {
          out.set(cacheKey(kind, id), null);
        }
      }
    }),
  );
  return out;
});

/**
 * Drop every cached studio/performer card status. Called after the settings page saves a new connection — the
 * cache key is "kind:coveId" with no connection identity, so a Whisparr URL/key/version change makes every
 * entry stale. Safe to clear: the badges ride library slots, not the settings tab, so the next mount refetches.
 */
export function clearEntityCardStatusCache(): void {
  store.clear();
}

/**
 * Re-read the given entities' card status from Whisparr after a bulk mutation. Evicts each `kind:id`
 * key THEN re-requests it, so the coalescer folds them into ONE POST /entity-status-batch and the
 * flush re-renders subscribed cards. Eviction must precede the re-request — `request` short-circuits
 * on a still-cached key, so a bare re-request would be a no-op.
 */
export function refreshEntityCardStatus(kind: string, coveIds: number[]): void {
  const keys = coveIds.map((id) => cacheKey(kind, id));
  store.evict(keys);
  for (const key of keys) {
    store.request(key);
  }
}

/**
 * The Whisparr status for one studio/performer, resolved via a coalesced per-kind batch across the visible cards.
 * Returns null (no badge) until fetched, when `enabled` is false, or when the entity has no resolvable status.
 */
export function useEntityCardStatus(
  kind: string,
  coveId: number,
  enabled: boolean,
): EntityCardStatus | null {
  const key = cacheKey(kind, coveId);
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
