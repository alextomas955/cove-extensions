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

import { api } from "../common/lib/extension";
import {
  allScenesConfirmation,
  BULK_ACTIONS_COULD_NOT_BE_OFFERED,
  BULK_SELECTION_IS_OVER_THE_BOUND,
  RUN_WAS_NOT_STARTED,
  searchAllMonitoredConfirmation,
} from "../common/ui/copy";
import type { EntityMonitoringView, WhisparrEntityKind } from "../wire/api";
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

  // Read for one of the selected entities: what the connected generation can do is a fact about
  // the connection rather than about that entity.
  const offer = await offeredFor(kind, payload.entityIds[0]);

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

  try {
    // PascalCase, matching the C# request record. Requests bind case-insensitively while responses
    // are camelCase, so the casing is read from the server per direction.
    await postAction(api("entities/bulk-monitor"), {
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

  return {};
}

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
// its code alone and the reader sees a sentence from the copy module.
function refusalSentenceFor(refusal: unknown): string {
  return refusal instanceof ApiError && codeNamedIn(refusal.body) === "TOO_MANY_IDS"
    ? BULK_SELECTION_IS_OVER_THE_BOUND
    : RUN_WAS_NOT_STARTED;
}

function codeNamedIn(answer: string): string | null {
  try {
    const named: unknown = JSON.parse(answer);
    return typeof named === "object" && named !== null && "code" in named
      ? typeof named.code === "string"
        ? named.code
        : null
      : null;
  } catch {
    return null;
  }
}

// Shows one sentence over the selection, with a way out and nothing to choose between. The same
// overlay the offer path reaches when it has nothing to offer.
async function stated(reason: string, count: number): Promise<void> {
  await presentOverlay<BulkMonitorAction>((finish) =>
    createElement(BulkMonitorChoice, { actions: [], count, reason, onChoose: finish }),
  );
}

async function offeredFor(kind: WhisparrEntityKind, coveId: number): Promise<BulkMonitorOffer> {
  try {
    const view = await requestJson<EntityMonitoringView>(
      api(`entity/${kind}/${String(coveId)}/monitoring`),
    );
    return bulkMonitorActions(view);
  } catch {
    // Nothing was read, so nothing is known about the connected generation either.
    return { actions: [], reason: BULK_ACTIONS_COULD_NOT_BE_OFFERED, oneWayDoor: false };
  }
}
