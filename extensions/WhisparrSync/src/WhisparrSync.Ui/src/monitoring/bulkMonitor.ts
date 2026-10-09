/**
 * The action handler behind the studios and performers selection bars: it asks which monitoring
 * gesture to carry out, then hands the whole selection to one background job.
 *
 * The choice is an imperatively mounted overlay because a bulk handler owns no React tree and a
 * browser confirm answers only yes or no. Leaving without choosing is the cancelled result, so the
 * host issues no request and shows no toast.
 */
import { createElement } from "react";
import { ApiError, requestJson } from "@cove-extensions/ui-shared/extensionRequest";
import type { ActionPayload, HandlerResult } from "@cove-extensions/ui-shared";
import { postAction } from "@cove-extensions/ui-shared/postAction";

import { errorCodeIn } from "../common/lib/errorCodeLogic";
import { api } from "../common/lib/extension";
import { announceWhenRunEnds } from "../common/lib/cardsChanged";
import {
  allScenesConfirmation,
  BULK_ACTIONS_COULD_NOT_BE_OFFERED,
  bulkSelectionIsOverTheBoundSentence,
  RUN_WAS_NOT_STARTED,
  searchAllMonitoredConfirmation,
} from "../common/ui/copy";
import type {
  EntityMonitoringView,
  LibraryCardKind,
  WhisparrConnectionOffer,
  WhisparrEntityKind,
} from "../wire/api";
import { BulkMonitorChoice } from "./BulkMonitorChoice";
import { ConfirmDialog } from "./hostComponents";
import {
  bulkMonitorActions,
  type BulkMonitorAction,
  type BulkMonitorOffer,
} from "./monitorMenuLogic";
import { presentOverlay } from "@cove-extensions/ui-shared/overlay";

// The host's selection bar normalizes only the two media plurals, so a studio or performer
// selection arrives plural. The read route takes the singular, so the two spellings meet here.
const ENTITY_KIND_BEHIND_SELECTION_TYPE: Record<string, WhisparrEntityKind | undefined> = {
  studios: "studio",
  performers: "performer",
};

export async function monitorSelected(
  _action: unknown,
  payload: ActionPayload,
): Promise<HandlerResult> {
  const kind = ENTITY_KIND_BEHIND_SELECTION_TYPE[payload.entityType];
  if (kind === undefined || payload.entityIds.length === 0) {
    return { cancelled: true };
  }

  // Read of the connection, naming no entity: which gestures this menu offers follows the
  // connected generation, and the sampled entity's own state is never read from it.
  const offer = await offeredFor(kind);

  const chosen = await presentOverlay<BulkMonitorAction>((finish) =>
    createElement(BulkMonitorChoice, {
      actions: offer.actions,
      count: payload.entityIds.length,
      reason: offer.reason,
      onChoose: finish,
    }),
  );

  if (chosen === null) {
    return { cancelled: true };
  }

  const confirmation = confirmationFor(chosen, offer, payload.entityIds.length);
  if (confirmation !== null && !(await confirmed(chosen, confirmation))) {
    return { cancelled: true };
  }

  let started: { jobId?: string } = {};
  try {
    // PascalCase, matching the C# request record. Requests bind case-insensitively while responses
    // are camelCase, so the casing is read from the server per direction.
    started = await postAction<{ jobId?: string }>(api("entities/bulk-monitor"), {
      EntityType: payload.entityType,
      Verb: chosen.verb,
      Scope: chosen.scope,
      EntityIds: payload.entityIds,
    });
  } catch (refusal) {
    // Nothing is rethrown. A HandlerResult carries no error member, so anything escaping here
    // reaches the host's own alert, which shows the answer's raw text.
    await stated(refusalSentenceFor(refusal), payload.entityIds.length);
    return { cancelled: true };
  }

  // Not awaited: the run is the host's job drawer's to report, and the badges are this bundle's to
  // repaint once it has finished. Awaiting it would hold the selection bar for the length of the run.
  void announceWhenRunEnds(CARD_KIND_OF_ENTITY[kind], payload.entityIds, started.jobId);

  return {};
}

// One card kind per entity kind, so a run over studios repaints studio cards and nothing else.
const CARD_KIND_OF_ENTITY: Record<WhisparrEntityKind, LibraryCardKind> = {
  studio: "studio",
  performer: "performer",
  tag: "studio",
};

// Only the two rows that spend something the reader cannot take back are confirmed: the wider
// scope marks a whole back catalogue wanted, and the search downloads.
function confirmationFor(
  action: BulkMonitorAction,
  offer: BulkMonitorOffer,
  selected: number,
): string | null {
  if (action.marksTheBackCatalogue) {
    return allScenesConfirmation(selected, offer.oneWayDoor);
  }

  return action.verb === "searchAllMonitored" ? searchAllMonitoredConfirmation(selected) : null;
}

// A second imperative overlay after the first has resolved, rather than a dialog inside the
// chooser: this handler owns no React tree to render one into.
async function confirmed(action: BulkMonitorAction, message: string): Promise<boolean> {
  const answer = await presentOverlay<BulkMonitorAction>((finish) =>
    createElement(ConfirmDialog, {
      open: true,
      title: action.label,
      confirmLabel: action.label,
      message,
      onConfirm: () => {
        finish(action);
      },
      onCancel: () => {
        finish(null);
      },
    }),
  );

  return answer !== null;
}

// The instance answers a refusal with a body carrying a full stack trace, so the body is read for
// its declared members alone and the reader sees a sentence from the copy module. The bound is the
// one the route refused above; a refusal naming no bound falls through, because a number invented
// here would read as the server's.
function refusalSentenceFor(refusal: unknown): string {
  if (!(refusal instanceof ApiError)) return RUN_WAS_NOT_STARTED;

  const named = errorCodeIn(refusal.body);
  return named?.code === "TOO_MANY_IDS" && named.max != null
    ? bulkSelectionIsOverTheBoundSentence(named.max)
    : RUN_WAS_NOT_STARTED;
}

// Shows one sentence over the selection, with a way out and nothing to choose between. The same
// overlay the offer path reaches when it has nothing to offer.
async function stated(reason: string, count: number): Promise<void> {
  await presentOverlay<BulkMonitorAction>((finish) =>
    createElement(BulkMonitorChoice, { actions: [], count, reason, onChoose: finish }),
  );
}

async function offeredFor(kind: WhisparrEntityKind): Promise<BulkMonitorOffer> {
  try {
    const connection = await requestJson<WhisparrConnectionOffer>(api("connection/offer"));
    return bulkMonitorActions(asMonitoringView(kind, connection));
  } catch {
    // Nothing was read, so nothing is known about the connected generation either.
    return { actions: [], reason: BULK_ACTIONS_COULD_NOT_BE_OFFERED, oneWayDoor: false };
  }
}

/**
 * The connection's facts in the shape the menu reads.
 *
 * Every entity-shaped member carries what the menu treats as unset: it rebuilds both monitored
 * states itself, and a refusal one entity earned is not a fact about a selection.
 */
function asMonitoringView(
  kind: WhisparrEntityKind,
  connection: WhisparrConnectionOffer,
): EntityMonitoringView {
  return {
    kind,
    generation: connection.generation,
    capabilities: connection.capabilities,
    present: null,
    monitored: false,
    refusal: connection.configured ? "none" : "notConfigured",
    scope: null,
    scopeChangeIsRetroactive: connection.scopeChangeIsRetroactive,
  };
}
