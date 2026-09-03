/**
 * The action handler behind the studios and performers selection bars: it asks which monitoring
 * gesture to carry out, then hands the whole selection to one background job.
 *
 * The choice is an imperatively mounted overlay rather than the browser's own confirm dialog. A
 * confirm answers yes or no, and this needs a verb and a scope; a bulk handler owns no React tree,
 * which is exactly what the imperative mounter was written for. Leaving without choosing is the
 * cancelled result, so the host issues no request and shows no toast.
 *
 * Whisparr's own studio selection bar offers only edit, tags and delete, and puts bulk monitoring
 * inside a tri-state "no change" form. This is the one place this product deliberately does not
 * mimic it: there is no verb menu to mimic, and a tri-state form fits one-off verbs badly. Do not
 * "fix" this toward Whisparr.
 *
 * What the connected generation can do is read for ONE of the selected entities before the overlay
 * opens, because a handler has no mounted store to read it from. That read answers a fact about the
 * connection, not about that entity. A read that fails states that nothing could be offered rather
 * than guessing: a guessed set would put a verb in front of the reader the instance cannot honour,
 * and the refusal would read as a fault in the product.
 */
import { createElement } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";
import type { ActionPayload, HandlerResult } from "@cove-extensions/ui-shared";
import { postAction } from "@cove-extensions/ui-shared/postAction";

import { api } from "../common/lib/extension";
import { BULK_ACTIONS_COULD_NOT_BE_OFFERED } from "../common/ui/copy";
import type { EntityMonitoringView, WhisparrEntityKind } from "../wire/api";
import { BulkMonitorChoice } from "./BulkMonitorChoice";
import {
  bulkMonitorActions,
  type BulkMonitorAction,
  type BulkMonitorOffer,
} from "./monitorMenuLogic";
import { presentOverlay } from "@cove-extensions/ui-shared/overlay";

/**
 * Which entity kind each selection type names.
 *
 * The host's selection bar normalizes only the two media plurals, so a studio or performer selection
 * arrives PLURAL. The read route names one entity and takes the singular, so the two spellings meet
 * here and nowhere else.
 */
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

  // One of the selected entities, because what the connected generation can do is a fact about the
  // connection rather than about that entity.
  const offer = await offeredFor(kind, payload.entityIds[0]);

  const chosen = await presentOverlay<BulkMonitorAction>((finish) =>
    createElement(BulkMonitorChoice, {
      actions: offer.actions,
      reason: offer.reason,
      onChoose: finish,
    }),
  );

  if (chosen === null) {
    return { cancelled: true };
  }

  // PascalCase, matching the C# request record. Requests bind case-insensitively while responses are
  // camelCase, so the casing is read from the server per direction rather than assumed to be one.
  await postAction(api("entities/bulk-monitor"), {
    EntityType: payload.entityType,
    Verb: chosen.verb,
    Scope: chosen.scope,
    EntityIds: payload.entityIds,
  });

  return {};
}

async function offeredFor(kind: WhisparrEntityKind, coveId: number): Promise<BulkMonitorOffer> {
  try {
    const view = await requestJson<EntityMonitoringView>(
      api(`entity/${kind}/${String(coveId)}/monitoring`),
    );
    return bulkMonitorActions(view);
  } catch {
    return { actions: [], reason: BULK_ACTIONS_COULD_NOT_BE_OFFERED };
  }
}
