// @vitest-environment jsdom
/**
 * What the control paints at each stage of its own read, what a press opens, and what a choice sends.
 *
 * A DOM is needed because the properties under test are about the rendered element rather than about
 * a value a helper returns: a mark drawn before the read answers is a wrong-generation colour on
 * screen, a failed read painted as unmonitored is a wrong answer dressed as an answer, and a body
 * carrying an identifier is a request the server is obliged to ignore.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is flushed by waiting rather than by wrapping. The shared
 * primitives, the host's authenticated fetch and its POST helper all stand in, because each resolves
 * only inside a consuming bundle.
 */
import { test, expect, vi, afterEach } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

vi.mock("@cove-extensions/ui-shared", () => ({
  // The real primitive draws an SVG, so the stand-in draws one too. A stand-in rendering nothing
  // would make every "no mark yet" assertion below agree with the stand-in instead of with what the
  // read surface actually paints while it reads.
  Spinner: () => createElement("svg", { "data-stand-in": "spinner" }),
  // The real builder, because the route the control asks for is one of the things under test.
  extensionApi: (extensionId: string) => (route: string) => `/extensions/${extensionId}/${route}`,
}));

interface Sent {
  path: string;
  method: string;
  body: string | undefined;
}

const sent: Sent[] = [];

// Each answer is a function called at request time rather than a promise created ahead of one. A
// promise that settles before anything reads it is reported as unhandled, whatever the code under
// test then does with it.
type Answer = () => Promise<unknown>;
let readAnswer: Answer = () => Promise.resolve(null);
let actionAnswer: Answer = () => Promise.resolve({});

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {
    constructor(
      public status: number,
      public body: string,
    ) {
      super(`${String(status)} ${body}`);
    }
  },
  requestJson: (route: string) => {
    sent.push({ path: route, method: "GET", body: undefined });
    return readAnswer();
  },
}));

vi.mock("@cove-extensions/ui-shared/postAction", () => ({
  postAction: (route: string, body: unknown) => {
    sent.push({ path: route, method: "POST", body: JSON.stringify(body) });
    return actionAnswer();
  },
}));

const { WhisparrPerformerActions, WhisparrStudioActions } = await import("./EntityMonitorButton");
const {
  ACTION_ABSENT_IN_THIS_VERSION,
  ACTION_ADD_ALL_MISSING,
  ACTION_DID_NOT_REACH_WHISPARR,
  ACTION_REFLECT_OWNED,
  CAP_UNAVAILABLE_ON_THIS_GENERATION,
  MONITORED_IN_WHISPARR,
  MONITORING_COULD_NOT_BE_READ,
  MONITOR_IN_WHISPARR,
  REFLECT_OWNED_SKIPPED,
  SCOPE_ALL_SCENES,
  STOP_MONITORING_IN_WHISPARR,
} = await import("../common/ui/copy");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

/**
 * Whether `element` carries `cls` as a WHOLE class.
 *
 * A substring check reports an absent class as present: the host's own action-row string already
 * carries `hover:border-accent`, so a substring test for the monitored border passes on the
 * unmonitored control too.
 */
function hasClass(element: Element | null, cls: string): boolean {
  return element?.classList.contains(cls) === true;
}

function view(overrides: Record<string, unknown>) {
  return {
    kind: "studio",
    generation: "v3",
    monitored: false,
    refusal: "none",
    capabilities: ["monitorStudio"],
    ...overrides,
  };
}

const teardowns: (() => void)[] = [];
afterEach(() => {
  while (teardowns.length > 0) teardowns.pop()?.();
  sent.length = 0;
  readAnswer = () => Promise.resolve(null);
  actionAnswer = () => Promise.resolve({});
});

async function render(node: ReactNode) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(node);
  await sleep(COMMIT_MS);
  teardowns.push(() => {
    root.unmount();
    container.remove();
  });
  return {
    container,
    button: container.querySelector("button"),
    marks: () => container.querySelector("button")?.querySelectorAll("svg").length ?? 0,
    // Queried from the document, because the menu is portaled out of the host page's own hero: that
    // container clips its overflow, so a panel left in the flow there would be cut off.
    menu: () => document.body.querySelector('[role="menu"]'),
    rows: () => [...document.body.querySelectorAll<HTMLButtonElement>('[role^="menuitem"]')],
  };
}

/** A read that never settles, which is the frame under test. */
const NEVER: Answer = () =>
  new Promise(() => {
    // intentionally never resolved
  });

test("the first frame is a bordered shell with no mark at all", async () => {
  readAnswer = NEVER;

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));

  expect(rendered.button).not.toBeNull();
  expect(rendered.marks()).toBe(0);
  expect(rendered.button?.getAttribute("aria-label")).toBe(MONITOR_IN_WHISPARR);
});

test("a read answering not-monitored paints the mark and no tick", async () => {
  readAnswer = () => Promise.resolve(view({}));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));

  expect(rendered.marks()).toBe(1);
  expect(hasClass(rendered.button, "border-accent")).toBe(false);
  expect(rendered.button?.getAttribute("aria-label")).toBe(MONITOR_IN_WHISPARR);
});

test("the monitored state is a border and a tick, and no accent fill on the control itself", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true }));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));

  // The mark and the tick: the state lives on the border plus the tick, never on the mark, which is
  // a filled two-tone disc that cannot inherit a colour.
  expect(rendered.marks()).toBe(2);
  expect(hasClass(rendered.button, "border-accent")).toBe(true);
  expect(rendered.button?.getAttribute("aria-label")).toBe(MONITORED_IN_WHISPARR);

  // Neither accent background is on the button. The tint that lifts the border is a layer behind the
  // mark, which is what keeps a two-tone disc off a solid accent field.
  expect(hasClass(rendered.button, "bg-accent")).toBe(false);
  expect(hasClass(rendered.button, "bg-accent/10")).toBe(false);
  const tint = rendered.container.querySelector("span.bg-accent\\/10");
  expect(tint, "the accent tint is not drawn behind the mark").not.toBeNull();
  expect(tint?.parentElement).toBe(rendered.button);
});

test("a failed read says so, and never paints the unmonitored state", async () => {
  readAnswer = () => Promise.reject(new Error("nothing answered"));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));

  expect(rendered.button).not.toBeNull();
  expect(rendered.marks()).toBe(0);
  expect(rendered.button?.disabled).toBe(true);
  expect(rendered.button?.getAttribute("aria-label")).toBe(
    `${MONITOR_IN_WHISPARR}, ${MONITORING_COULD_NOT_BE_READ}`,
  );
  expect(rendered.button?.getAttribute("title")).toBe(
    `${MONITOR_IN_WHISPARR}, ${MONITORING_COULD_NOT_BE_READ}`,
  );
});

test("the read names the mounted entity and asks for its monitoring", async () => {
  readAnswer = () => Promise.resolve(view({}));

  await render(createElement(WhisparrStudioActions, { studio: { id: 42 } }));

  expect(sent).toHaveLength(1);
  expect(sent[0].path).toBe(
    "/extensions/com.alextomas955.whisparrsync/entity/studio/42/monitoring",
  );
  expect(sent[0].method).toBe("GET");
});

test("the performer control asks about a performer, on its own page", async () => {
  readAnswer = () => Promise.resolve(view({ kind: "performer" }));

  await render(createElement(WhisparrPerformerActions, { performer: { id: 7 } }));

  expect(sent).toHaveLength(1);
  expect(sent[0].path).toBe(
    "/extensions/com.alextomas955.whisparrsync/entity/performer/7/monitoring",
  );
});

test("a press opens the menu and posts nothing, because monitoring lives in the menu", async () => {
  readAnswer = () => Promise.resolve(view({}));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 42 } }));
  expect(rendered.menu()).toBeNull();

  rendered.button?.click();
  await sleep(COMMIT_MS);

  expect(rendered.menu()).not.toBeNull();
  expect(rendered.button?.getAttribute("aria-expanded")).toBe("true");
  expect(sent.filter((call) => call.method === "POST")).toEqual([]);
});

test("choosing a scope posts it, with no identifier of any kind, and reads the state back", async () => {
  readAnswer = () => Promise.resolve(view({}));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 42 } }));
  rendered.button?.click();
  await sleep(COMMIT_MS);

  rendered.rows()[0].click();
  await sleep(COMMIT_MS);

  const posted = sent.filter((call) => call.method === "POST");
  expect(posted).toHaveLength(1);
  expect(posted[0].path).toBe("/extensions/com.alextomas955.whisparrsync/entity/studio/42/monitor");
  expect(Object.keys(JSON.parse(posted[0].body ?? "{}") as Record<string, unknown>)).toEqual([
    "scope",
  ]);
  expect((JSON.parse(posted[0].body ?? "{}") as { scope: string }).scope).toBe("futureScenes");

  // Exactly one re-read: the mount read, then one more after the action settled. What the entity now
  // is comes from the instance, never from what the browser asked for.
  expect(sent.filter((call) => call.method === "GET")).toHaveLength(2);
});

test("an item already on its way is not pressable again", async () => {
  readAnswer = () => Promise.resolve(view({}));
  actionAnswer = NEVER;

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  rendered.button?.click();
  await sleep(COMMIT_MS);

  rendered.rows()[0].click();
  await sleep(COMMIT_MS);

  expect(rendered.rows().every((row) => row.disabled)).toBe(true);
  rendered.rows()[1].click();
  await sleep(COMMIT_MS);

  expect(sent.filter((call) => call.method === "POST")).toHaveLength(1);
});

test("a v2 performer keeps its control, disabled, saying what the generation cannot do", async () => {
  readAnswer = () =>
    Promise.resolve(
      view({
        kind: "performer",
        generation: "v2",
        refusal: "capabilityAbsentOnThisGeneration",
        capabilities: ["outOfBandCallbackSecret", "monitorStudio"],
      }),
    );

  const rendered = await render(createElement(WhisparrPerformerActions, { performer: { id: 3 } }));

  expect(rendered.button, "the control is omitted rather than refused").not.toBeNull();
  expect(rendered.button?.disabled).toBe(true);
  expect(rendered.button?.getAttribute("aria-label")).toBe(
    `${MONITOR_IN_WHISPARR}, ${CAP_UNAVAILABLE_ON_THIS_GENERATION}`,
  );
});

test("each verb this build serves posts its own route rather than the monitor one", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true }));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  rendered.button?.click();
  await sleep(COMMIT_MS);

  const rows = rendered.rows();
  const scope = rows.find((row) => (row.getAttribute("title") ?? "").startsWith(SCOPE_ALL_SCENES));
  const unmonitor = rows.find((row) =>
    (row.getAttribute("title") ?? "").startsWith(STOP_MONITORING_IN_WHISPARR),
  );

  // A scope change on something already monitored is a different verb from monitoring it, so the
  // two must not share a route: posting the monitor route here would re-add an entity the instance
  // already holds.
  // The POSTs alone. Each action is followed by a read back, so the last call recorded is a GET
  // whichever route the action used.
  const posted = () => sent.filter((call) => call.method === "POST").map((call) => call.path);

  expect(scope?.disabled).toBe(false);
  scope?.click();
  await sleep(COMMIT_MS);
  expect(posted().at(-1)?.endsWith("/scope")).toBe(true);

  expect(unmonitor?.disabled).toBe(false);
  unmonitor?.click();
  await sleep(COMMIT_MS);
  expect(posted().at(-1)?.endsWith("/unmonitor")).toBe(true);
  expect(posted()).toHaveLength(2);
});

/** A generation holding the capability the reflect-owned row is gated on. */
const REFLECTING = ["monitorStudio", "reflectOwnedFiles"];

/** The reflect-owned row of an open menu, which this build does serve a route for. */
async function pressReflectOwned(rendered: Awaited<ReturnType<typeof render>>) {
  rendered.button?.click();
  await sleep(COMMIT_MS);
  const reflect = rendered
    .rows()
    .find((row) => (row.getAttribute("title") ?? "").startsWith(ACTION_REFLECT_OWNED));
  expect(reflect?.disabled).toBe(false);
  reflect?.click();
  await sleep(COMMIT_MS);
  return rendered.container.querySelector('[role="status"]');
}

test("an action the server answered and skipped states the reason at the control", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true, capabilities: REFLECTING }));
  actionAnswer = () => Promise.resolve({ skipped: "hardLinksOff", jobId: null, refusal: "none" });

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  const notice = await pressReflectOwned(rendered);

  expect(notice?.textContent).toBe(REFLECT_OWNED_SKIPPED);
  expect(
    sent
      .filter((call) => call.method === "POST")
      .at(-1)
      ?.path.endsWith("/reflect-owned"),
  ).toBe(true);
});

test("an action that never reached the instance says that instead", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true, capabilities: REFLECTING }));
  actionAnswer = () => Promise.reject(new Error("nothing answered"));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  const notice = await pressReflectOwned(rendered);

  expect(notice?.textContent).toBe(ACTION_DID_NOT_REACH_WHISPARR);
});

test("an action that was carried out states nothing at the control", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true, capabilities: REFLECTING }));
  actionAnswer = () => Promise.resolve({ skipped: null, jobId: "job-1", refusal: "none" });

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  const notice = await pressReflectOwned(rendered);

  expect(notice).toBeNull();
});

test("an item this build serves no route for is offered dimmed rather than pressed into nothing", async () => {
  // A capability the generation DOES hold, so the row is not dimmed for the capability reason and
  // the only thing left to dim it is the absent route. Add all missing is the one verb left in that
  // state: the other two are served, and a row this build cannot carry out has to stay legible.
  readAnswer = () =>
    Promise.resolve(
      view({ monitored: true, capabilities: ["monitorStudio", "registerMissingScenes"] }),
    );

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  rendered.button?.click();
  await sleep(COMMIT_MS);

  const unserved = rendered
    .rows()
    .find((row) => (row.getAttribute("title") ?? "").startsWith(ACTION_ADD_ALL_MISSING));

  expect(unserved).toBeDefined();
  expect(unserved?.disabled).toBe(true);
  expect(unserved?.getAttribute("title")?.endsWith(ACTION_ABSENT_IN_THIS_VERSION)).toBe(true);

  unserved?.click();
  await sleep(COMMIT_MS);
  expect(sent.filter((call) => call.method === "POST")).toEqual([]);
});
