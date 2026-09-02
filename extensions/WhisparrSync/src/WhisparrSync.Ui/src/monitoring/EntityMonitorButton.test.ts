// @vitest-environment jsdom
/**
 * What the control paints at each stage of its own read, and what one press sends.
 *
 * A DOM is needed because the properties under test are about the rendered element rather than about
 * a value a helper returns: a mark drawn before the read answers is a wrong-generation colour on
 * screen, and a body carrying an identifier is a request the server is obliged to ignore.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is flushed by waiting rather than by wrapping. The shared
 * primitives and the host's authenticated fetch both stand in, because each resolves only inside a
 * consuming bundle.
 */
import { test, expect, vi } from "vitest";
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
  method: string | undefined;
  body: string | undefined;
}

const sent: Sent[] = [];

// Each answer is a function called at request time rather than a promise created ahead of one. A
// promise that settles before anything reads it is reported as unhandled, whatever the code under
// test then does with it.
type Answer = () => Promise<unknown>;
let readAnswer: Answer = () => Promise.resolve(null);
let actionAnswer: Answer = () => Promise.resolve(null);

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {
    constructor(
      public status: number,
      public body: string,
    ) {
      super(`${String(status)} ${body}`);
    }
  },
  requestJson: (route: string, options?: RequestInit) => {
    sent.push({
      path: route,
      method: options?.method,
      body: typeof options?.body === "string" ? options.body : undefined,
    });
    return route.endsWith("/monitor") ? actionAnswer() : readAnswer();
  },
}));

const { EntityMonitorButton } = await import("./EntityMonitorButton");
const { MONITOR_IN_WHISPARR, MONITORED_IN_WHISPARR } = await import("../common/ui/copy");

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

function view(monitored: boolean) {
  return {
    kind: "studio",
    generation: "v3",
    monitored,
    refusal: "none",
    capabilities: ["monitorStudio"],
  };
}

async function render(node: ReactNode) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(node);
  await sleep(COMMIT_MS);
  return {
    container,
    button: container.querySelector("button"),
    marks: () => container.querySelectorAll("svg").length,
    teardown: () => {
      root.unmount();
      container.remove();
    },
  };
}

function reset() {
  sent.length = 0;
  readAnswer = () => Promise.resolve(null);
  actionAnswer = () => Promise.resolve(null);
}

test("the first frame is a bordered shell with no mark at all", async () => {
  reset();
  // A read that never settles, which is the frame under test.
  readAnswer = () =>
    new Promise(() => {
      // intentionally never resolved
    });

  const rendered = await render(createElement(EntityMonitorButton, { studio: { id: 1 } }));

  expect(rendered.button).not.toBeNull();
  expect(rendered.marks()).toBe(0);
  expect(rendered.button?.getAttribute("aria-label")).toBe(MONITOR_IN_WHISPARR);
  rendered.teardown();
});

test("a read answering not-monitored paints the mark and no tick", async () => {
  reset();
  readAnswer = () => Promise.resolve(view(false));

  const rendered = await render(createElement(EntityMonitorButton, { studio: { id: 1 } }));

  expect(rendered.marks()).toBe(1);
  expect(hasClass(rendered.button, "border-accent")).toBe(false);
  expect(rendered.button?.getAttribute("aria-label")).toBe(MONITOR_IN_WHISPARR);
  rendered.teardown();
});

test("a read answering monitored paints the mark, the tick and the border", async () => {
  reset();
  readAnswer = () => Promise.resolve(view(true));

  const rendered = await render(createElement(EntityMonitorButton, { studio: { id: 1 } }));

  // The mark and the tick: the state lives on the border plus the tick, never on the mark, which is
  // a filled two-tone disc that cannot inherit a colour.
  expect(rendered.marks()).toBe(2);
  expect(hasClass(rendered.button, "border-accent")).toBe(true);
  expect(rendered.button?.getAttribute("aria-label")).toBe(MONITORED_IN_WHISPARR);
  rendered.teardown();
});

test("the read names the mounted entity and asks for its monitoring", async () => {
  reset();
  readAnswer = () => Promise.resolve(view(false));

  const rendered = await render(createElement(EntityMonitorButton, { studio: { id: 42 } }));

  expect(sent).toHaveLength(1);
  expect(sent[0].path).toBe(
    "/extensions/com.alextomas955.whisparrsync/entity/studio/42/monitoring",
  );
  expect(sent[0].method).toBeUndefined();
  rendered.teardown();
});

test("one press posts the scope and no identifier of any kind", async () => {
  reset();
  readAnswer = () => Promise.resolve(view(false));
  actionAnswer = () => Promise.resolve(view(true));

  const rendered = await render(createElement(EntityMonitorButton, { studio: { id: 42 } }));
  rendered.button?.click();
  await sleep(COMMIT_MS);

  const posted = sent.filter((call) => call.method === "POST");
  expect(posted).toHaveLength(1);
  expect(posted[0].path).toBe("/extensions/com.alextomas955.whisparrsync/entity/studio/42/monitor");
  expect(Object.keys(JSON.parse(posted[0].body ?? "{}") as Record<string, unknown>)).toEqual([
    "scope",
  ]);
  expect((JSON.parse(posted[0].body ?? "{}") as { scope: string }).scope).toBe("futureScenes");
  rendered.teardown();
});

test("the answer to a press is what the control then paints", async () => {
  reset();
  readAnswer = () => Promise.resolve(view(false));
  actionAnswer = () => Promise.resolve(view(true));

  const rendered = await render(createElement(EntityMonitorButton, { studio: { id: 1 } }));
  expect(rendered.marks()).toBe(1);

  rendered.button?.click();
  await sleep(COMMIT_MS);

  expect(rendered.marks()).toBe(2);
  expect(rendered.button?.getAttribute("aria-label")).toBe(MONITORED_IN_WHISPARR);
  rendered.teardown();
});

test("a press in flight leaves the control unpressable, so one entity cannot get two", async () => {
  reset();
  readAnswer = () => Promise.resolve(view(false));
  actionAnswer = () =>
    new Promise(() => {
      // intentionally never resolved
    });

  const rendered = await render(createElement(EntityMonitorButton, { studio: { id: 1 } }));
  rendered.button?.click();
  await sleep(COMMIT_MS);

  expect(rendered.button?.disabled).toBe(true);
  rendered.button?.click();
  await sleep(COMMIT_MS);

  expect(sent.filter((call) => call.method === "POST")).toHaveLength(1);
  rendered.teardown();
});

test("a failed read leaves the shell with no mark rather than a guessed one", async () => {
  reset();
  readAnswer = () => Promise.reject(new Error("nothing answered"));

  const rendered = await render(createElement(EntityMonitorButton, { studio: { id: 1 } }));

  expect(rendered.button).not.toBeNull();
  expect(rendered.marks()).toBe(0);
  rendered.teardown();
});
