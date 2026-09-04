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
  AddAllMissingEnqueued,
  EntityMonitoringView,
  MonitorRefusalKind,
  MonitorScope,
  ReflectOwnedEnqueued,
  ReflectOwnedSkipReason,
  WhisparrCapability,
  WhisparrEntityKind,
  WhisparrGeneration,
} from "../wire/api";
import {
  ACTION_ADD_ALL_MISSING,
  ACTION_DID_NOT_REACH_WHISPARR,
  ACTION_REFLECT_OWNED,
  ACTION_SEARCH_ALL_MONITORED,
  ADD_ALL_MISSING,
  ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE,
  ALL_SCENES_MARKS_THE_BACK_CATALOGUE,
  CAP_UNAVAILABLE_ON_THIS_GENERATION,
  INSTANCE_ANSWER_WAS_TOO_LARGE_TO_READ,
  INSTANCE_DID_NOT_REPORT_THE_CHANGE,
  INSTANCE_HOLDS_NO_SUCH_ENTRY,
  INSTANCE_OFFERS_NO_QUALITY_PROFILE,
  INSTANCE_OFFERS_NO_ROOT_FOLDER,
  INSTANCE_REFUSED,
  MONITOR_IN_WHISPARR,
  NO_IDENTITY_IN_THIS_NAMESPACE,
  NO_INSTANCE_CONNECTED,
  PERFORMER_HAS_NO_FUTURE_ONLY_SCOPE,
  REFLECT_OWNED,
  REFLECT_OWNED_SKIPPED,
  REFLECT_OWNED_SKIPPED_SETTING_UNREADABLE,
  SCOPE_ALL_SCENES,
  SCOPE_DOES_NOT_LIMIT_WHAT_IS_MONITORED,
  SCOPE_FUTURE_SCENES,
  SCOPE_IN_FORCE_IS_NOT_REPORTED,
  SEARCH_ALL_MONITORED,
  SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
  STOP_MONITORING_IN_WHISPARR,
  UNMONITORING_DOES_NOT_RETRACT,
  WAITING_FOR_WHISPARR,
  WHISPARR_MAY_RENAME,
} from "../common/ui/copy";

/** A scope a caller can actually choose. The wire type admits null, which is "take the default". */
export type MonitorScopeChoice = NonNullable<MonitorScope>;

/** A generation something can be connected to. The wire type admits null, which is "none". */
export type ConnectedGeneration = NonNullable<WhisparrGeneration>;

/** A reason the server can name. The wire type admits null, which is "nothing was skipped". */
export type ReflectOwnedSkip = NonNullable<ReflectOwnedSkipReason>;

/** The three items that appear only once the entity is monitored. */
export type SecondaryAction = "addAllMissing" | "reflectOwned" | "searchAllMonitored";

/**
 * What every row carries, however it is pressed.
 *
 * The reason is the only representation of whether the row can be pressed: a reason disables it and
 * an absent reason enables it. There is deliberately no second boolean, so a row that is dimmed with
 * nothing to hear cannot be expressed.
 */
interface MenuItemFace {
  readonly label: string;
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
  /**
   * The one sentence to state beneath the scope rows, or null when there is nothing to say.
   *
   * Set only where a scope IS in force and the read did not report which, so a reader looking at an
   * unmarked pair is told why none is marked rather than left to read it as a fault.
   */
  readonly scopeNote: string | null;
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
  answerTooLargeToRead: {
    sentence: INSTANCE_ANSWER_WAS_TOO_LARGE_TO_READ,
    leavesNothingToOffer: false,
  },
  instanceHoldsNoSuchEntity: {
    sentence: INSTANCE_HOLDS_NO_SUCH_ENTRY,
    leavesNothingToOffer: false,
  },
  instanceDidNotReportTheChange: {
    sentence: INSTANCE_DID_NOT_REPORT_THE_CHANGE,
    leavesNothingToOffer: false,
  },
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

/**
 * What each secondary action states beneath itself, in the order it reads.
 *
 * Total by TYPE, so a secondary action added later fails this build rather than rendering a row with
 * nothing stated under it. Reflect owned states two: what it does, and the consequence of Whisparr
 * taking charge of the files it links.
 */
const SECONDARY_SENTENCES: Record<SecondaryAction, readonly string[]> = {
  addAllMissing: [ADD_ALL_MISSING],
  reflectOwned: [REFLECT_OWNED, WHISPARR_MAY_RENAME],
  searchAllMonitored: [SEARCH_ALL_MONITORED],
};

/** Turning monitoring on, at a chosen scope. Only reached for an entity not yet monitored. */
const MONITOR_ROUTE = "monitor";

/** Turning monitoring off. */
const UNMONITOR_ROUTE = "unmonitor";

/** Changing the scope of something already monitored, which is not the same verb as monitoring. */
const SCOPE_ROUTE = "scope";

/** Registering the scenes the library holds that the instance's own catalogue does not. */
const ADD_ALL_MISSING_ROUTE = "add-all-missing";

/** Linking the files the library already holds into place on the instance. */
const REFLECT_OWNED_ROUTE = "reflect-owned";

/** The one verb here that makes the instance go and download. Its own row and its own route. */
const SEARCH_ALL_MONITORED_ROUTE = "search-all-monitored";

/**
 * Which answer each acting route serves, named as the component the emitted wire document declares
 * for that route's 200 response.
 *
 * Four of the six answer the entity's own state and two answer an enqueued job, so one stand-in type
 * for all six typechecks only while every answer happens to spell one member the same way. Every key
 * is the declared route constant rather than its text, and <code>monitorRoutes.test.ts</code> reads
 * these values against the document itself.
 */
export const MONITOR_ACTION_ANSWER_SCHEMAS = {
  [MONITOR_ROUTE]: "EntityMonitoringView",
  [UNMONITOR_ROUTE]: "EntityMonitoringView",
  [SCOPE_ROUTE]: "EntityMonitoringView",
  [SEARCH_ALL_MONITORED_ROUTE]: "EntityMonitoringView",
  [REFLECT_OWNED_ROUTE]: "ReflectOwnedEnqueued",
  [ADD_ALL_MISSING_ROUTE]: "AddAllMissingEnqueued",
} as const;

/** One route an action is carried out at. */
export type MonitorActionRoute = keyof typeof MONITOR_ACTION_ANSWER_SCHEMAS;

/** Whatever one of those routes can answer. */
export type MonitorActionAnswer =
  EntityMonitoringView | ReflectOwnedEnqueued | AddAllMissingEnqueued;

/**
 * Which route each secondary action is served at, or null where this build serves none.
 *
 * The ONE place either surface learns whether a verb is reachable. The entity menu renders a row
 * disabled when the answer is null and the selection overlay does not offer it at all, so the two
 * cannot come to disagree about which verbs this build carries out.
 *
 * Total by TYPE, so a secondary action added later has to be classified here. Every non-null value
 * names one of the route constants above rather than repeating its text, and a pin reads the two
 * against each other.
 */
const SECONDARY_ACTION_ROUTES: Record<SecondaryAction, MonitorActionRoute | null> = {
  addAllMissing: ADD_ALL_MISSING_ROUTE,
  reflectOwned: REFLECT_OWNED_ROUTE,
  searchAllMonitored: SEARCH_ALL_MONITORED_ROUTE,
};

/**
 * What each skip reason states at the control.
 *
 * Total by TYPE, so a reason added to the wire enum fails this build rather than rendering a
 * sentence about a setting nobody read.
 */
const REFLECT_OWNED_SKIP_SENTENCE: Record<ReflectOwnedSkip, string> = {
  hardLinksOff: REFLECT_OWNED_SKIPPED,
  hardLinkSettingUnreadable: REFLECT_OWNED_SKIPPED_SETTING_UNREADABLE,
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
  "answerTooLargeToRead",
  "instanceHoldsNoSuchEntity",
  "instanceDidNotReportTheChange",
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

/** What <code>reason</code> states at the control when reflect owned linked nothing. */
export function describeReflectOwnedSkip(reason: ReflectOwnedSkip): string {
  return REFLECT_OWNED_SKIP_SENTENCE[reason];
}

function memberOf(answer: unknown, member: string): unknown {
  return answer !== null && typeof answer === "object"
    ? (answer as Record<string, unknown>)[member]
    : null;
}

/**
 * The refusal <code>answer</code> carries, or null where it carries none this build recognises.
 *
 * An answer with no such member is a live path rather than a guarded-against one: the POST helper
 * resolves an empty object for an empty 2xx body and for an unparseable one. Validated against the
 * key set of the sentence record, which is total by TYPE, so a kind added to the wire enum is
 * accepted here and still forces a sentence decision in that record.
 */
export function monitorRefusalIn(answer: unknown): MonitorRefusalKind | null {
  const value = memberOf(answer, "refusal");
  return typeof value === "string" && Object.hasOwn(REFUSALS, value)
    ? (value as MonitorRefusalKind)
    : null;
}

/** The skip reason <code>answer</code> carries, or null where it carries none this build recognises. */
export function reflectOwnedSkipIn(answer: unknown): ReflectOwnedSkip | null {
  const value = memberOf(answer, "skipped");
  return typeof value === "string" && Object.hasOwn(REFLECT_OWNED_SKIP_SENTENCE, value)
    ? (value as ReflectOwnedSkip)
    : null;
}

/**
 * What <code>kind</code> states beneath the control, or null where it states nothing there.
 *
 * The same flag decides both surfaces. A refusal that leaves nothing to offer speaks in the
 * control's own name and is silent here, so the two cannot come to disagree about which reason a
 * reader is given where.
 */
export function refusalNoticeFor(kind: MonitorRefusalKind): string | null {
  const refusal = describeMonitorRefusal(kind);
  return refusal.leavesNothingToOffer ? null : refusal.sentence;
}

/**
 * The one sentence beneath the control after the last gesture, or null where there is none.
 *
 * The precedence is over the NOTICES rather than over the inputs, which is what keeps a healthy
 * answer from silencing a skip: <code>none</code> is a refusal kind and is what every healthy read
 * answers, so a branch that stopped at a non-null refusal would answer null for the common case and
 * never reach the skip. Falling through on a null notice also covers the kinds that empty the menu,
 * with no second list of which those are.
 *
 * A failure reads ahead of both: an action that never arrived cannot also have been refused or
 * passed over.
 */
export function controlNotice({
  failed,
  refusal,
  skip,
}: {
  failed: boolean;
  refusal: MonitorRefusalKind | null;
  skip: ReflectOwnedSkip | null;
}): string | null {
  if (failed) return ACTION_DID_NOT_REACH_WHISPARR;
  const refused = refusal === null ? null : refusalNoticeFor(refusal);
  if (refused !== null) return refused;
  return skip === null ? null : describeReflectOwnedSkip(skip);
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
    return { available, reason, scopeNote: null, items: [] };
  }

  const transient = inFlight ? WAITING_FOR_WHISPARR : null;
  const face = (label: string, sentences: readonly string[], unavailable: string | null) => ({
    label,
    // A permanent reason reads ahead of the transient one: a control that will never work should not
    // say it is waiting.
    reason: unavailable ?? transient,
    sentences,
  });

  // The same two rows mean two different things. On an entity nothing monitors they are the monitor
  // gesture, and the mark is the choice this menu will carry out, which defaults to the cheaper
  // scope. On a monitored entity they are a report of what Whisparr holds, and a mark is a claim
  // about the instance: an answer that named no scope must leave every row unmarked.
  const offersAScopePair = OFFERS_A_SCOPE_PAIR[view.kind];
  const scopes: MonitorMenuItem[] = offersAScopePair
    ? SCOPE_ORDER.map((scope) => ({
        ...face(SCOPE_LABEL[scope], scopeSentences(scope, view.generation), null),
        item: "scope" as const,
        scope,
        selected: view.monitored ? scope === view.scope : scope === DEFAULT_SCOPE,
      }))
    : [];

  const scopeNote =
    offersAScopePair && view.monitored && view.scope === null
      ? SCOPE_IN_FORCE_IS_NOT_REPORTED
      : null;

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
    return { available, reason, scopeNote, items: [...scopes, ...monitorItem] };
  }

  const unmonitor: MonitorMenuItem = {
    ...face(STOP_MONITORING_IN_WHISPARR, [UNMONITORING_DOES_NOT_RETRACT], null),
    item: "unmonitor",
  };

  const secondary: MonitorMenuItem[] = SECONDARY_ACTIONS.map((action) => ({
    ...face(
      SECONDARY_LABEL[action],
      SECONDARY_SENTENCES[action],
      held.has(capabilityBehindAction(action)) ? null : CAP_UNAVAILABLE_ON_THIS_GENERATION,
    ),
    item: "secondary" as const,
    action,
  }));

  return { available, reason, scopeNote, items: [...scopes, unmonitor, ...secondary] };
}

/**
 * The route <code>item</code> is carried out at, or null where this build serves none.
 *
 * A scope row is two different verbs depending on the state: on an entity not yet monitored it is
 * the monitor gesture carrying that scope, and on one already monitored it changes the scope and
 * leaves the flag alone.
 *
 * <code>monitorRoutes.test.ts</code> reads the route constants above against the routes the shipped
 * wire document declares, so a verb mounted later cannot be left out here in silence.
 *
 * @param item the menu item pressed
 * @param monitored whether the connected instance already monitors the entity
 */
export function routeFor(item: MonitorMenuItem, monitored: boolean): MonitorActionRoute | null {
  switch (item.item) {
    case "monitor":
      return MONITOR_ROUTE;
    case "unmonitor":
      return UNMONITOR_ROUTE;
    case "scope":
      return monitored ? SCOPE_ROUTE : MONITOR_ROUTE;
    default:
      return SECONDARY_ACTION_ROUTES[item.action];
  }
}

/** A stable key for one item, so two secondary actions are not the same row. */
export function monitorMenuItemKey(item: MonitorMenuItem): string {
  switch (item.item) {
    case "scope":
      return `scope:${item.scope}`;
    case "secondary":
      return `secondary:${item.action}`;
    default:
      return item.item;
  }
}

/** The verbs the bulk route carries, which are a subset of the entity routes. */
export type BulkVerb = "monitor" | "unmonitor";

/** One action the selection overlay offers, already decided. */
export interface BulkMonitorAction {
  readonly key: string;
  readonly label: string;
  /** What is stated beneath it, in the order it reads. */
  readonly sentences: readonly string[];
  readonly verb: BulkVerb;
  /** The scope the request carries, or null where the verb expresses none. */
  readonly scope: MonitorScopeChoice | null;
}

/** What a selection of one entity kind can be offered against one connection. */
export interface BulkMonitorOffer {
  readonly actions: readonly BulkMonitorAction[];
  /** The one sentence to state when nothing can be offered, or null when something can. */
  readonly reason: string | null;
}

/**
 * What the selection bar offers for a selection of <code>view</code>'s kind.
 *
 * Derived from {@link monitorMenu} and {@link routeFor}, so the overlay offers exactly the verbs the
 * entity menu can carry out: whatever that menu renders disabled is not offered here at all.
 *
 * Only what is true of the CONNECTION decides the offer. A refusal the sampled entity earned - no
 * link, or several conflicting ones - is a fact about that one entity, and a selection can hold a
 * hundred others it is not true of, so it is deliberately not read here. Nothing connected is the
 * exception, because that one is about the connection.
 *
 * The entity's own monitored state is not read either, for the same reason: a selection can mix
 * monitored and unmonitored entities, and each entity's own state is settled per entity on the
 * server.
 *
 * @param view what a read of one selected entity answered
 */
export function bulkMonitorActions(view: EntityMonitoringView): BulkMonitorOffer {
  if (view.refusal === "notConfigured") {
    return { actions: [], reason: NO_INSTANCE_CONNECTED };
  }

  const connection: EntityMonitoringView = { ...view, refusal: "none" };
  const notYetMonitored = monitorMenu({ ...connection, monitored: false }, false);
  if (!notYetMonitored.available) {
    return { actions: [], reason: notYetMonitored.reason };
  }

  const alreadyMonitored = monitorMenu({ ...connection, monitored: true }, false);

  return {
    actions: [
      ...offered(notYetMonitored.items, false),
      // The scope rows of a monitored entity are the scope-change verb, which the bulk route does not
      // carry: this offers the gestures D-14 names and not a third one.
      ...offered(
        alreadyMonitored.items.filter((item) => item.item !== "scope"),
        true,
      ),
    ],
    reason: null,
  };
}

function offered(
  items: readonly MonitorMenuItem[],
  monitored: boolean,
): readonly BulkMonitorAction[] {
  return items.flatMap((item) => {
    if (item.reason !== null) return [];
    const route = routeFor(item, monitored);
    const verb = route === null ? null : bulkVerbFor(route);
    if (verb === null) return [];
    return [
      {
        key: monitorMenuItemKey(item),
        label: item.label,
        sentences: item.sentences,
        verb,
        scope: item.item === "scope" ? item.scope : null,
      },
    ];
  });
}

/** Which bulk verb one entity route is carried out as, or null where the bulk route carries none. */
function bulkVerbFor(route: string): BulkVerb | null {
  if (route === MONITOR_ROUTE) return "monitor";
  if (route === UNMONITOR_ROUTE) return "unmonitor";
  return null;
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
