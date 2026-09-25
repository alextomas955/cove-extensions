/**
 * Pure rules for the scene tab's four controls.
 *
 * A reason disables a control and a null reason enables it, so a disabled control with no stated
 * reason is unrepresentable. At most one control takes the primary variant for any input.
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
  NO_AGREED_ROOT_FOR_THIS_ENTITY,
  NO_IDENTITY_IN_THIS_NAMESPACE,
  NO_INSTANCE_CONNECTED,
  SCENE_ADD,
  SCENE_EXCLUDE,
  SCENE_IS_ALREADY_IN_WHISPARR,
  SCENE_IS_ON_THE_EXCLUSION_LIST,
  SCENE_MONITOR_NEEDS_AN_ENTRY,
  SCENE_REMOVE_EXCLUSION,
  SCENE_SEARCH,
  SCENE_SEARCH_IS_WITH_WHISPARR,
  SCENE_SEARCH_NEEDS_AN_ENTRY,
  SCENE_SEARCH_NEEDS_MONITORING,
  SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
  STOP_MONITORING_IN_WHISPARR,
  WAITING_FOR_WHISPARR,
  WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  WHISPARR_STATUS_COULD_NOT_BE_READ,
} from "../common/ui/copy";

export type SceneVerb = "add" | "monitor" | "unmonitor" | "search" | "exclude" | "removeExclusion";

const SCENE_VERB_ROUTES: Record<SceneVerb, string> = {
  add: "add",
  monitor: "monitor",
  unmonitor: "unmonitor",
  search: "search",
  exclude: "exclude",
  removeExclusion: "remove-exclusion",
};

/** Two verbs share the exclusion control. */
export type SceneControlKey = "add" | "monitor" | "search" | "exclude";

/** In the order the tab draws them. */
export const SCENE_CONTROL_KEYS: readonly SceneControlKey[] = [
  "add",
  "monitor",
  "search",
  "exclude",
];

export interface SceneControl {
  readonly key: SceneControlKey;
  /** Always a fixed constant; no instance-supplied text enters it. */
  readonly label: string;
  readonly variant: "primary" | "ghost";
  /** Why it cannot be pressed, or null when it can. */
  readonly reason: string | null;
  readonly verb: SceneVerb;
}

export interface SceneControlState {
  readonly add: SceneControl;
  readonly monitor: SceneControl;
  readonly search: SceneControl;
  readonly exclude: SceneControl;
  /**
   * A reason two or more controls share, stated above them, or null. A reason stopping one control
   * rides that control instead.
   */
  readonly sharedReason: string | null;
  /** How many controls {@link sharedReason} stops. Zero where there is none. */
  readonly affectedControls: number;
  readonly statusLine: SceneStatusLine | null;
  readonly state: WhisparrEntityState;
}

interface SceneStatusLine {
  readonly sentence: string;
  /** Decides the tone the sentence reads in. */
  readonly failed: boolean;
}

export interface SceneReadRefusal {
  readonly sentence: string | null;
  readonly affectedControls: number;
}

// Total by type, so a member added to the wire enum fails this build. Null is a kind only a verb
// can answer, read through deriveSceneControls instead.
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
  noAgreedRootForThisEntity: null,
  whisparrHasNoEntryForScene: null,
  whisparrAlreadyHoldsThisScene: null,
  whisparrIsNotMonitoringThisScene: null,
};

type VerbRefusalPlace = "sharedNotice" | "add" | "search" | "statusLine" | "theFactsSayIt";

// Total by type. `theFactsSayIt` is a refusal naming a state: every verb is followed by a re-read,
// which puts that state on the chip and the control reasons already.
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
  noAgreedRootForThisEntity: "add",
  whisparrHasNoEntryForScene: "theFactsSayIt",
  whisparrAlreadyHoldsThisScene: "theFactsSayIt",
  whisparrIsNotMonitoringThisScene: "theFactsSayIt",
};

// Held apart from the read's own table: a read that never arrived cannot state a status, and a
// verb that never arrived changed nothing.
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
  noAgreedRootForThisEntity: NO_AGREED_ROOT_FOR_THIS_ENTITY,
  whisparrHasNoEntryForScene: null,
  whisparrAlreadyHoldsThisScene: null,
  whisparrIsNotMonitoringThisScene: null,
};

// Total by type, so a sixth state fails this build. Never search and never the excluding half: a
// search can download a file, and an accent fill would invite that press.
const ACCENT_FILL_IN: Record<WhisparrEntityState, SceneControlKey | null> = {
  notAdded: "add",
  unmonitored: "monitor",
  monitored: null,
  excluded: "exclude",
  statusUnknown: null,
};

// Null where the state does not stop the control.
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

const EVERY_CONTROL = SCENE_CONTROL_KEYS.length;

export interface SceneControlInput {
  readonly view: SceneDetailView;
  /** A verb is on its way, so no control can be pressed. */
  readonly acting: boolean;
  /** The last verb produced no answer at all. */
  readonly actionFailed: boolean;
  readonly actionRefusal: SceneRefusalKind | null;
  /** The last search was read back off the instance under its own command id. */
  readonly searchIsWithWhisparr: boolean;
}

export function sceneReadRefusal(kind: SceneRefusalKind): SceneReadRefusal {
  const sentence = SENTENCE_FOR_A_READ_REFUSAL[kind];
  return { sentence, affectedControls: sentence === null ? 0 : EVERY_CONTROL };
}

/**
 * The refusal `answer` carries, or null where it carries none this build recognises. An answer
 * with no such member is a live path: the POST helper resolves an empty object for an empty 2xx
 * body and for an unparseable one.
 */
export function sceneVerbRefusalIn(answer: unknown): SceneRefusalKind | null {
  const value = memberOf(answer, "refusal");
  return typeof value === "string" && Object.hasOwn(SENTENCE_FOR_A_VERB_REFUSAL, value)
    ? (value as SceneRefusalKind)
    : null;
}

export function searchIsWithWhisparrIn(answer: unknown): boolean {
  return memberOf(answer, "searchIsWithWhisparr") === true;
}

export function routeSegmentFor(verb: SceneVerb): string {
  return SCENE_VERB_ROUTES[verb];
}

/** The four controls, in the order the tab draws them. */
export function sceneControls(state: SceneControlState): readonly SceneControl[] {
  return SCENE_CONTROL_KEYS.map((key) => state[key]);
}

/** What the tab draws beneath the fact block for one answered read. */
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

  // The in-flight reason outranks every permanent one and disables all four, not just the pressed
  // one: all four mutate the same state the tab reads back.
  const reasonFor = (permanent: string | null) => (input.acting ? WAITING_FOR_WHISPARR : permanent);

  const control = (
    key: SceneControlKey,
    label: string,
    verb: SceneVerb,
    permanent: string | null,
  ): SceneControl => ({
    key,
    label,
    variant: accent === key ? "primary" : "ghost",
    reason: reasonFor(permanent),
    verb,
  });

  const monitoring = state === "monitored";
  const excluded = state === "excluded";

  return {
    add: control("add", SCENE_ADD, "add", addReason),
    monitor: monitoring
      ? control("monitor", STOP_MONITORING_IN_WHISPARR, "unmonitor", stateReason.monitor)
      : control("monitor", MONITOR_IN_WHISPARR, "monitor", stateReason.monitor),
    search: control(
      "search",
      SCENE_SEARCH,
      "search",
      place === "search" ? refused : stateReason.search,
    ),
    // One control with two labels, so the tab never shows both.
    exclude: excluded
      ? control("exclude", SCENE_REMOVE_EXCLUSION, "removeExclusion", stateReason.exclude)
      : control("exclude", SCENE_EXCLUDE, "exclude", stateReason.exclude),
    sharedReason: place === "sharedNotice" ? refused : null,
    affectedControls: place === "sharedNotice" ? EVERY_CONTROL : 0,
    statusLine: statusLine(input, place, refused),
    state,
  };
}

// A failure reads ahead of a refusal: a verb that never arrived cannot also have been declined.
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
