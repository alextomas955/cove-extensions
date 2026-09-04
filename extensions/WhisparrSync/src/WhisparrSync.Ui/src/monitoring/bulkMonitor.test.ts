// @vitest-environment jsdom
/**
 * What the selection bar's handler offers, what leaving without choosing sends, and what a choice
 * puts in the body.
 *
 * A DOM is needed because the overlay is mounted imperatively into the document rather than returned
 * as a value: the properties under test are which rows a reader is actually offered and what
 * pressing one sends. The real mounter is used rather than a stand-in for the same reason.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is flushed by waiting rather than by wrapping.
 */
import { test, expect, vi, afterEach } from "vitest";
import type { EntityMonitoringView, WhisparrCapability } from "../wire/api";

vi.mock("@cove-extensions/ui-shared", () => ({
  // The real builder, because the route the handler reads from is one of the things under test.
  extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
}));

interface Sent {
  path: string;
  method: string;
  body: unknown;
}

const sent: Sent[] = [];

// An answer is a function called at request time rather than a promise created ahead of one. A
// promise that settles before anything reads it is reported as unhandled, whatever the code under
// test then does with it.
let readAnswer: () => Promise<unknown> = () => Promise.resolve(null);

class FakeApiError extends Error {
  constructor(
    public status: number,
    public body: string,
  ) {
    super(`${String(status)} ${body}`);
  }
}

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: FakeApiError,
  requestJson: (route: string) => {
    sent.push({ path: route, method: "GET", body: undefined });
    return readAnswer();
  },
}));

// Called at request time for the reason the read answer is: a rejected promise created ahead of the
// call it belongs to is reported as unhandled whatever the code under test then does with it.
let postAnswer: () => Promise<unknown> = () => Promise.resolve({ jobId: "job-1" });

vi.mock("@cove-extensions/ui-shared/postAction", () => ({
  postAction: (route: string, body: unknown) => {
    sent.push({ path: route, method: "POST", body });
    return postAnswer();
  },
}));

const { monitorSelected } = await import("./bulkMonitor");
const {
  BULK_ACTIONS_COULD_NOT_BE_OFFERED,
  BULK_CANCEL,
  BULK_CHOOSE_AN_ACTION,
  BULK_CLOSE,
  BULK_SELECTION_IS_OVER_THE_BOUND,
  BULK_SELECTION_WAS_NOT_STARTED,
  CAP_UNAVAILABLE_ON_THIS_GENERATION,
  SCOPE_ALL_SCENES,
  SCOPE_FUTURE_SCENES,
  STOP_MONITORING_IN_WHISPARR,
  UNMONITORING_DOES_NOT_RETRACT,
} = await import("../common/ui/copy");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

const EVERY_CAPABILITY: WhisparrCapability[] = [
  "outOfBandCallbackSecret",
  "monitorStudio",
  "monitorPerformer",
  "registerMissingScenes",
  "reflectOwnedFiles",
  "searchMonitored",
];

function viewOf(over: Partial<EntityMonitoringView> = {}): EntityMonitoringView {
  return {
    kind: "studio",
    generation: "v3",
    monitored: false,
    refusal: "none",
    capabilities: EVERY_CAPABILITY,
    scope: null,
    ...over,
  };
}

function answering(view: EntityMonitoringView): void {
  readAnswer = () => Promise.resolve(view);
}

function buttons(): HTMLButtonElement[] {
  return [...document.querySelectorAll("button")];
}

function labels(): string[] {
  return buttons().map((button) => button.textContent);
}

function press(label: string): void {
  const button = buttons().find((candidate) => candidate.textContent === label);
  if (button === undefined) {
    throw new Error(`no button reads "${label}"; the overlay offers ${JSON.stringify(labels())}`);
  }
  button.click();
}

/**
 * Starts the handler and waits for its overlay to be on screen.
 *
 * The handler's own promise is returned WRAPPED. An async function returning it bare would await it,
 * and it does not settle until the overlay is answered.
 */
async function open(
  entityType: string,
  entityIds: number[],
): Promise<{ running: Promise<unknown> }> {
  const running = monitorSelected(null, { entityType, entityIds });
  await sleep(COMMIT_MS);
  return { running };
}

afterEach(() => {
  sent.length = 0;
  readAnswer = () => Promise.resolve(null);
  postAnswer = () => Promise.resolve({ jobId: "job-1" });
  document.body.innerHTML = "";
});

/** What the route answers a selection larger than its bound, body and all. */
const OVER_THE_BOUND = '{"code":"TOO_MANY_IDS","max":1000}';

/** What this generation puts in the body of a refusal: its own words, at length. */
const A_REFUSAL_CARRYING_THE_INSTANCES_WORDS =
  '{"code":"SOMETHING_ELSE","message":"System.InvalidOperationException: at Whisparr.Api.V3"}';

/**
 * Presses one offered row and waits for whatever it leads to to be on screen.
 *
 * The handler's own promise is deliberately not touched here: a refusal opens a second overlay, and
 * the handler does not settle until that overlay is answered too.
 */
async function chosen(label: string): Promise<void> {
  press(label);
  await sleep(COMMIT_MS);
}

test("the read names the first selected entity, on the route the entity page uses", async () => {
  answering(viewOf());

  const { running } = await open("studios", [7, 8, 9]);
  press(BULK_CANCEL);
  await running;

  expect(sent[0]).toEqual({
    path: "/extensions/com.alextomas955.whisparrsync/entity/studio/7/monitoring",
    method: "GET",
    body: undefined,
  });
});

test("leaving without choosing returns the cancelled result and posts nothing", async () => {
  answering(viewOf());

  const { running } = await open("studios", [7]);
  expect(document.body.textContent).toContain(BULK_CHOOSE_AN_ACTION);
  press(BULK_CANCEL);

  await expect(running).resolves.toEqual({ cancelled: true });
  expect(sent.filter((call) => call.method === "POST")).toEqual([]);
});

test("the chosen verb and scope reach the body", async () => {
  answering(viewOf());

  const { running } = await open("studios", [7, 8]);
  press(SCOPE_ALL_SCENES);
  await running;

  const posted = sent.find((call) => call.method === "POST");
  expect(posted?.path).toBe("/extensions/com.alextomas955.whisparrsync/entities/bulk-monitor");
  expect(posted?.body).toEqual({
    EntityType: "studios",
    Verb: "monitor",
    Scope: "allScenes",
    EntityIds: [7, 8],
  });
});

test("unmonitoring sends its own verb and no scope", async () => {
  answering(viewOf());

  const { running } = await open("studios", [7]);
  press(STOP_MONITORING_IN_WHISPARR);
  await running;

  const posted = sent.find((call) => call.method === "POST");
  expect(posted?.body).toEqual({
    EntityType: "studios",
    Verb: "unmonitor",
    Scope: null,
    EntityIds: [7],
  });
});

/**
 * Requests bind case-insensitively while responses are camelCase, so the casing is read from the
 * server per direction. The expected keys are hand-written from the C# record.
 */
test("the posted body's keys are PascalCase", async () => {
  answering(viewOf());

  const { running } = await open("studios", [7]);
  press(SCOPE_FUTURE_SCENES);
  await running;

  const posted = sent.find((call) => call.method === "POST");
  expect(Object.keys(posted?.body as object)).toEqual(["EntityType", "Verb", "Scope", "EntityIds"]);
});

test("a failed capability read states the reason, offers nothing and posts nothing", async () => {
  readAnswer = () => Promise.reject(new Error("nothing answered"));

  const { running } = await open("studios", [7]);

  expect(document.body.textContent).toContain(BULK_ACTIONS_COULD_NOT_BE_OFFERED);
  expect(labels()).toEqual([BULK_CLOSE]);
  press(BULK_CLOSE);

  await expect(running).resolves.toEqual({ cancelled: true });
  expect(sent.filter((call) => call.method === "POST")).toEqual([]);
});

test("a verb absent from the held list is not offered", async () => {
  answering(viewOf({ kind: "performer", generation: "v2", capabilities: ["monitorStudio"] }));

  const { running } = await open("performers", [7]);

  expect(document.body.textContent).toContain(CAP_UNAVAILABLE_ON_THIS_GENERATION);
  expect(labels()).toEqual([BULK_CLOSE]);
  press(BULK_CLOSE);

  await expect(running).resolves.toEqual({ cancelled: true });
  expect(sent.filter((call) => call.method === "POST")).toEqual([]);
});

/**
 * The three secondary actions are served by no single-entity route, so the entity menu renders each
 * disabled. Whatever that menu renders disabled the selection overlay does not offer at all, even
 * where the connected generation holds every capability behind them.
 */
test("the secondary actions are not offered even where the generation holds all of them", async () => {
  answering(viewOf());

  const { running } = await open("studios", [7]);

  expect(labels()).toEqual([
    SCOPE_FUTURE_SCENES,
    SCOPE_ALL_SCENES,
    STOP_MONITORING_IN_WHISPARR,
    BULK_CANCEL,
  ]);
  press(BULK_CANCEL);
  await running;
});

test("a selection type this product does not address opens nothing and posts nothing", async () => {
  answering(viewOf());

  const running = monitorSelected(null, { entityType: "tags", entityIds: [7] });
  await sleep(COMMIT_MS);

  await expect(running).resolves.toEqual({ cancelled: true });
  expect(sent).toEqual([]);
});

test("an empty selection opens nothing and posts nothing", async () => {
  answering(viewOf());

  const running = monitorSelected(null, { entityType: "studios", entityIds: [] });
  await sleep(COMMIT_MS);

  await expect(running).resolves.toEqual({ cancelled: true });
  expect(sent).toEqual([]);
});

test("a selection over the route's bound resolves rather than rejecting", async () => {
  answering(viewOf());
  postAnswer = () => Promise.reject(new FakeApiError(400, OVER_THE_BOUND));

  const { running } = await open("studios", [7, 8]);
  await chosen(SCOPE_FUTURE_SCENES);
  press(BULK_CLOSE);

  await expect(running).resolves.toEqual({ cancelled: true });
});

test("a selection over the bound is refused in this product's own sentence", async () => {
  answering(viewOf());
  postAnswer = () => Promise.reject(new FakeApiError(400, OVER_THE_BOUND));

  const { running } = await open("studios", [7, 8]);
  await chosen(SCOPE_FUTURE_SCENES);

  expect(document.body.textContent).toContain(BULK_SELECTION_IS_OVER_THE_BOUND);
  expect(labels()).toEqual([BULK_CLOSE]);
  press(BULK_CLOSE);
  await running;
});

test("no part of the refusal body reaches the sentence the reader is shown", async () => {
  answering(viewOf());
  postAnswer = () => Promise.reject(new FakeApiError(500, A_REFUSAL_CARRYING_THE_INSTANCES_WORDS));

  const { running } = await open("studios", [7]);
  await chosen(SCOPE_FUTURE_SCENES);

  const shown = document.body.textContent;
  expect(shown).toContain(BULK_SELECTION_WAS_NOT_STARTED);
  for (const word of ["InvalidOperationException", "Whisparr.Api.V3", "SOMETHING_ELSE", "500"]) {
    expect(shown, word).not.toContain(word);
  }
  press(BULK_CLOSE);
  await running;
});

test("any other refusal of the whole gesture returns the cancelled result", async () => {
  answering(viewOf());
  postAnswer = () => Promise.reject(new FakeApiError(500, A_REFUSAL_CARRYING_THE_INSTANCES_WORDS));

  const { running } = await open("studios", [7]);
  await chosen(SCOPE_FUTURE_SCENES);
  press(BULK_CLOSE);

  await expect(running).resolves.toEqual({ cancelled: true });
});

test("a selection the route accepts returns the success result and opens no second overlay", async () => {
  answering(viewOf());

  const { running } = await open("studios", [7]);
  await chosen(SCOPE_FUTURE_SCENES);

  await expect(running).resolves.toEqual({});
  expect(document.body.textContent).toBe("");
});

/**
 * D-20 holds wherever unmonitoring is offered. A reader unmonitoring a whole selection needs the
 * same sentence as one unmonitoring a single entity.
 */
test("the unmonitor row states what unmonitoring does not retract", async () => {
  answering(viewOf());

  const { running } = await open("studios", [7]);

  expect(document.body.textContent).toContain(UNMONITORING_DOES_NOT_RETRACT);
  press(BULK_CANCEL);
  await running;
});
