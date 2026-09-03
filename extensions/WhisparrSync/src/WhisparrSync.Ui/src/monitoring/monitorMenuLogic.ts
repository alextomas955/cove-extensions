/**
 * Pure rules for the entity monitor menu: which items exist at all, which of them can be pressed,
 * and what each one states beneath itself.
 *
 * Relative imports only, so this module runs with no environment and needs no doubles. The wire types
 * arrive as `import type`, which erases at runtime and so takes nothing with it.
 *
 * Nothing here is decided from a table of what a generation can do. The view carries the capabilities
 * the connected instance holds, and that list is the authority, so a capability registered later
 * needs no edit here.
 */
import type {
  EntityMonitoringView,
  MonitorRefusalKind,
  MonitorScope,
  WhisparrCapability,
  WhisparrEntityKind,
  WhisparrGeneration,
} from "../wire/api";
import {
  ACTION_ADD_ALL_MISSING,
  ACTION_REFLECT_OWNED,
  ACTION_SEARCH_ALL_MONITORED,
  ADD_ALL_MISSING,
  ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE,
  ALL_SCENES_MARKS_THE_BACK_CATALOGUE,
  CAP_UNAVAILABLE_ON_THIS_GENERATION,
  INSTANCE_OFFERS_NO_QUALITY_PROFILE,
  INSTANCE_OFFERS_NO_ROOT_FOLDER,
  INSTANCE_REFUSED,
  MONITOR_IN_WHISPARR,
  NO_IDENTITY_IN_THIS_NAMESPACE,
  NO_INSTANCE_CONNECTED,
  PERFORMER_HAS_NO_FUTURE_ONLY_SCOPE,
  REFLECT_OWNED,
  SCOPE_ALL_SCENES,
  SCOPE_DOES_NOT_LIMIT_WHAT_IS_MONITORED,
  SCOPE_FUTURE_SCENES,
  SEARCH_ALL_MONITORED,
  SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
  STOP_MONITORING_IN_WHISPARR,
  UNMONITORING_DOES_NOT_RETRACT,
  WAITING_FOR_WHISPARR,
} from "../common/ui/copy";

/** A scope a caller can actually choose. The wire type admits null, which is "take the default". */
export type MonitorScopeChoice = NonNullable<MonitorScope>;

/** A generation something can be connected to. The wire type admits null, which is "none". */
export type ConnectedGeneration = NonNullable<WhisparrGeneration>;

/** The three items that appear only once the entity is monitored. */
export type SecondaryAction = "addAllMissing" | "reflectOwned" | "searchAllMonitored";

interface MenuItemFace {
  readonly label: string;
  readonly enabled: boolean;
  /** The one sentence saying why it cannot be pressed, or null when it can. */
  readonly reason: string | null;
  /** What is stated beneath it, in the order it reads. */
  readonly sentences: readonly string[];
}

/** One item the menu offers. */
export type MonitorMenuItem =
  | (MenuItemFace & {
      readonly item: "scope";
      readonly scope: MonitorScopeChoice;
      readonly selected: boolean;
    })
  | (MenuItemFace & { readonly item: "monitor" })
  | (MenuItemFace & { readonly item: "unmonitor" })
  | (MenuItemFace & { readonly item: "secondary"; readonly action: SecondaryAction });

/** What the control offers for one entity. */
export interface MonitorMenu {
  /** Whether the control can be opened at all. */
  readonly available: boolean;
  /** The one sentence to state at the control, or null when there is nothing to say. */
  readonly reason: string | null;
  readonly items: readonly MonitorMenuItem[];
}

/** How one refusal kind reads, and whether it leaves anything to offer. */
export interface MonitorRefusal {
  /** The specified sentence, or null where nothing was refused. */
  readonly sentence: string | null;
  /**
   * Whether the entity cannot be monitored here at all. A refusal that was one attempt failing
   * leaves the control open, because emptying the menu would take away the retry.
   */
  readonly leavesNothingToOffer: boolean;
}

/**
 * Every refusal kind the server can answer.
 *
 * Total by TYPE, so a kind added to the wire enum fails this build rather than compiling with no
 * decision made about it. Exactly one sentence per kind and never two combined: the server has
 * already chosen which reason the user reads by answering one kind.
 */
const REFUSALS: Record<MonitorRefusalKind, MonitorRefusal> = {
  none: { sentence: null, leavesNothingToOffer: false },
  notConfigured: { sentence: NO_INSTANCE_CONNECTED, leavesNothingToOffer: true },
  noIdentityInThisNamespace: {
    sentence: NO_IDENTITY_IN_THIS_NAMESPACE,
    leavesNothingToOffer: true,
  },
  severalIdentitiesInThisNamespace: {
    sentence: SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
    leavesNothingToOffer: true,
  },
  capabilityAbsentOnThisGeneration: {
    sentence: CAP_UNAVAILABLE_ON_THIS_GENERATION,
    leavesNothingToOffer: true,
  },
  noQualityProfile: { sentence: INSTANCE_OFFERS_NO_QUALITY_PROFILE, leavesNothingToOffer: false },
  noRootFolder: { sentence: INSTANCE_OFFERS_NO_ROOT_FOLDER, leavesNothingToOffer: false },
  instanceRefused: { sentence: INSTANCE_REFUSED, leavesNothingToOffer: false },
};

/**
 * Which capability monitoring each kind of entity needs.
 *
 * Total by TYPE, so an entity kind added to the wire enum fails this build rather than rendering a
 * menu with no decision made about it.
 */
const MONITOR_CAPABILITY: Record<WhisparrEntityKind, WhisparrCapability> = {
  studio: "monitorStudio",
  performer: "monitorPerformer",
};

/**
 * Which kinds express a narrower scope, and so are offered the pair rather than one plain toggle.
 *
 * Total by TYPE. A performer expresses no date gate on either generation, so monitoring one is
 * All-Scenes behaviour and a pair would offer a choice that does not exist.
 */
const OFFERS_A_SCOPE_PAIR: Record<WhisparrEntityKind, boolean> = {
  studio: true,
  performer: false,
};

/**
 * Whether changing the scope on this generation rewrites what is already monitored.
 *
 * Total by TYPE, so a generation added to the wire enum fails this build rather than compiling with
 * no decision made about it. Where it does not, the wider scope is a one-way door and the item says
 * so.
 */
const A_SCOPE_CHANGE_IS_RETROACTIVE: Record<ConnectedGeneration, boolean> = {
  v3: false,
  v2: true,
};

/**
 * Which item of this menu each capability gates, or null where it gates none.
 *
 * Total by TYPE, so a capability added to the wire enum fails this build rather than compiling with
 * no decision made about whether this menu offers it.
 */
const ITEM_BEHIND_CAPABILITY: Record<WhisparrCapability, SecondaryAction | null> = {
  outOfBandCallbackSecret: null,
  monitorStudio: null,
  monitorPerformer: null,
  registerMissingScenes: "addAllMissing",
  reflectOwnedFiles: "reflectOwned",
  searchMonitored: "searchAllMonitored",
};

/**
 * The capability each secondary action needs.
 *
 * Transcribed rather than derived from {@link ITEM_BEHIND_CAPABILITY}, which is the other direction
 * of the same fact. A derived pair agrees with itself; two written-down ones disagree out loud, and
 * a test reads them against each other.
 */
const CAPABILITY_BEHIND_ITEM: Record<SecondaryAction, WhisparrCapability> = {
  addAllMissing: "registerMissingScenes",
  reflectOwned: "reflectOwnedFiles",
  searchAllMonitored: "searchMonitored",
};

/** What each secondary action is called. */
const SECONDARY_LABEL: Record<SecondaryAction, string> = {
  addAllMissing: ACTION_ADD_ALL_MISSING,
  reflectOwned: ACTION_REFLECT_OWNED,
  searchAllMonitored: ACTION_SEARCH_ALL_MONITORED,
};

/** What each secondary action states beneath itself. */
const SECONDARY_SENTENCE: Record<SecondaryAction, string> = {
  addAllMissing: ADD_ALL_MISSING,
  reflectOwned: REFLECT_OWNED,
  searchAllMonitored: SEARCH_ALL_MONITORED,
};

/** What each scope is called, in the instance's own words. */
const SCOPE_LABEL: Record<MonitorScopeChoice, string> = {
  futureScenes: SCOPE_FUTURE_SCENES,
  allScenes: SCOPE_ALL_SCENES,
};

/**
 * The refusal kinds, so a caller covering all of them cannot miss one.
 *
 * The spellings are transcribed by hand from the server's enum. A list computed from the generated
 * module would agree with it whatever it says.
 */
export const MONITOR_REFUSAL_KINDS: readonly MonitorRefusalKind[] = [
  "none",
  "notConfigured",
  "noIdentityInThisNamespace",
  "severalIdentitiesInThisNamespace",
  "capabilityAbsentOnThisGeneration",
  "noQualityProfile",
  "noRootFolder",
  "instanceRefused",
];

/**
 * The scopes, in the order they render.
 *
 * The order is fixed rather than derived from the state, because an order that varies puts the cheap
 * option under the cursor sometimes and the expensive one others.
 */
export const SCOPE_ORDER: readonly MonitorScopeChoice[] = ["futureScenes", "allScenes"];

/** The scope taken when the reader takes none. */
export const DEFAULT_SCOPE: MonitorScopeChoice = "futureScenes";

/** The secondary actions, in the order they render. */
export const SECONDARY_ACTIONS: readonly SecondaryAction[] = [
  "addAllMissing",
  "reflectOwned",
  "searchAllMonitored",
];

/** The entity kinds, so a caller covering both cannot miss one. */
export const ENTITY_KINDS: readonly WhisparrEntityKind[] = ["studio", "performer"];

/** The generations something can be connected to. */
export const GENERATIONS: readonly ConnectedGeneration[] = ["v3", "v2"];

/** Every capability the wire enum carries. */
export const CAPABILITY_ORDER: readonly WhisparrCapability[] = [
  "outOfBandCallbackSecret",
  "monitorStudio",
  "monitorPerformer",
  "registerMissingScenes",
  "reflectOwnedFiles",
  "searchMonitored",
];

/** How <code>kind</code> reads, and whether it leaves anything to offer. */
export function describeMonitorRefusal(kind: MonitorRefusalKind): MonitorRefusal {
  return REFUSALS[kind];
}

/** Which capability <code>action</code> needs the connected generation to hold. */
export function capabilityBehindAction(action: SecondaryAction): WhisparrCapability {
  return CAPABILITY_BEHIND_ITEM[action];
}

/** Which item of this menu <code>capability</code> gates, or null where it gates none. */
export function actionBehindCapability(capability: WhisparrCapability): SecondaryAction | null {
  return ITEM_BEHIND_CAPABILITY[capability];
}

/**
 * The menu for one entity.
 *
 * @param view what the entity's own mount read answered
 * @param inFlight whether a monitor action for this entity is still on its way, which disables every
 * item so two cannot be in flight for one entity
 */
export function monitorMenu(view: EntityMonitoringView, inFlight: boolean): MonitorMenu {
  const refusal = describeMonitorRefusal(view.refusal);
  const held = new Set(view.capabilities);
  const cannotMonitorThisKind = !held.has(MONITOR_CAPABILITY[view.kind]);

  // One sentence, and the server's kind chooses it: it has already decided which reason the user
  // reads. The held list answers only where the server named nothing.
  const reason =
    refusal.sentence ?? (cannotMonitorThisKind ? CAP_UNAVAILABLE_ON_THIS_GENERATION : null);
  const available = !refusal.leavesNothingToOffer && !cannotMonitorThisKind;
  if (!available) {
    return { available, reason, items: [] };
  }

  const transient = inFlight ? WAITING_FOR_WHISPARR : null;
  const face = (label: string, sentences: readonly string[], unavailable: string | null) => ({
    label,
    // A permanent reason reads ahead of the transient one: a control that will never work should not
    // say it is waiting.
    reason: unavailable ?? transient,
    enabled: unavailable === null && !inFlight,
    sentences,
  });

  const scopes: MonitorMenuItem[] = OFFERS_A_SCOPE_PAIR[view.kind]
    ? SCOPE_ORDER.map((scope) => ({
        ...face(SCOPE_LABEL[scope], scopeSentences(scope, view.generation), null),
        item: "scope" as const,
        scope,
        selected: scope === DEFAULT_SCOPE,
      }))
    : [];

  const monitorItem: MonitorMenuItem[] =
    scopes.length === 0 && !view.monitored
      ? [
          {
            ...face(
              MONITOR_IN_WHISPARR,
              [PERFORMER_HAS_NO_FUTURE_ONLY_SCOPE, ALL_SCENES_MARKS_THE_BACK_CATALOGUE],
              null,
            ),
            item: "monitor" as const,
          },
        ]
      : [];

  if (!view.monitored) {
    return { available, reason, items: [...scopes, ...monitorItem] };
  }

  const unmonitor: MonitorMenuItem = {
    ...face(STOP_MONITORING_IN_WHISPARR, [UNMONITORING_DOES_NOT_RETRACT], null),
    item: "unmonitor",
  };

  const secondary: MonitorMenuItem[] = SECONDARY_ACTIONS.map((action) => ({
    ...face(
      SECONDARY_LABEL[action],
      [SECONDARY_SENTENCE[action]],
      held.has(capabilityBehindAction(action)) ? null : CAP_UNAVAILABLE_ON_THIS_GENERATION,
    ),
    item: "secondary" as const,
    action,
  }));

  return { available, reason, items: [...scopes, unmonitor, ...secondary] };
}

function scopeSentences(
  scope: MonitorScopeChoice,
  generation: WhisparrGeneration,
): readonly string[] {
  if (scope !== "allScenes") {
    return [SCOPE_DOES_NOT_LIMIT_WHAT_IS_MONITORED];
  }
  const oneWayDoor =
    generation !== null && !A_SCOPE_CHANGE_IS_RETROACTIVE[generation]
      ? [ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE]
      : [];
  return [
    SCOPE_DOES_NOT_LIMIT_WHAT_IS_MONITORED,
    ALL_SCENES_MARKS_THE_BACK_CATALOGUE,
    ...oneWayDoor,
  ];
}
