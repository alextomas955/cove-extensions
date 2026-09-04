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
  ACTION_ADD_ALL_MISSING,
  ACTION_DID_NOT_REACH_WHISPARR,
  ACTION_REFLECT_OWNED,
  CAP_UNAVAILABLE_ON_THIS_GENERATION,
  INSTANCE_OFFERS_NO_QUALITY_PROFILE,
  INSTANCE_REFUSED,
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
  // Queried from the document for the reason the menu is: the hero clips its children, so the
  // notice leaves that container too and is not reachable from the control's own subtree.
  return document.body.querySelector('[role="status"]');
}

/** How many times `sentence` is written anywhere in the document. */
function occurrencesOf(sentence: string): number {
  return document.body.textContent.split(sentence).length - 1;
}

/** The element the trigger sits in, which is the subtree inside the host's clipping hero. */
function wrapperOf(rendered: Awaited<ReturnType<typeof render>>): Element {
  const wrapper = rendered.button?.parentElement ?? null;
  expect(wrapper).not.toBeNull();
  return wrapper as Element;
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

test("a failure notice leaves the container the menu had to leave", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true, capabilities: REFLECTING }));
  actionAnswer = () => Promise.reject(new Error("nothing answered"));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  const notice = await pressReflectOwned(rendered);

  expect(notice).not.toBeNull();
  expect(wrapperOf(rendered).contains(notice)).toBe(false);
  expect(document.body.contains(notice)).toBe(true);
  expect(notice?.getAttribute("role")).toBe("status");
});

test("a failure notice is anchored to the control, on the menu's own placement", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true, capabilities: REFLECTING }));
  actionAnswer = () => Promise.reject(new Error("nothing answered"));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  const notice = (await pressReflectOwned(rendered)) as HTMLElement | null;

  // The menu is still open, so the two share one placed container rather than each carrying the
  // same rectangle: two elements placed at one rectangle is what made the notice cover the menu.
  const menu = rendered.menu() as HTMLElement;
  expect(menu).not.toBeNull();
  const container = menu.parentElement!;
  expect(container.contains(notice)).toBe(true);
  expect(container.style.top).not.toBe("");
  expect(notice?.style.top).toBe("");
});

/**
 * jsdom reports every element rectangle as zeroes, so no geometric assertion is available and none
 * may be written: one would pass on any layout at all. What stands in for it is the document order
 * the paint follows from, which compareDocumentPosition reports directly.
 */
test("with the menu open the notice follows the menu and is not inside it", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true, capabilities: REFLECTING }));
  actionAnswer = () => Promise.reject(new Error("nothing answered"));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  const notice = (await pressReflectOwned(rendered)) as HTMLElement;
  const menu = rendered.menu() as HTMLElement;

  expect(notice).not.toBeNull();
  expect(menu).not.toBeNull();

  // The two assertions that tell this shape apart from the one that covered the menu. Both elements
  // were children of the body before, each carrying the same rectangle, so sharing a parent and
  // following in document order were already true of the covering shape and guard nothing on their
  // own.
  expect(notice.parentElement).not.toBe(document.body);
  expect(notice.classList.contains("fixed")).toBe(false);

  expect(menu.contains(notice)).toBe(false);
  expect(notice.parentElement).toBe(menu.parentElement);
  expect(menu.compareDocumentPosition(notice) & Node.DOCUMENT_POSITION_FOLLOWING).toBeGreaterThan(
    0,
  );
});

test("with the menu closed the notice still renders", async () => {
  readAnswer = () => Promise.resolve(view({ refusal: "instanceRefused" }));

  await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));

  expect(document.body.querySelectorAll('[role="status"]')).toHaveLength(1);
  expect(document.body.querySelector('[role="menu"]')).toBeNull();
  expect(document.body.querySelector('[role="status"]')?.textContent).toBe(INSTANCE_REFUSED);
});

test("the notice appears exactly once whichever way the menu is", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true, capabilities: REFLECTING }));
  actionAnswer = () => Promise.resolve({ skipped: "hardLinksOff", jobId: null, refusal: "none" });

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  await pressReflectOwned(rendered);

  expect(rendered.menu()).not.toBeNull();
  expect(document.body.querySelectorAll('[role="status"]')).toHaveLength(1);
  expect(occurrencesOf(REFLECT_OWNED_SKIPPED)).toBe(1);

  rendered.button?.click();
  await sleep(COMMIT_MS);

  expect(rendered.menu()).toBeNull();
  expect(document.body.querySelectorAll('[role="status"]')).toHaveLength(1);
  expect(occurrencesOf(REFLECT_OWNED_SKIPPED)).toBe(1);
});

test("a skipped action's notice leaves that container too", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true, capabilities: REFLECTING }));
  actionAnswer = () => Promise.resolve({ skipped: "hardLinksOff", jobId: null, refusal: "none" });

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  const notice = await pressReflectOwned(rendered);

  expect(notice?.textContent).toBe(REFLECT_OWNED_SKIPPED);
  expect(wrapperOf(rendered).contains(notice)).toBe(false);
  expect(document.body.contains(notice)).toBe(true);
});

test("nothing pressed at all leaves no notice anywhere in the document", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true, capabilities: REFLECTING }));

  await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));

  expect(document.body.querySelector('[role="status"]')).toBeNull();
});

test("an action that was carried out states nothing at the control", async () => {
  readAnswer = () => Promise.resolve(view({ monitored: true, capabilities: REFLECTING }));
  actionAnswer = () => Promise.resolve({ skipped: null, jobId: "job-1", refusal: "none" });

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  const notice = await pressReflectOwned(rendered);

  expect(notice).toBeNull();
});

test("a press refused for no quality profile states that beneath the control", async () => {
  readAnswer = () => Promise.resolve(view({}));
  actionAnswer = () => Promise.resolve(view({ refusal: "noQualityProfile", scope: null }));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  rendered.button?.click();
  await sleep(COMMIT_MS);

  rendered.rows()[0].click();
  await sleep(COMMIT_MS);

  expect(document.body.textContent).toContain(INSTANCE_OFFERS_NO_QUALITY_PROFILE);
  // The retry the refusal must not take away: a reader can add a profile in Whisparr and press again.
  expect(rendered.button?.disabled).toBe(false);
});

/**
 * The page-load half of the same discarded sentence.
 *
 * The menu is available, so nothing is stated in the control's own name, and the reason the read
 * carried would otherwise reach nobody.
 */
test("a read answering that the instance declined states that beneath the control", async () => {
  readAnswer = () => Promise.resolve(view({ refusal: "instanceRefused" }));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));

  expect(document.body.textContent).toContain(INSTANCE_REFUSED);
  expect(rendered.button?.disabled).toBe(false);

  rendered.button?.click();
  await sleep(COMMIT_MS);
  expect(rendered.menu()).not.toBeNull();
});

test("a read that failed says only that, with no refusal sentence beside it", async () => {
  readAnswer = () => Promise.reject(new Error("nothing answered"));

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));

  expect(rendered.button?.getAttribute("aria-label")).toBe(
    `${MONITOR_IN_WHISPARR}, ${MONITORING_COULD_NOT_BE_READ}`,
  );
  expect(document.body.textContent).not.toContain(INSTANCE_REFUSED);
});

// The answer that is not an entity view, so the refusal is read off a second route's own type.
test("a refused add all missing states the reason, on an answer carrying no view at all", async () => {
  readAnswer = () =>
    Promise.resolve(
      view({ monitored: true, capabilities: ["monitorStudio", "registerMissingScenes"] }),
    );
  actionAnswer = () => Promise.resolve({ jobId: null, refusal: "instanceRefused" });

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  rendered.button?.click();
  await sleep(COMMIT_MS);

  const row = rendered
    .rows()
    .find((entry) => (entry.getAttribute("title") ?? "").startsWith(ACTION_ADD_ALL_MISSING));
  row?.click();
  await sleep(COMMIT_MS);

  expect(document.body.querySelector('[role="status"]')?.textContent).toBe(INSTANCE_REFUSED);
  expect(rendered.button?.disabled).toBe(false);
});

test("add all missing is pressed at its own route on a generation holding the capability", async () => {
  readAnswer = () =>
    Promise.resolve(
      view({ monitored: true, capabilities: ["monitorStudio", "registerMissingScenes"] }),
    );
  actionAnswer = () => Promise.resolve({ jobId: "job-1", refusal: "none" });

  const rendered = await render(createElement(WhisparrStudioActions, { studio: { id: 1 } }));
  rendered.button?.click();
  await sleep(COMMIT_MS);

  const row = rendered
    .rows()
    .find((entry) => (entry.getAttribute("title") ?? "").startsWith(ACTION_ADD_ALL_MISSING));

  expect(row).toBeDefined();
  expect(row?.disabled).toBe(false);

  row?.click();
  await sleep(COMMIT_MS);

  const posted = sent.filter((call) => call.method === "POST");
  expect(posted).toHaveLength(1);
  expect(posted[0].path).toBe(
    "/extensions/com.alextomas955.whisparrsync/entity/studio/1/add-all-missing",
  );
});
