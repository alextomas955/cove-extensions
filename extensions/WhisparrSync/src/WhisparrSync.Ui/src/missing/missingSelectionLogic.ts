/**
 * What the selection bar offers over a page of cards, and what it says when a run is refused before
 * it starts.
 *
 * The invariant this module holds: every action answers a set drawn from the loaded page and from
 * nothing else, so no gesture here can reach the whole result set. That is what keeps a selection
 * from growing with the library, and it is asserted over the answers below rather than over the
 * component that renders them.
 */
import {
  BULK_SELECTION_WAS_NOT_STARTED,
  NO_INSTANCE_CONNECTED,
  WHISPARR_KEEPS_NO_SCENE_RECORDS,
} from "../common/ui/copy";

/** The three gestures the bar offers. A fourth is not representable. */
export type SelectionActionKey = "selectAll" | "selectNone" | "invert";

/** One gesture: what it is called, what key sequence runs it, and what it would leave ticked. */
export interface SelectionAction {
  readonly key: SelectionActionKey;
  readonly label: string;
  /**
   * The binding id Cove's own list page registers this gesture under.
   *
   * Registering the same id resolves through the active preset, so a reader who rebound the gesture
   * elsewhere in Cove gets their binding here rather than a shadowed default.
   */
  readonly shortcutId: string;
  readonly keys: string;
  /** Exactly what would be ticked afterwards, drawn from the loaded page alone. */
  readonly resulting: readonly string[];
}

/** What the bar calls each gesture. */
const SELECT_ALL_LABEL = "Select all";
const SELECT_NONE_LABEL = "Select none";
const INVERT_SELECTION_LABEL = "Invert selection";

/** What the bar calls the one thing it does with a selection. */
export const MONITOR_SELECTION_LABEL = "Monitor";

/** Every card on the loaded page that is not ticked. */
export function invertSelection(
  loadedPageIds: readonly string[],
  selected: ReadonlySet<string>,
): readonly string[] {
  return loadedPageIds.filter((id) => !selected.has(id));
}

/**
 * The three gestures, each carrying the selection it would produce.
 *
 * @param loadedPageIds the scenes currently on screen, in the order they are drawn
 * @param selected which of them are ticked
 */
export function selectionActionsFor(
  loadedPageIds: readonly string[],
  selected: ReadonlySet<string>,
): readonly SelectionAction[] {
  return [
    {
      key: "selectAll",
      label: SELECT_ALL_LABEL,
      shortcutId: "list.select.all",
      keys: "s a",
      resulting: [...loadedPageIds],
    },
    {
      key: "selectNone",
      label: SELECT_NONE_LABEL,
      shortcutId: "list.select.none",
      keys: "s n",
      resulting: [],
    },
    {
      key: "invert",
      label: INVERT_SELECTION_LABEL,
      shortcutId: "list.select.invert",
      keys: "s i",
      resulting: invertSelection(loadedPageIds, selected),
    },
  ];
}

/** Why a run this tab started never began. */
export type SelectionRefusalKind =
  "noInstanceConnected" | "whisparrKeepsNoSceneRecords" | "notStarted";

/** What the last press produced. Carries an outcome and no scene identifiers. */
export type SelectionOutcome =
  | { readonly kind: "atRest" }
  | { readonly kind: "inFlight" }
  | { readonly kind: "started" }
  | { readonly kind: "refused"; readonly refusal: SelectionRefusalKind };

/** No press has been made, or the page under the selection changed. */
export const SELECTION_AT_REST: SelectionOutcome = { kind: "atRest" };

const REFUSAL_LINES: Record<SelectionRefusalKind, string> = {
  noInstanceConnected: NO_INSTANCE_CONNECTED,
  whisparrKeepsNoSceneRecords: WHISPARR_KEEPS_NO_SCENE_RECORDS,
  notStarted: BULK_SELECTION_WAS_NOT_STARTED,
};

/** The kinds, so a caller that must cover them all cannot miss one. */
export const SELECTION_REFUSAL_KINDS: readonly SelectionRefusalKind[] = [
  "noInstanceConnected",
  "whisparrKeepsNoSceneRecords",
  "notStarted",
];

/** The sentence `kind` states, read from the one place each is declared. */
export function selectionRefusalLine(kind: SelectionRefusalKind): string {
  return REFUSAL_LINES[kind];
}

/** The sentence `outcome` states beneath the bar, or null where it states none. */
export function selectionOutcomeLine(outcome: SelectionOutcome): string | null {
  return outcome.kind === "refused" ? selectionRefusalLine(outcome.refusal) : null;
}

/**
 * What the enqueue answered, as one outcome.
 *
 * The route answers a job id with no refusal, or a refusal with no job id. A body neither can be
 * read out of is the same position as no answer at all, so it reads as not started rather than as
 * a run whose progress is somewhere the reader could look for it.
 */
export function selectionOutcomeIn(answer: unknown): SelectionOutcome {
  if (typeof answer !== "object" || answer === null) {
    return { kind: "refused", refusal: "notStarted" };
  }

  const body = answer as { jobId?: unknown; refusal?: unknown };
  if (body.refusal === "none" && typeof body.jobId === "string" && body.jobId !== "") {
    return { kind: "started" };
  }
  if (body.refusal === "noInstanceConnected") {
    return { kind: "refused", refusal: "noInstanceConnected" };
  }
  if (body.refusal === "whisparrKeepsNoSceneRecords") {
    return { kind: "refused", refusal: "whisparrKeepsNoSceneRecords" };
  }
  return { kind: "refused", refusal: "notStarted" };
}
