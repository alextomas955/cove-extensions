/**
 * Pure rules for the entity monitor menu: which items exist at all, which of them can be pressed,
 * and what each one is called.
 *
 * Nothing here is decided from a table of what a generation can do. The view carries the
 * capabilities the connected instance holds, so a capability registered later needs no edit here.
 *
 * Every row is labelled from the menu set rather than from the scene tab's, because the menu above
 * the rows is already headed with the product's name.
 */
import type {
  AddAllMissingEnqueued,
  EntityMonitoringView,
  MonitorBulkVerb,
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
  CAP_UNAVAILABLE_ON_THIS_GENERATION,
  INSTANCE_ANSWER_WAS_TOO_LARGE_TO_READ,
  INSTANCE_DID_NOT_REPORT_THE_CHANGE,
  INSTANCE_HOLDS_NO_SUCH_ENTRY,
  INSTANCE_OFFERS_NO_QUALITY_PROFILE,
  INSTANCE_OFFERS_NO_ROOT_FOLDER,
  INSTANCE_REFUSED,
  MENU_MONITOR,
  MENU_UNMONITOR,
  NO_AGREED_ROOT_FOR_THIS_ENTITY,
  NO_IDENTITY_IN_THIS_NAMESPACE,
  NO_INSTANCE_CONNECTED,
  REFLECT_OWNED_SKIPPED,
  REFLECT_OWNED_SKIPPED_SETTING_UNREADABLE,
  SCOPE_ALL_SCENES,
  SCOPE_FUTURE_SCENES,
  SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
  WAITING_FOR_WHISPARR,
} from "../common/ui/copy";
import { membersOf } from "../common/lib/totalTableLogic";

/** A scope a caller can actually choose. The wire type admits null, which is "take the default". */
export type MonitorScopeChoice = NonNullable<MonitorScope>;

/** A generation something can be connected to. The wire type admits null, which is "none". */
export type ConnectedGeneration = NonNullable<WhisparrGeneration>;

/** A reason the server can name. The wire type admits null, which is "nothing was skipped". */
export type ReflectOwnedSkip = NonNullable<ReflectOwnedSkipReason>;

/**
 * The items that appear only once the entity is monitored.
 *
 * Read off the values of {@link ITEM_BEHIND_CAPABILITY}, which is the one place the tie between a
 * capability and an item is written. An action added there has to be classified in every table
 * keyed by this type before the build passes.
 */
export type SecondaryAction = NonNullable<(typeof ITEM_BEHIND_CAPABILITY)[WhisparrCapability]>;

/**
 * What every row carries, however it is pressed.
 *
 * The reason is the only representation of whether the row can be pressed: a reason disables it and
 * an absent reason enables it. There is no second boolean, so a row that is dimmed with nothing to
 * hear cannot be expressed.
 */
interface MenuItemFace {
  readonly label: string;
  /** The one sentence saying why it cannot be pressed, or null when it can. */
  readonly reason: string | null;
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
 * Total by type, so a kind added to the wire enum fails this build. One sentence per kind and never
 * two combined: the server has already chosen which reason the reader gets.
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
  noAgreedRootForThisEntity: {
    sentence: NO_AGREED_ROOT_FOR_THIS_ENTITY,
    leavesNothingToOffer: false,
  },
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
 * Which capability monitoring each kind of entity needs, or null where nothing monitors one.
 *
 * Total by type, so an entity kind added to the wire enum fails this build. Null is a kind Whisparr
 * monitors through no capability, which leaves this menu nothing to offer for it.
 */
const MONITOR_CAPABILITY: Record<WhisparrEntityKind, WhisparrCapability | null> = {
  studio: "monitorStudio",
  performer: "monitorPerformer",
  tag: null,
};

/**
 * Which kinds express a narrower scope, and so are offered the pair rather than one plain toggle.
 *
 * Total by type. A performer expresses no date gate on either generation, so monitoring one is
 * All-Scenes behaviour and a pair would offer a choice that does not exist.
 */
const OFFERS_A_SCOPE_PAIR: Record<WhisparrEntityKind, boolean> = {
  studio: true,
  performer: false,
  tag: false,
};

/**
 * The generations something can be connected to.
 *
 * Total by type, so a generation added to the wire enum fails this build. It carries no values:
 * nothing in this menu is decided from which generation is connected, and the member set is all
 * its readers want.
 */
const CONNECTED_GENERATIONS: Record<ConnectedGeneration, null> = {
  v3: null,
  v2: null,
};

/**
 * Which item of this menu each capability gates, or null where it gates none.
 *
 * Total by type, so a capability added to the wire enum fails this build. It is also the one
 * declaration of the tie: {@link SecondaryAction} is read off these values and the reverse lookup
 * is derived, so no second copy can disagree with it. Two capabilities naming one action would make
 * that reverse ambiguous, which is why each action is named once.
 */
const ITEM_BEHIND_CAPABILITY = {
  outOfBandCallbackSecret: null,
  monitorStudio: null,
  monitorPerformer: null,
  registerMissingScenes: "addAllMissing",
  reflectOwnedFiles: "reflectOwned",
  searchMonitored: "searchAllMonitored",
  // Reads, so this menu offers nothing for them.
  readEntityCardsInBatch: null,
  readSceneCardsInBatch: null,
  readEntityCatalogue: null,

  readSceneStatus: null,
  readSceneExclusions: null,

  // The catalogue tab offers this add beside the reason it clears, so the menu carries none.
  trackEntityCatalogue: null,
  // Offered on a catalogue card rather than in this menu.
  searchScene: null,
  // Offered on one scene's own surface rather than in this menu.
  monitorScene: null,
  excludeScene: null,
  // Reached from the library sync section on the settings page, which acts on the whole library
  // rather than on the studio this menu is opened from.
  registerOwnedSites: null,
  // A read, so this menu offers nothing for it.
  readSiteSceneRows: null,
  readHeldSites: null,
  readInstanceFilesystem: null,
} as const satisfies Record<WhisparrCapability, string | null>;

/**
 * The capability each secondary action needs, the other direction of {@link ITEM_BEHIND_CAPABILITY}.
 *
 * Derived, so it cannot say anything the table does not. Every member of {@link SecondaryAction}
 * comes from a row of that table, so every member has an entry here.
 */
const CAPABILITY_BEHIND_ITEM = Object.fromEntries(
  Object.entries(ITEM_BEHIND_CAPABILITY)
    .filter(([, action]) => action !== null)
    .map(([capability, action]) => [action, capability]),
) as Record<SecondaryAction, WhisparrCapability>;

/** What each secondary action is called. */
const SECONDARY_LABEL: Record<SecondaryAction, string> = {
  addAllMissing: ACTION_ADD_ALL_MISSING,
  reflectOwned: ACTION_REFLECT_OWNED,
  searchAllMonitored: ACTION_SEARCH_ALL_MONITORED,
};

// Only reached for an entity not yet monitored.
const MONITOR_ROUTE = "monitor";

const UNMONITOR_ROUTE = "unmonitor";

// Changing the scope of something already monitored, which is not the monitor verb.
const SCOPE_ROUTE = "scope";

const ADD_ALL_MISSING_ROUTE = "add-all-missing";

const REFLECT_OWNED_ROUTE = "reflect-owned";

// The one verb here that makes the instance go and download.
const SEARCH_ALL_MONITORED_ROUTE = "search-all-monitored";

/**
 * Which answer each acting route serves, named as the component the emitted wire document declares
 * for that route's 200 response.
 *
 * Four of the six answer the entity's own state and two answer an enqueued job, so one stand-in
 * type for all six would typecheck only while every answer spells one member the same way.
 * `monitorRoutes.test.ts` reads these values against the document itself.
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
 * The one place either surface learns whether a verb is reachable. The entity menu renders a row
 * disabled when the answer is null and the selection overlay does not offer it at all, so the two
 * cannot disagree about which verbs this build carries out.
 *
 * Total by type, so a secondary action added later has to be classified here.
 */
const SECONDARY_ACTION_ROUTES: Record<SecondaryAction, MonitorActionRoute | null> = {
  addAllMissing: ADD_ALL_MISSING_ROUTE,
  reflectOwned: REFLECT_OWNED_ROUTE,
  searchAllMonitored: SEARCH_ALL_MONITORED_ROUTE,
};

// Total by type, so a reason added to the wire enum fails this build rather than rendering a
// sentence about a setting nobody read.
const REFLECT_OWNED_SKIP_SENTENCE: Record<ReflectOwnedSkip, string> = {
  hardLinksOff: REFLECT_OWNED_SKIPPED,
  hardLinkSettingUnreadable: REFLECT_OWNED_SKIPPED_SETTING_UNREADABLE,
};

const SCOPE_LABEL: Record<MonitorScopeChoice, string> = {
  futureScenes: SCOPE_FUTURE_SCENES,
  allScenes: SCOPE_ALL_SCENES,
};

/** The refusal kinds, so a caller covering all of them cannot miss one. */
export const MONITOR_REFUSAL_KINDS: readonly MonitorRefusalKind[] = membersOf(REFUSALS);

/**
 * The scopes, in the order they render.
 *
 * The order is the label table's own rather than derived from the state: a varying order puts the
 * cheap option under the cursor sometimes and the expensive one others.
 */
export const SCOPE_ORDER: readonly MonitorScopeChoice[] = membersOf(SCOPE_LABEL);

/** The scope taken when the reader takes none. */
const DEFAULT_SCOPE: MonitorScopeChoice = "futureScenes";

/** The secondary actions, in the order they render. */
export const SECONDARY_ACTIONS: readonly SecondaryAction[] = membersOf(SECONDARY_LABEL);

/** The entity kinds this menu is opened from, so a caller covering them cannot miss one. */
export const ENTITY_KINDS: readonly WhisparrEntityKind[] = membersOf(MONITOR_CAPABILITY).filter(
  (kind) => MONITOR_CAPABILITY[kind] !== null,
);

/** The generations something can be connected to. */
export const GENERATIONS: readonly ConnectedGeneration[] = membersOf(CONNECTED_GENERATIONS);

/** How `kind` reads, and whether it leaves anything to offer. */
export function describeMonitorRefusal(kind: MonitorRefusalKind): MonitorRefusal {
  return REFUSALS[kind];
}

/**
 * Whether the wider scope cannot be taken back on the connection `view` was read over.
 *
 * The server states this on the view, because it is a fact about how the connected generation
 * behaves rather than about how this menu renders. A null answer is a read that named no instance,
 * which settles the question neither way, so it reads as not a one-way door and no extra sentence
 * is added.
 */
export function allScenesIsAOneWayDoor(view: EntityMonitoringView | null): boolean {
  return view?.scopeChangeIsRetroactive === false;
}

/**
 * Whether pressing `item` marks every scene the instance already lists as wanted.
 *
 * True of the wider scope row, and of the standalone monitor row, which is what a kind expressing no
 * scope pair is offered and covers the back catalogue with no scope to name.
 */
export function marksTheBackCatalogue(item: MonitorMenuItem): boolean {
  return item.item === "monitor" || (item.item === "scope" && item.scope === "allScenes");
}

/** Which capability `action` needs the connected generation to hold. */
export function capabilityBehindAction(action: SecondaryAction): WhisparrCapability {
  return CAPABILITY_BEHIND_ITEM[action];
}

/** What `reason` states at the control when reflect owned linked nothing. */
export function describeReflectOwnedSkip(reason: ReflectOwnedSkip): string {
  return REFLECT_OWNED_SKIP_SENTENCE[reason];
}

function memberOf(answer: unknown, member: string): unknown {
  return answer !== null && typeof answer === "object"
    ? (answer as Record<string, unknown>)[member]
    : null;
}

/**
 * The refusal `answer` carries, or null where it carries none this build recognises.
 *
 * An answer with no such member is a live path, not a guarded-against one: the POST helper resolves
 * an empty object for an empty 2xx body and for an unparseable one. Validated against the key set
 * of the sentence record, so a kind added to the wire enum is accepted here and still forces a
 * sentence decision there.
 */
export function monitorRefusalIn(answer: unknown): MonitorRefusalKind | null {
  const value = memberOf(answer, "refusal");
  return typeof value === "string" && Object.hasOwn(REFUSALS, value)
    ? (value as MonitorRefusalKind)
    : null;
}

/** The skip reason `answer` carries, or null where it carries none this build recognises. */
export function reflectOwnedSkipIn(answer: unknown): ReflectOwnedSkip | null {
  const value = memberOf(answer, "skipped");
  return typeof value === "string" && Object.hasOwn(REFLECT_OWNED_SKIP_SENTENCE, value)
    ? (value as ReflectOwnedSkip)
    : null;
}

/**
 * What `kind` states beneath the control, or null where it states nothing there.
 *
 * The same flag decides both surfaces. A refusal that leaves nothing to offer speaks in the
 * control's own name and is silent here, so the two cannot disagree about where a reason is given.
 */
export function refusalNoticeFor(kind: MonitorRefusalKind): string | null {
  const refusal = describeMonitorRefusal(kind);
  return refusal.leavesNothingToOffer ? null : refusal.sentence;
}

/**
 * The one sentence beneath the control after the last gesture, or null where there is none.
 *
 * The precedence is over the sentences rather than over the inputs, which is what keeps a healthy
 * answer from silencing a skip: `none` is a refusal kind and is what every healthy read answers, so
 * a branch stopping at a non-null refusal would answer null for the common case and never reach the
 * skip. Falling through on a null sentence also covers the kinds that empty the menu.
 *
 * A failure reads ahead of both: an action that never arrived cannot also have been refused.
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
  const monitorCapability = MONITOR_CAPABILITY[view.kind];
  const cannotMonitorThisKind = monitorCapability === null || !held.has(monitorCapability);

  // One sentence, and the server's kind chooses it. The held list answers only where the server
  // named nothing.
  const reason =
    refusal.sentence ?? (cannotMonitorThisKind ? CAP_UNAVAILABLE_ON_THIS_GENERATION : null);
  const available = !refusal.leavesNothingToOffer && !cannotMonitorThisKind;
  if (!available) {
    return { available, reason, items: [] };
  }

  const transient = inFlight ? WAITING_FOR_WHISPARR : null;
  const face = (label: string, unavailable: string | null) => ({
    label,
    // A permanent reason reads ahead of the transient one: a control that will never work should
    // not say it is waiting.
    reason: unavailable ?? transient,
  });

  // The same two rows mean two different things. On an unmonitored entity they are the monitor
  // gesture and the mark is the choice this menu will carry out. On a monitored entity they report
  // what Whisparr holds, so an answer that named no scope must leave every row unmarked.
  const offersAScopePair = OFFERS_A_SCOPE_PAIR[view.kind];
  const scopes: MonitorMenuItem[] = offersAScopePair
    ? SCOPE_ORDER.map((scope) => ({
        ...face(SCOPE_LABEL[scope], null),
        item: "scope" as const,
        scope,
        selected: view.monitored ? scope === view.scope : scope === DEFAULT_SCOPE,
      }))
    : [];

  const monitorItem: MonitorMenuItem[] =
    scopes.length === 0 && !view.monitored
      ? [
          {
            ...face(MENU_MONITOR, null),
            item: "monitor" as const,
          },
        ]
      : [];

  if (!view.monitored) {
    return { available, reason, items: [...scopes, ...monitorItem] };
  }

  const unmonitor: MonitorMenuItem = {
    ...face(MENU_UNMONITOR, null),
    item: "unmonitor",
  };

  const secondary: MonitorMenuItem[] = SECONDARY_ACTIONS.map((action) => ({
    ...face(
      SECONDARY_LABEL[action],
      held.has(capabilityBehindAction(action)) ? null : CAP_UNAVAILABLE_ON_THIS_GENERATION,
    ),
    item: "secondary" as const,
    action,
  }));

  return { available, reason, items: [...scopes, unmonitor, ...secondary] };
}

/**
 * The route `item` is carried out at, or null where this build serves none.
 *
 * A scope row is two different verbs depending on the state: on an unmonitored entity it is the
 * monitor gesture carrying that scope, and on a monitored one it changes the scope and leaves the
 * flag alone.
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

/**
 * A stable key for one item, so two secondary actions are not the same row.
 *
 * Its own union type rather than a bare string, so a table keyed by it is total and an item added
 * later fails the build rather than drawing a row with no glyph.
 */
export type MonitorMenuItemKey =
  `scope:${MonitorScopeChoice}` | "monitor" | "unmonitor" | `secondary:${SecondaryAction}`;

export function monitorMenuItemKey(item: MonitorMenuItem): MonitorMenuItemKey {
  switch (item.item) {
    case "scope":
      return `scope:${item.scope}`;
    case "secondary":
      return `secondary:${item.action}`;
    default:
      return item.item;
  }
}

/** A verb the bulk route carries. The wire type admits null, which names no verb. */
type BulkVerb = NonNullable<MonitorBulkVerb>;

/** One action the selection overlay offers, already decided. */
export interface BulkMonitorAction {
  readonly key: MonitorMenuItemKey;
  readonly label: string;
  readonly verb: BulkVerb;
  /** The scope the request carries, or null where the verb expresses none. */
  readonly scope: MonitorScopeChoice | null;
  /** Whether pressing it marks every scene the instance already lists as wanted. */
  readonly marksTheBackCatalogue: boolean;
}

/** What a selection of one entity kind can be offered against one connection. */
export interface BulkMonitorOffer {
  readonly actions: readonly BulkMonitorAction[];
  /** The one sentence to state when nothing can be offered, or null when something can. */
  readonly reason: string | null;
  /** Whether the wider scope cannot be taken back on the connected generation. */
  readonly oneWayDoor: boolean;
}

/**
 * What the selection bar offers for a selection of `view`'s kind.
 *
 * Derived from {@link monitorMenu} and {@link routeFor}, so the overlay offers exactly the verbs
 * the entity menu can carry out.
 *
 * Only what is true of the connection decides the offer. A refusal the sampled entity earned is a
 * fact about that one entity, and a selection can hold a hundred others it is not true of, so it is
 * not read here. Nothing connected is the exception, because that is about the connection. The
 * entity's own monitored state is not read either, for the same reason.
 *
 * @param view what a read of one selected entity answered
 */
export function bulkMonitorActions(view: EntityMonitoringView): BulkMonitorOffer {
  const oneWayDoor = allScenesIsAOneWayDoor(view);
  if (view.refusal === "notConfigured") {
    return { actions: [], reason: NO_INSTANCE_CONNECTED, oneWayDoor };
  }

  const connection: EntityMonitoringView = { ...view, refusal: "none" };
  const notYetMonitored = monitorMenu({ ...connection, monitored: false }, false);
  if (!notYetMonitored.available) {
    return { actions: [], reason: notYetMonitored.reason, oneWayDoor };
  }

  const alreadyMonitored = monitorMenu({ ...connection, monitored: true }, false);

  return {
    actions: [
      ...offered(notYetMonitored.items, false),
      // The scope rows of a monitored entity are the scope-change verb, which the bulk route does
      // not carry.
      ...offered(
        alreadyMonitored.items.filter((item) => item.item !== "scope"),
        true,
      ),
    ],
    reason: null,
    oneWayDoor,
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
        verb,
        scope: item.item === "scope" ? item.scope : null,
        marksTheBackCatalogue: marksTheBackCatalogue(item),
      },
    ];
  });
}

/** Which bulk verb one entity route is carried out as, or null where the bulk route carries none. */
function bulkVerbFor(route: string): BulkVerb | null {
  if (route === MONITOR_ROUTE) return "monitor";
  if (route === UNMONITOR_ROUTE) return "unmonitor";
  if (route === SEARCH_ALL_MONITORED_ROUTE) return "searchAllMonitored";
  return null;
}
