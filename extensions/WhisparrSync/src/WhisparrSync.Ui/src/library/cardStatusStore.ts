/**
 * What the connected instance holds for each card on screen, one coalescer per card kind.
 *
 * Module scope for the same reason `libraryToggleStore.ts` is: each card renders its own component
 * instance and they have to fold into one request. The key carries the kind as well as the Cove id,
 * so one entity's answer cannot paint onto another kind's card with the same number.
 *
 * The page-level reason is held per kind and read by the toolbar control, which states it once for
 * the page. A card that could not be answered for draws nothing and says nothing.
 */
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import { api } from "../common/lib/extension";
import type { LibraryCardReading, LibraryStatusRefusalKind, LibraryStatusView } from "../wire/api";
import { createBatchCoalescer, type BatchCoalescer } from "./batchCoalescerLogic";

/**
 * The card kinds the status route answers for.
 *
 * The route names its kind in a path segment, so the value reaches no request or response body and
 * cannot be generated into this bundle's wire types. `cardSlotProps.test.ts` pins these three names
 * against the enum the server declares.
 */
export type LibraryCardKind = "video" | "studio" | "performer";

const coalescers = new Map<LibraryCardKind, BatchCoalescer<LibraryCardReading>>();
const refusals = new Map<LibraryCardKind, LibraryStatusRefusalKind>();
const listeners = new Set<() => void>();

function emit(): void {
  for (const listener of listeners) listener();
}

/** One request for every card of `kind` that registered inside the same tick. */
async function readBatch(
  kind: LibraryCardKind,
  keys: string[],
): Promise<Map<string, LibraryCardReading | null>> {
  const view = await requestJson<LibraryStatusView>(api(`library/${kind}/status`), {
    method: "POST",
    body: JSON.stringify({ coveIds: keys.map((key) => Number(key)) }),
  });

  refusals.set(kind, view.refusal);
  emit();

  return new Map(view.rows.map((row) => [String(row.coveId), row.reading]));
}

function coalescerFor(kind: LibraryCardKind): BatchCoalescer<LibraryCardReading> {
  const held = coalescers.get(kind);
  if (held !== undefined) return held;

  const made = createBatchCoalescer<LibraryCardReading>((keys) => readBatch(kind, keys));
  made.subscribe(emit);
  coalescers.set(kind, made);
  return made;
}

/** Subscribes to every change: a settled batch, a page-level reason, and how many cards asked. */
export function subscribeCardStatus(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/**
 * Registers one card and returns the release its component calls when it unmounts.
 *
 * Both ends emit, because the registered count is what the toolbar control reads to tell a display
 * mode that mounts no card from one that mounts cards the extension cannot speak for.
 */
export function requestCardStatus(kind: LibraryCardKind, coveId: number): () => void {
  const release = coalescerFor(kind).request(String(coveId));
  emit();

  return () => {
    release();
    emit();
  };
}

/** What the instance holds for one card, or null where nothing was established for it. */
export function readCardStatus(kind: LibraryCardKind, coveId: number): LibraryCardReading | null {
  return coalescers.get(kind)?.get(String(coveId)) ?? null;
}

/** Whether one card's read has answered, which tells an absent answer from an unfinished one. */
export function cardStatusSettled(kind: LibraryCardKind, coveId: number): boolean {
  return coalescers.get(kind)?.settled(String(coveId)) ?? false;
}

/**
 * Why the page could not be answered for, or that it could.
 *
 * Read across every kind rather than per kind. The control that states it serves all three list
 * pages and reads no slot context, so it has no kind of its own to ask about, and one page mounts
 * cards of one kind.
 */
export function cardStatusRefusal(): LibraryStatusRefusalKind {
  for (const refusal of refusals.values()) {
    if (refusal !== "none") return refusal;
  }
  return "none";
}

/** How many cards have registered an identifier, across every kind. */
export function registeredCardCount(): number {
  let held = 0;
  for (const coalescer of coalescers.values()) held += coalescer.registered();
  return held;
}
