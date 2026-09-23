// @vitest-environment jsdom
import { afterEach, beforeEach, expect, test, vi } from "vitest";
import { act, createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import type { MissingCard as MissingCardView, MissingSceneState } from "../wire/api";
import type { CardActionState } from "./missingCardLogic";

// The shared primitives stand in, because their `react` and `lucide-react` imports resolve only
// inside a consuming bundle. Each stand-in reproduces the real element's content in order, which
// is what the chip's glyph and label are read from below.
vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    StatusPill: (props: { variant: string; icon?: ReactNode; children?: ReactNode }) =>
      h("span", { "data-pill": props.variant }, props.icon, props.children),
    StatusText: (props: { kind: string; children?: ReactNode }) =>
      h("span", { "data-status": props.kind }, props.children),
  };
});

const { MissingCard } = await import("./MissingCard");

const TITLE = "A Scene The Library Does Not Hold";
const SCENE_ID = "3ac7838f-0e3f-4f19-9d2b-7c1a5b9e2f10";

const SCENE: MissingCardView = {
  providerSceneId: SCENE_ID,
  title: TITLE,
  releaseDate: "2024-03-01",
  coverUrl: "https://example.invalid/cover.jpg",
  sceneUrl: "https://a.source.invalid/scenes/3ac7838f",
  studioName: "A Studio",
  description: "What the source says about it.",
  performers: [],
  tags: [],
  performerCount: 0,
  tagCount: 0,
  state: "notAdded",
};

const AT_REST: CardActionState = {
  inFlight: null,
  optimistic: null,
  refusal: null,
  failed: false,
};

const teardowns: (() => void)[] = [];

beforeEach(() => {
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
});

afterEach(async () => {
  await act(() => {
    while (teardowns.length > 0) teardowns.pop()?.();
    return Promise.resolve();
  });
  window.history.replaceState(null, "", "/");
  vi.unstubAllGlobals();
});

async function mountCard(
  over: Partial<MissingCardView> = {},
  props: {
    action?: CardActionState;
    onMonitor?: (providerSceneId: string) => void;
    onSearch?: (providerSceneId: string) => void;
    onToggleSelect?: (() => void) | undefined;
    selected?: boolean;
    verbs?: boolean;
  } = {},
) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  const offered = props.verbs ?? true;
  await act(() => {
    root.render(
      createElement(MissingCard, {
        card: { ...SCENE, ...over },
        selected: props.selected ?? false,
        onToggleSelect: props.onToggleSelect,
        action: props.action ?? AT_REST,
        onMonitor: offered ? (props.onMonitor ?? (() => undefined)) : undefined,
        onSearch: offered ? (props.onSearch ?? (() => undefined)) : undefined,
      }),
    );
    return Promise.resolve();
  });
  teardowns.push(() => {
    root.unmount();
    container.remove();
  });

  return container;
}

function named(container: Element, name: string) {
  return [...container.querySelectorAll("button")].find(
    (candidate) => candidate.getAttribute("aria-label") === name,
  );
}

async function press(control: Element | undefined) {
  if (control === undefined) throw new Error("the card drew no such control");
  await act(() => {
    control.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    return Promise.resolve();
  });
}

test("each verb names itself and the scene it acts on", async () => {
  const container = await mountCard();

  const names = [...container.querySelectorAll("button")].map((control) =>
    control.getAttribute("aria-label"),
  );

  expect(names).toContain(`Monitor ${TITLE} in Whisparr`);
  expect(names).toContain(`Search Whisparr for ${TITLE}`);
});

test("pressing a verb names this card's scene to it, and leaves the other unpressed", async () => {
  const monitored: string[] = [];
  const searched: string[] = [];
  const container = await mountCard(
    {},
    { onMonitor: (id) => monitored.push(id), onSearch: (id) => searched.push(id) },
  );

  await press(named(container, `Monitor ${TITLE} in Whisparr`));
  expect(monitored).toEqual([SCENE_ID]);
  expect(searched).toEqual([]);

  await press(named(container, `Search Whisparr for ${TITLE}`));
  expect(searched).toEqual([SCENE_ID]);
  expect(monitored).toEqual([SCENE_ID]);
});

test("a verb whose request is in the air cannot be pressed again, and says why", async () => {
  const monitored: string[] = [];
  const container = await mountCard(
    {},
    {
      action: { ...AT_REST, inFlight: "monitor" },
      onMonitor: (id) => monitored.push(id),
    },
  );

  const waiting = named(
    container,
    `Monitor ${TITLE} in Whisparr. Waiting for Whisparr to answer the last thing you asked for.`,
  );
  expect(waiting?.disabled).toBe(true);

  await press(waiting);
  expect(monitored).toEqual([]);
});

test("the other verb is unaffected while one is in the air", async () => {
  const searched: string[] = [];
  const container = await mountCard(
    {},
    { action: { ...AT_REST, inFlight: "monitor" }, onSearch: (id) => searched.push(id) },
  );

  const search = named(container, `Search Whisparr for ${TITLE}`);
  expect(search?.disabled).toBe(false);

  await press(search);
  expect(searched).toEqual([SCENE_ID]);
});

test("nothing on a settled card is dimmed", async () => {
  const container = await mountCard();

  expect([...container.querySelectorAll("button")].filter((control) => control.disabled)).toEqual(
    [],
  );
});

// The chip's tint, mark and label as one string. The mark is read off the rendered shape: two
// states that share a tint would pass a text-only check while drawing the same shape.
function chip(container: Element) {
  const drawn = container.querySelector("[data-pill]");
  const mark = drawn?.querySelector("svg")?.getAttribute("class")?.split(" ")[1] ?? "no mark";
  return `${drawn?.getAttribute("data-pill") ?? "none"}:${mark}:${drawn?.textContent ?? ""}`;
}

test("the status reads in the shared vocabulary, mark and tint and all", async () => {
  const drawn: string[] = [];
  for (const state of ["notAdded", "unmonitored", "statusUnknown"] satisfies MissingSceneState[]) {
    drawn.push(chip(await mountCard({ state })));
  }

  expect(drawn).toEqual([
    "cyan:lucide-circle-dashed:Not added",
    "gray:lucide-bookmark-minus:Unmonitored",
    "amber:lucide-circle-question-mark:Status unknown",
  ]);
});

// The state's own word, not a word of this tab's own: "Wanted" is the name of a list the
// instance keeps rather than a state a scene is in.
test("a monitored scene is called Monitored, under the vocabulary's own mark and tint", async () => {
  expect(chip(await mountCard({ state: "monitored" }))).toBe("green:lucide-bookmark:Monitored");
});

test("a refused press states the reason beneath the verbs, in the tone the reason carries", async () => {
  const container = await mountCard(
    {},
    { action: { ...AT_REST, refusal: "whisparrHasNoEntryForScene" } },
  );

  const stated = container.querySelector("[data-status]");
  expect(stated?.textContent).toBe(
    "Whisparr has no entry for this scene yet, so there is nothing to search for - monitor it first.",
  );
  expect(stated?.getAttribute("data-status")).toBe("muted");
});

test("a description from the source is drawn as text, never as markup", async () => {
  const container = await mountCard({ description: "<b>bold</b> and <script>run()</script>" });

  expect(container.querySelector("b")).toBeNull();
  expect(container.querySelector("script")).toBeNull();
  expect(container.textContent).toContain("<b>bold</b> and <script>run()</script>");
});

test("the selection control names the gesture it offers, and is absent where none is", async () => {
  const withNone = await mountCard();
  expect(named(withNone, "Select scene")).toBeUndefined();

  const offered = await mountCard({}, { onToggleSelect: () => undefined });
  expect(named(offered, "Select scene")).toBeDefined();

  const ticked = await mountCard({}, { onToggleSelect: () => undefined, selected: true });
  const control = named(ticked, "Deselect scene");
  expect(control?.getAttribute("aria-pressed")).toBe("true");
});

test("no verb is drawn at all where the surface offers none", async () => {
  const container = await mountCard({}, { verbs: false });

  expect([...container.querySelectorAll("button")]).toEqual([]);
});

test("the cover and the title lead to the address the source named, in a new tab", async () => {
  const container = await mountCard();

  const links = [...container.querySelectorAll("a")];
  expect(links.length).toBe(2);
  for (const link of links) {
    expect(link.getAttribute("href")).toBe("https://a.source.invalid/scenes/3ac7838f");
    expect(link.getAttribute("target")).toBe("_blank");
    expect(link.getAttribute("rel")).toBe("noreferrer");
    expect(link.getAttribute("aria-label")).toBe(`Open ${TITLE} at your metadata source`);
  }

  expect(links[0]?.querySelector("img")?.getAttribute("alt")).toBe(TITLE);
  expect(links[1]?.textContent).toBe(TITLE);
});

test("a card whose source named no address is not a link", async () => {
  const container = await mountCard({ sceneUrl: null });

  expect([...container.querySelectorAll("a")]).toEqual([]);
  expect(container.textContent).toContain(TITLE);
});

// A control inside the link would follow the link on every press.
test("no control the card offers sits inside the link", async () => {
  const container = await mountCard({}, { onToggleSelect: () => undefined });

  const inside = [...container.querySelectorAll("button")].filter(
    (control) => control.closest("a") !== null,
  );

  expect(inside).toEqual([]);
});

// jsdom applies no host stylesheet, so what a control carries is read from its class list. The
// host emits `focus:ring-*` and not `focus-visible:ring-*`.
test("every control takes the focus ring the host stylesheet emits", async () => {
  const container = await mountCard({}, { onToggleSelect: () => undefined });

  const controls = [...container.querySelectorAll("button, a")];
  expect(controls.length).toBe(5);
  for (const control of controls) {
    expect(control.className, control.getAttribute("aria-label") ?? "").toContain(
      "focus:ring-2 focus:ring-accent",
    );
    expect(control.className).not.toContain("focus-visible:ring");
  }
});
