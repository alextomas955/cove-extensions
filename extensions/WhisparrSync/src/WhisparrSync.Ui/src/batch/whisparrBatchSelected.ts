/**
 * The action handler behind the videos selection bar: it asks what to do with the selected
 * scenes, then hands the whole selection to one background run.
 *
 * The choice is an imperatively mounted overlay because a selection-bar handler owns no React
 * tree, and a confirm dialog answers yes or no where this needs one of five verbs.
 *
 * Leaving without choosing, and every refusal, answer the cancelled result, which suppresses the
 * host's own toast.
 */
import { createElement } from "react";
import type { ActionPayload, HandlerResult } from "@cove-extensions/ui-shared";
import { ApiError } from "@cove-extensions/ui-shared/extensionRequest";
import { presentOverlay } from "@cove-extensions/ui-shared/overlay";
import { postAction } from "@cove-extensions/ui-shared/postAction";

import { errorCodeIn } from "../common/lib/errorCodeLogic";
import { api } from "../common/lib/extension";
import {
  batchSearchIsOverTheBoundSentence,
  bulkSelectionIsOverTheBoundSentence,
  RUN_WAS_NOT_STARTED,
} from "../common/ui/copy";
import { BATCH_MENU_ROWS, type BatchMenuRow } from "./batchMenuLogic";
import { WhisparrBatchChooser } from "./WhisparrBatchChooser";

// The host's selection bar normalizes only the two media plurals, so a studio or performer
// selection arrives plural while a video selection arrives singular. The route names the same
// singular.
const VIDEOS_SELECTION_TYPE = "video";

export async function sceneBatchSelected(
  _action: unknown,
  payload: ActionPayload,
): Promise<HandlerResult> {
  if (payload.entityType !== VIDEOS_SELECTION_TYPE || payload.entityIds.length === 0) {
    return { cancelled: true };
  }

  const chosen = await presentOverlay<BatchMenuRow>((finish) =>
    createElement(WhisparrBatchChooser, {
      rows: BATCH_MENU_ROWS,
      count: payload.entityIds.length,
      reason: null,
      onChoose: finish,
    }),
  );

  if (chosen === null) {
    return { cancelled: true };
  }

  try {
    // PascalCase, matching the C# request record. Requests bind case-insensitively while responses
    // are camelCase, so the casing is read from the server per direction.
    await postAction(api("scenes/batch"), {
      EntityType: payload.entityType,
      Verb: chosen.verb,
      CoveIds: payload.entityIds,
    });
  } catch (refusal) {
    // Nothing is rethrown. A HandlerResult carries no error member, so anything escaping here
    // reaches the host's own alert, which shows the answer's raw text.
    await stated(refusalSentenceFor(refusal), payload.entityIds.length);
    return { cancelled: true };
  }

  return {};
}

// Chosen on the code the answer names, never on its text: a refusal body can carry a full stack
// trace. The bound is the one the route refused above, so the sentence cannot name a limit the
// server does not hold. A refusal naming no bound falls through: a number invented here would read
// as the server's.
function refusalSentenceFor(refusal: unknown): string {
  if (!(refusal instanceof ApiError)) return RUN_WAS_NOT_STARTED;

  const named = errorCodeIn(refusal.body);
  if (named?.max == null) return RUN_WAS_NOT_STARTED;

  switch (named.code) {
    case "TOO_MANY_IDS":
      return bulkSelectionIsOverTheBoundSentence(named.max);
    case "TOO_MANY_SEARCH_IDS":
      return batchSearchIsOverTheBoundSentence(named.max);
    default:
      return RUN_WAS_NOT_STARTED;
  }
}

// Reopens the same overlay with no rows, so the refusal is stated where the choice was made.
async function stated(reason: string, count: number): Promise<void> {
  await presentOverlay<BatchMenuRow>((finish) =>
    createElement(WhisparrBatchChooser, { rows: [], count, reason, onChoose: finish }),
  );
}
