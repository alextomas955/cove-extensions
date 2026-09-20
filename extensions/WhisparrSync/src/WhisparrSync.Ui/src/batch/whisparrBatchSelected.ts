/**
 * The action handler behind the videos selection bar: it asks what to do with the selected scenes,
 * then hands the whole selection to one background run.
 *
 * The choice is an imperatively mounted overlay because a selection-bar handler owns no React tree,
 * and a confirm dialog answers yes or no where this needs one of five verbs.
 *
 * Leaving without choosing, and every refusal, answer the cancelled result. That makes the host
 * suppress its own toast. The host never clears the selection, so a refused gesture leaves the
 * reader with the selection they made.
 */
import { createElement } from "react";
import type { ActionPayload, HandlerResult } from "@cove-extensions/ui-shared";
import { ApiError } from "@cove-extensions/ui-shared/extensionRequest";
import { presentOverlay } from "@cove-extensions/ui-shared/overlay";
import { postAction } from "@cove-extensions/ui-shared/postAction";

import { api } from "../common/lib/extension";
import {
  BATCH_SEARCH_IS_OVER_THE_BOUND,
  BULK_SELECTION_IS_OVER_THE_BOUND,
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
// trace.
function refusalSentenceFor(refusal: unknown): string {
  if (!(refusal instanceof ApiError)) return RUN_WAS_NOT_STARTED;

  switch (codeNamedIn(refusal.body)) {
    case "TOO_MANY_IDS":
      return BULK_SELECTION_IS_OVER_THE_BOUND;
    case "TOO_MANY_SEARCH_IDS":
      return BATCH_SEARCH_IS_OVER_THE_BOUND;
    default:
      return RUN_WAS_NOT_STARTED;
  }
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

// Reopens the same overlay with no rows, so the refusal is stated where the choice was made.
async function stated(reason: string, count: number): Promise<void> {
  await presentOverlay<BatchMenuRow>((finish) =>
    createElement(WhisparrBatchChooser, { rows: [], count, reason, onChoose: finish }),
  );
}
