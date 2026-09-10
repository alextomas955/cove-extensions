/**
 * What the connected instance holds for each card on screen, one coalescer per card kind.
 *
 * Module scope for the same reason `libraryToggleStore.ts` is: each card renders its own component
 * instance and they have to fold into one request. The key carries the kind as well as the Cove id,
 * so one entity's answer cannot paint onto another kind's card with the same number.
 *
 * The page-level reason is held per kind for as long as an answered card of that kind is on screen,
 * and read by the toolbar control, which states it once for the page. A card that could not be
 * answered for draws nothing and says nothing.
 */
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import { api } from "../common/lib/extension";
import type { LibraryCardKind, LibraryCardReading, LibraryStatusView } from "../wire/api";
import {
  createBatchCoalescer,
  type BatchCoalescer,
  type FetchedBatch,
} from "./batchCoalescerLogic";
import type { LibraryPageRefusal } from "./libraryRefusalLogic";

const coalescers = new Map<LibraryCardKind, BatchCoalescer<LibraryCardReading>>();
const refusals = new Map<LibraryCardKind, LibraryPageRefusal>();
const listeners = new Set<() => void>();

function emit(): void {
  for (const listener of listeners) listener();
}

function recordRefusal(kind: LibraryCardKind, refusal: LibraryPageRefusal): void {
  // A page over the route's bound is asked for across requests, and the page has a reason when any
  // one of those requests could not be answered. So a request that answered never overwrites the reason
  // an earlier one established. Two requests can fail for reasons of their own, and the page states
  // the first: one sentence that stands still while the rest of the page answers.
  const held = refusals.get(kind);
  if (held !== undefined && held !== "none") return;
  refusals.set(kind, refusal);
}

/**
 * One request for the cards of `kind` handed over, whatever the route answers for.
 *
 * `noAnswersHeld` is true when no card of this kind on screen has an answer yet, and the kind's
 * reason is dropped there and nowhere else. While one answered card is still on screen the reason
 * belongs to its page, so neither a later request of that page nor the first request of a card that
 * mounted during it can displace it. A page whose cards have all left takes its reason with them.
 */
async function readBatch(
  kind: LibraryCardKind,
  keys: string[],
  noAnswersHeld: boolean,
): Promise<FetchedBatch<LibraryCardReading>> {
  if (noAnswersHeld) refusals.delete(kind);

  try {
    const view = await requestJson<LibraryStatusView>(api(`library/${kind}/status`), {
      method: "POST",
      body: JSON.stringify({ coveIds: keys.map((key) => Number(key)) }),
    });

    recordRefusal(kind, view.refusal);
    emit();

    return {
      answers: new Map(view.rows.map((row) => [String(row.coveId), row.reading])),
      moreNotAnswered: view.moreNotAnswered,
    };
  } catch (failure) {
    // Every way the request itself can fail lands here: a body the route refused, a tier the reader
    // does not hold, a failure inside Cove, or a connection to Cove that dropped. Each of them draws
    // no badge on any card, so without a reason on the page the control was pressed and nothing
    // happened.
    recordRefusal(kind, "statusCouldNotBeRead");
    emit();
    throw failure;
  }
}

function coalescerFor(kind: LibraryCardKind): BatchCoalescer<LibraryCardReading> {
  const held = coalescers.get(kind);
  if (held !== undefined) return held;

  const made = createBatchCoalescer<LibraryCardReading>((keys, noAnswersHeld) =>
    readBatch(kind, keys, noAnswersHeld),
  );
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
export function cardStatusRefusal(): LibraryPageRefusal {
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
