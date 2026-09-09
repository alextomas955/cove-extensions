/**
 * Pure rules for the scene tab's four controls: what each is called, what it states beneath itself,
 * which one carries the accent fill, and why a control that cannot act cannot act.
 *
 * Relative imports only, so this module runs with no environment and needs no doubles. The wire
 * types arrive as `import type`, which erases at runtime and so takes nothing with it.
 *
 * The invariant this module holds: a reason disables a control and a null reason enables it, so a
 * dimmed control with nothing to hear is unrepresentable, and at most one control answers the
 * primary variant for any input.
 */
import type { SceneDetailView, SceneRefusalKind } from "../wire/api";
import type { WhisparrEntityState } from "../common/ui/stateVocabularyLogic";
import { deriveState } from "../common/ui/stateVocabularyLogic";
import {
  ACTION_DID_NOT_REACH_WHISPARR,
  INSTANCE_OFFERS_NO_QUALITY_PROFILE,
  INSTANCE_OFFERS_NO_ROOT_FOLDER,
  INSTANCE_REFUSED,
  MONITOR_IN_WHISPARR,
  MONITORING_COULD_NOT_BE_READ,
  NO_IDENTITY_IN_THIS_NAMESPACE,
  NO_INSTANCE_CONNECTED,
  SCENE_ADD,
  SCENE_ADD_STATES,
  SCENE_EXCLUDE,
  SCENE_EXCLUDE_STATES,
  SCENE_IS_ALREADY_IN_WHISPARR,
  SCENE_IS_ON_THE_EXCLUSION_LIST,
  SCENE_MONITOR_NEEDS_AN_ENTRY,
  SCENE_MONITOR_STATES,
  SCENE_REMOVE_EXCLUSION,
  SCENE_REMOVE_EXCLUSION_STATES,
  SCENE_SEARCH,
  SCENE_SEARCH_IS_WITH_WHISPARR,
  SCENE_SEARCH_NEEDS_AN_ENTRY,
  SCENE_SEARCH_NEEDS_MONITORING,
  SCENE_SEARCH_STATES,
  SCENE_STOP_MONITORING_STATES,
  SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
  STOP_MONITORING_IN_WHISPARR,
  WAITING_FOR_WHISPARR,
  WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  WHISPARR_STATUS_COULD_NOT_BE_READ,
} from "../common/ui/copy";

/** One verb the tab can carry out, named as the route segment it is served at. */
export type SceneVerb = "add" | "monitor" | "unmonitor" | "search" | "exclude" | "removeExclusion";

/** The route segment each verb is served at, off the scene's own base. */
const SCENE_VERB_ROUTES: Record<SceneVerb, string> = {
  add: "add",
  monitor: "monitor",
  unmonitor: "unmonitor",
  search: "search",
  exclude: "exclude",
  removeExclusion: "remove-exclusion",
};

/** Which of the four controls one face belongs to. Two verbs share the exclusion control. */
export type SceneControlKey = "add" | "monitor" | "search" | "exclude";

/** The four controls, in the order the tab draws them. */
export const SCENE_CONTROL_KEYS: readonly SceneControlKey[] = [
  "add",
  "monitor",
  "search",
  "exclude",
];

/** One control as the tab draws it. */
export interface SceneControl {
  readonly key: SceneControlKey;
  /** What the control is called. Always a fixed constant; no instance-supplied text enters it. */
  readonly label: string;
  /** The one sentence stated beneath it, outside the button. */
  readonly states: string;
  readonly variant: "primary" | "ghost";
  /** The one sentence saying why it cannot be pressed, or null when it can. */
  readonly reason: string | null;
  readonly verb: SceneVerb;
}

/** What the tab draws beneath the fact block. */
export interface SceneControlState {
  readonly add: SceneControl;
  readonly monitor: SceneControl;
  readonly search: SceneControl;
  readonly exclude: SceneControl;
  /**
   * The one sentence to state above the controls for a reason two or more of them share, or null.
   *
   * A reason stopping a single control rides that control instead, so this is never a second place
   * saying what one control already says.
   */
  readonly sharedReason: string | null;
  /** How many controls {@link sharedReason} stops. Zero where there is none. */
  readonly affectedControls: number;
  /** What the status line beneath the controls states, or null where there is nothing to say. */
  readonly statusLine: SceneStatusLine | null;
  readonly state: WhisparrEntityState;
}

/** What the status line beneath the controls says, and whether it reports a failure. */
export interface SceneStatusLine {
  readonly sentence: string;
  /** Whether the outcome failed, which is what decides the tone it reads in. */
  readonly failed: boolean;
}

/** What the read's own refusal states, and how many of the tab's surfaces it stops. */
export interface SceneReadRefusal {
  readonly sentence: string | null;
  readonly affectedControls: number;
}

/**
 * What each refusal states when a READ answers it, or null where a read never answers it.
 *
 * Total by TYPE, so a member added to the wire enum fails this build rather than rendering a tab
 * with no decision made about it. The kinds mapped to null are the ones only a verb can answer, and
 * a verb's refusal is read through {@link deriveSceneControls} instead.
 */
const SENTENCE_FOR_A_READ_REFUSAL: Record<SceneRefusalKind, string | null> = {
  none: null,
  noInstanceConnected: NO_INSTANCE_CONNECTED,
  noIdentityInThisNamespace: NO_IDENTITY_IN_THIS_NAMESPACE,
  severalIdentitiesInThisNamespace: SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
  capabilityAbsentOnThisGeneration: WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  didNotReachWhisparr: WHISPARR_STATUS_COULD_NOT_BE_READ,
  instanceRefused: INSTANCE_REFUSED,
  instanceOffersNoQualityProfile: null,
  instanceOffersNoRootFolder: null,
  whisparrHasNoEntryForScene: null,
  whisparrAlreadyHoldsThisScene: null,
  whisparrIsNotMonitoringThisScene: null,
};

/** Where a verb's own refusal is stated. */
type VerbRefusalPlace = "sharedNotice" | "add" | "search" | "statusLine" | "theFactsSayIt";

/**
 * Where each refusal is stated when a VERB answers it.
 *
 * Total by TYPE. `theFactsSayIt` is a refusal naming a state: every verb is followed by a re-read,
 * and the state it answers puts that fact on the controls themselves, so a fourth place stating it
 * would be a second copy of what the chip and the control reasons already say.
 */
const PLACE_FOR_A_VERB_REFUSAL: Record<SceneRefusalKind, VerbRefusalPlace> = {
  none: "theFactsSayIt",
  noInstanceConnected: "sharedNotice",
  noIdentityInThisNamespace: "sharedNotice",
  severalIdentitiesInThisNamespace: "sharedNotice",
  capabilityAbsentOnThisGeneration: "sharedNotice",
  didNotReachWhisparr: "statusLine",
  instanceRefused: "statusLine",
  instanceOffersNoQualityProfile: "add",
  instanceOffersNoRootFolder: "add",
  whisparrHasNoEntryForScene: "theFactsSayIt",
  whisparrAlreadyHoldsThisScene: "theFactsSayIt",
  whisparrIsNotMonitoringThisScene: "theFactsSayIt",
};

/**
 * What each refusal states when a VERB answers it, or null where the verb answers no sentence.
 *
 * Held apart from the read's own table because the two questions differ: a read that never arrived
 * cannot state a status, and a verb that never arrived changed nothing.
 */
const SENTENCE_FOR_A_VERB_REFUSAL: Record<SceneRefusalKind, string | null> = {
  none: null,
  noInstanceConnected: NO_INSTANCE_CONNECTED,
  noIdentityInThisNamespace: NO_IDENTITY_IN_THIS_NAMESPACE,
  severalIdentitiesInThisNamespace: SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
  capabilityAbsentOnThisGeneration: WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  didNotReachWhisparr: ACTION_DID_NOT_REACH_WHISPARR,
  instanceRefused: INSTANCE_REFUSED,
  instanceOffersNoQualityProfile: INSTANCE_OFFERS_NO_QUALITY_PROFILE,
  instanceOffersNoRootFolder: INSTANCE_OFFERS_NO_ROOT_FOLDER,
  whisparrHasNoEntryForScene: null,
  whisparrAlreadyHoldsThisScene: null,
  whisparrIsNotMonitoringThisScene: null,
};

/**
 * Which control the accent fill goes on in each state, or null where none takes it.
 *
 * Total by TYPE, so a sixth state fails this build rather than drawing a control set with no
 * decision made about it. Never the search control and never the excluding half: a search spends
 * the reader's indexer traffic and disk and can download a file, and an accent fill on the one
 * control with an external cost invites the press.
 */
const ACCENT_FILL_IN: Record<WhisparrEntityState, SceneControlKey | null> = {
  notAdded: "add",
  unmonitored: "monitor",
  monitored: null,
  excluded: "exclude",
  statusUnknown: null,
};

/** The reason each control gives in each state, or null where the state does not stop it. */
const REASON_IN: Record<WhisparrEntityState, Record<SceneControlKey, string | null>> = {
  notAdded: {
    add: null,
    monitor: SCENE_MONITOR_NEEDS_AN_ENTRY,
    search: SCENE_SEARCH_NEEDS_AN_ENTRY,
    exclude: null,
  },
  unmonitored: {
    add: SCENE_IS_ALREADY_IN_WHISPARR,
    monitor: null,
    search: SCENE_SEARCH_NEEDS_MONITORING,
    exclude: null,
  },
  monitored: {
    add: SCENE_IS_ALREADY_IN_WHISPARR,
    monitor: null,
    search: null,
    exclude: null,
  },
  excluded: {
    add: SCENE_IS_ON_THE_EXCLUSION_LIST,
    monitor: SCENE_IS_ON_THE_EXCLUSION_LIST,
    search: SCENE_IS_ON_THE_EXCLUSION_LIST,
    exclude: null,
  },
  statusUnknown: {
    add: MONITORING_COULD_NOT_BE_READ,
    monitor: MONITORING_COULD_NOT_BE_READ,
    search: MONITORING_COULD_NOT_BE_READ,
    exclude: MONITORING_COULD_NOT_BE_READ,
  },
};

/** How many controls a reason all four share stops. */
const EVERY_CONTROL = SCENE_CONTROL_KEYS.length;

/** What the derivation is given: the answered read, and what the store holds about the last press. */
export interface SceneControlInput {
  readonly view: SceneDetailView;
  /** A verb is on its way, so no control can be pressed. */
  readonly acting: boolean;
  /** The last verb produced no answer at all. */
  readonly actionFailed: boolean;
  /** What the instance refused the last verb for, or null. */
  readonly actionRefusal: SceneRefusalKind | null;
  /** The last search was read back off the instance under its own command id. */
  readonly searchIsWithWhisparr: boolean;
}

/** How <code>kind</code> reads when a read of the scene answers it. */
export function sceneReadRefusal(kind: SceneRefusalKind): SceneReadRefusal {
  const sentence = SENTENCE_FOR_A_READ_REFUSAL[kind];
  return { sentence, affectedControls: sentence === null ? 0 : EVERY_CONTROL };
}

/**
 * The refusal <code>answer</code> carries, or null where it carries none this build recognises.
 *
 * An answer with no such member is a live path rather than a guarded-against one: the POST helper
 * resolves an empty object for an empty 2xx body and for an unparseable one.
 */
export function sceneVerbRefusalIn(answer: unknown): SceneRefusalKind | null {
  const value = memberOf(answer, "refusal");
  return typeof value === "string" && Object.hasOwn(SENTENCE_FOR_A_VERB_REFUSAL, value)
    ? (value as SceneRefusalKind)
    : null;
}

/** Whether <code>answer</code> reports that the instance holds the search it was asked for. */
export function searchIsWithWhisparrIn(answer: unknown): boolean {
  return memberOf(answer, "searchIsWithWhisparr") === true;
}

/** The route segment <code>verb</code> is carried out at. */
export function routeSegmentFor(verb: SceneVerb): string {
  return SCENE_VERB_ROUTES[verb];
}

/** The four controls of <code>state</code>, in the order the tab draws them. */
export function sceneControls(state: SceneControlState): readonly SceneControl[] {
  return SCENE_CONTROL_KEYS.map((key) => state[key]);
}

/**
 * What the tab draws beneath the fact block for one answered read.
 *
 * @param input the answered read, and what the store holds about the last press
 */
export function deriveSceneControls(input: SceneControlInput): SceneControlState {
  const { view } = input;
  const state = deriveState({
    excluded: view.excluded,
    present: view.present,
    monitored: view.monitored,
  });

  const refusal = input.actionRefusal;
  const place = refusal === null ? "theFactsSayIt" : PLACE_FOR_A_VERB_REFUSAL[refusal];
  const refused = refusal === null ? null : SENTENCE_FOR_A_VERB_REFUSAL[refusal];

  const accent = ACCENT_FILL_IN[state];
  const stateReason = REASON_IN[state];
  const addReason = place === "add" ? refused : stateReason.add;

  // The transient reason outranks every permanent one, and it rides all four rather than the
  // pressed one alone: all four mutate the same state the tab reads back, so a second press while
  // one is in flight is a race the reader cannot see.
  const reasonFor = (permanent: string | null) => (input.acting ? WAITING_FOR_WHISPARR : permanent);

  const control = (
    key: SceneControlKey,
    label: string,
    states: string,
    verb: SceneVerb,
    permanent: string | null,
  ): SceneControl => ({
    key,
    label,
    states,
    variant: accent === key ? "primary" : "ghost",
    reason: reasonFor(permanent),
    verb,
  });

  const monitoring = state === "monitored";
  const excluded = state === "excluded";

  return {
    add: control("add", SCENE_ADD, SCENE_ADD_STATES, "add", addReason),
    monitor: monitoring
      ? control(
          "monitor",
          STOP_MONITORING_IN_WHISPARR,
          SCENE_STOP_MONITORING_STATES,
          "unmonitor",
          stateReason.monitor,
        )
      : control(
          "monitor",
          MONITOR_IN_WHISPARR,
          SCENE_MONITOR_STATES,
          "monitor",
          stateReason.monitor,
        ),
    search: control(
      "search",
      SCENE_SEARCH,
      SCENE_SEARCH_STATES,
      "search",
      place === "search" ? refused : stateReason.search,
    ),
    // One control with two labels, so the tab never shows both and a mistake is fixed where it was
    // made.
    exclude: excluded
      ? control(
          "exclude",
          SCENE_REMOVE_EXCLUSION,
          SCENE_REMOVE_EXCLUSION_STATES,
          "removeExclusion",
          stateReason.exclude,
        )
      : control("exclude", SCENE_EXCLUDE, SCENE_EXCLUDE_STATES, "exclude", stateReason.exclude),
    sharedReason: place === "sharedNotice" ? refused : null,
    affectedControls: place === "sharedNotice" ? EVERY_CONTROL : 0,
    statusLine: statusLine(input, place, refused),
    state,
  };
}

/**
 * The one sentence beneath the controls after the last press, or null where there is none.
 *
 * A failure reads ahead of a refusal: a verb that never arrived cannot also have been declined. A
 * confirmed search reads last, because it is the only outcome any verb states in words.
 */
function statusLine(
  input: SceneControlInput,
  place: VerbRefusalPlace,
  refused: string | null,
): SceneStatusLine | null {
  if (input.actionFailed) return { sentence: ACTION_DID_NOT_REACH_WHISPARR, failed: true };
  if (place === "statusLine" && refused !== null) return { sentence: refused, failed: true };
  return input.searchIsWithWhisparr
    ? { sentence: SCENE_SEARCH_IS_WITH_WHISPARR, failed: false }
    : null;
}

function memberOf(answer: unknown, member: string): unknown {
  return answer !== null && typeof answer === "object"
    ? (answer as Record<string, unknown>)[member]
    : null;
}
