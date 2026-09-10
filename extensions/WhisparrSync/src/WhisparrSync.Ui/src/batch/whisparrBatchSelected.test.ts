// @vitest-environment jsdom
/**
 * What the videos selection bar's handler offers, what it sends, and what it states when the route
 * refuses the whole gesture.
 *
 * A DOM is needed because the overlay is mounted imperatively into the document rather than returned
 * as a value: the properties under test are which rows a reader is actually offered, and that a
 * refusal is stated in that same overlay rather than escaping to the host's own alert.
 */
import { test, expect, vi, afterEach } from "vitest";

vi.mock("@cove-extensions/ui-shared", () => ({
  // The real builder, because the route the handler posts to is one of the things under test.
  extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
}));

let opened = 0;

vi.mock("@cove-extensions/ui-shared/overlay", async (importOriginal) => {
  // The real mounter, wrapped only to count. What a reader is offered is read off the document it
  // mounts into, and a stand-in would answer that question with itself.
  const real = await importOriginal<typeof import("@cove-extensions/ui-shared/overlay")>();
  return {
    ...real,
    presentOverlay: (render: Parameters<typeof real.presentOverlay>[0]) => {
      opened += 1;
      return real.presentOverlay(render);
    },
  };
});

interface Sent {
  path: string;
  body: unknown;
}

const sent: Sent[] = [];

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
}));

// Called at request time rather than created ahead of one: a rejected promise created before the
// call it belongs to is reported as unhandled whatever the code under test then does with it.
let postAnswer: () => Promise<unknown> = () => Promise.resolve({ jobId: "job-1" });

vi.mock("@cove-extensions/ui-shared/postAction", () => ({
  postAction: (route: string, body: unknown) => {
    sent.push({ path: route, body });
    return postAnswer();
  },
}));

const { sceneBatchSelected } = await import("./whisparrBatchSelected");
const {
  BATCH_SEARCH_IS_OVER_THE_BOUND,
  BULK_CANCEL,
  BULK_SELECTION_IS_OVER_THE_BOUND,
  RUN_WAS_NOT_STARTED,
  MENU_ADD,
  MENU_EXCLUDE,
  MENU_MONITOR,
  MENU_UNMONITOR,
  SCENE_SEARCH,
  selectionMenuHeader,
} = await import("../common/ui/copy");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

const BATCH_ROUTE = "/extensions/com.alextomas955.whisparrsync/scenes/batch";

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
 * Starts the handler and waits for whatever it opens to be on screen.
 *
 * The handler's own promise is returned WRAPPED. An async function returning it bare would await it,
 * and it does not settle until the overlay is answered.
 */
async function open(
  entityType: string,
  entityIds: number[],
): Promise<{ running: Promise<unknown> }> {
  const running = sceneBatchSelected(null, { entityType, entityIds });
  await sleep(COMMIT_MS);
  return { running };
}

/** Presses one offered row and waits for whatever it leads to to be on screen. */
async function chosen(label: string): Promise<void> {
  press(label);
  await sleep(COMMIT_MS);
}

/** Refuses the post with one answer, and returns what the reader was left reading. */
async function refusedWith(status: number, body: string): Promise<string> {
  postAnswer = () => Promise.reject(new FakeApiError(status, body));

  const { running } = await open("video", [1, 2]);
  await chosen(SCENE_SEARCH);
  const stated = document.body.textContent;
  press("Close");
  await expect(running).resolves.toEqual({ cancelled: true });

  return stated;
}

afterEach(() => {
  sent.length = 0;
  opened = 0;
  postAnswer = () => Promise.resolve({ jobId: "job-1" });
  document.body.innerHTML = "";
});

test("a selection that is not the videos one answers cancelled and opens nothing", async () => {
  await expect(
    sceneBatchSelected(null, { entityType: "studios", entityIds: [1] }),
  ).resolves.toEqual({ cancelled: true });

  expect(opened).toBe(0);
  expect(sent).toEqual([]);
});

test("an empty selection answers cancelled and opens nothing", async () => {
  await expect(sceneBatchSelected(null, { entityType: "video", entityIds: [] })).resolves.toEqual({
    cancelled: true,
  });

  expect(opened).toBe(0);
  expect(sent).toEqual([]);
});

test("offers the five rows in their fixed order, headed by the count", async () => {
  const { running } = await open("video", [1, 2]);

  expect(labels()).toEqual([
    MENU_ADD,
    MENU_MONITOR,
    MENU_UNMONITOR,
    SCENE_SEARCH,
    MENU_EXCLUDE,
    BULK_CANCEL,
  ]);
  expect(document.querySelector('[role="menu"]')?.getAttribute("aria-label")).toBe(
    selectionMenuHeader(2),
  );
  expect(document.querySelectorAll('[role="menu"] p')).toHaveLength(0);

  press(BULK_CANCEL);
  await running;
});

test("leaving without choosing answers cancelled and sends nothing", async () => {
  const { running } = await open("video", [1, 2]);
  press(BULK_CANCEL);

  await expect(running).resolves.toEqual({ cancelled: true });
  expect(sent).toEqual([]);
});

test("a chosen row posts once, in the spelling the route binds", async () => {
  const { running } = await open("video", [7, 8]);
  await chosen(MENU_EXCLUDE);

  await expect(running).resolves.toEqual({});
  expect(sent).toEqual([
    {
      path: BATCH_ROUTE,
      body: { EntityType: "video", Verb: "exclude", CoveIds: [7, 8] },
    },
  ]);
  expect(opened).toBe(1);
});

test("a refusal at the shipped bound states the sentence naming that bound", async () => {
  const stated = await refusedWith(400, '{"code":"TOO_MANY_IDS","max":1000}');

  expect(stated).toContain(BULK_SELECTION_IS_OVER_THE_BOUND);
});

test("a refusal at the search bound states the sentence naming its own lower limit", async () => {
  const stated = await refusedWith(400, '{"code":"TOO_MANY_SEARCH_IDS","max":100}');

  expect(stated).toContain(BATCH_SEARCH_IS_OVER_THE_BOUND);
  expect(stated).not.toContain(BULK_SELECTION_IS_OVER_THE_BOUND);
  // The same overlay, reopened: once to offer the rows and once to state the refusal.
  expect(opened).toBe(2);
});

test("any other refusal states that the run was not started", async () => {
  const stated = await refusedWith(
    500,
    '{"code":"SOMETHING_ELSE","message":"System.InvalidOperationException: at Whisparr.Api.V3"}',
  );

  expect(stated).toContain(RUN_WAS_NOT_STARTED);
  expect(stated).not.toContain("System.InvalidOperationException");
});

test("a refusal whose body is not JSON states that the run was not started", async () => {
  const stated = await refusedWith(502, "<html>Bad Gateway</html>");

  expect(stated).toContain(RUN_WAS_NOT_STARTED);
});

test("a refusal whose body names no code states that the run was not started", async () => {
  const stated = await refusedWith(400, '{"message":"no code here"}');

  expect(stated).toContain(RUN_WAS_NOT_STARTED);
});

test("a refusal whose code is not a string states that the run was not started", async () => {
  const stated = await refusedWith(400, '{"code":429}');

  expect(stated).toContain(RUN_WAS_NOT_STARTED);
});
