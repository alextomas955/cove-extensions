/**
 * What the connected instance holds for each card on screen, one coalescer per card kind.
 *
 * Module scope because each card renders its own component instance and they have to fold into one
 * request. One coalescer per kind, so one entity's answer cannot paint onto another kind's card
 * with the same number.
 *
 * The page-level reason is held per kind while an answered card of that kind is on screen, and the
 * toolbar control states it once for the page.
 */
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import { onCardsChanged, onCardsRunning } from "../common/lib/cardsChanged";
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

// The cards a run this browser started is still working through, keyed as the coalescers are.
const running = new Map<LibraryCardKind, Set<string>>();

// What a reader subscribes over. A snapshot of the answers is a fresh array on every read, which a
// subscription cannot compare and so re-renders forever.
let version = 0;

function emit(): void {
  version++;
  for (const listener of listeners) listener();
}

/** How many times the answers have changed. Stable between changes, so a subscription can hold it. */
export function cardStatusVersion(): number {
  return version;
}

function recordRefusal(kind: LibraryCardKind, refusal: LibraryPageRefusal): void {
  // A page over the route's bound is read across several requests, so a later request never
  // overwrites the reason an earlier one established. The page states the first reason.
  const held = refusals.get(kind);
  if (held !== undefined && held !== "none") return;
  refusals.set(kind, refusal);
}

// `noAnswersHeld` is true when no card of this kind on screen has an answer yet, and the kind's
// reason is dropped there and nowhere else. While one answered card is on screen the reason belongs
// to its page.
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
    // Every way the request itself can fail lands here, and each draws no badge on any card.
    // Without a reason on the page, the control was pressed and nothing happened.
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

// Subscribed at module scope, for the lifetime of the bundle: the announcement can arrive while no
// card is mounted, and a kind nothing is on screen for is left alone by the re-read itself.
onCardsChanged((kind, coveIds) => {
  rereadCardStatus(kind, coveIds);
});

// Subscribed at module scope for the bundle's lifetime, as the change bus is: a run can start and
// end while no card of that kind is mounted.
onCardsRunning((kind, coveIds, isRunning) => {
  const held = running.get(kind) ?? new Set<string>();
  for (const coveId of coveIds) {
    if (isRunning) {
      held.add(String(coveId));
    } else {
      held.delete(String(coveId));
    }
  }

  running.set(kind, held);
  emit();
});

/** Whether a run this browser started is still working through one card. */
export function cardIsRunning(kind: LibraryCardKind, coveId: number): boolean {
  return running.get(kind)?.has(String(coveId)) ?? false;
}

/**
 * Reads the named cards again, for a caller that changed what the instance holds for them.
 *
 * Narrowed to those cards rather than the whole page: on one generation the read behind them is a
 * request per card, so repainting a page over one changed card would spend the rest for nothing.
 * The page-level reason is dropped with the answers, because the read that follows establishes its
 * own.
 */
function rereadCardStatus(kind: LibraryCardKind, coveIds: readonly number[]): void {
  const held = coalescers.get(kind);
  if (held === undefined) return;

  refusals.delete(kind);
  held.reread(coveIds.map(String));
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
 * Read across every kind, because the control that states it reads no slot context and so has no
 * kind of its own to ask about. One page mounts cards of one kind.
 */
export function cardStatusRefusal(): LibraryPageRefusal {
  for (const refusal of refusals.values()) {
    if (refusal !== "none") return refusal;
  }
  return "none";
}

/**
 * What the instance holds for every card of `kind` still on screen, in no particular order.
 *
 * A null entry is a card the read answered nothing for. The array shrinks as cards unmount, so a
 * count taken from it describes the page in front of the reader.
 */
export function readAnsweredCardStatuses(kind: LibraryCardKind): (LibraryCardReading | null)[] {
  return coalescers.get(kind)?.answered() ?? [];
}

/** How many cards of `kind` have registered an identifier. */
export function registeredCardCountFor(kind: LibraryCardKind): number {
  return coalescers.get(kind)?.registered() ?? 0;
}

/** How many cards have registered an identifier, across every kind. */
export function registeredCardCount(): number {
  let held = 0;
  for (const coalescer of coalescers.values()) held += coalescer.registered();
  return held;
}
