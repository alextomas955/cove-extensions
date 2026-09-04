// @vitest-environment jsdom
/**
 * The menu's keyboard behaviour, its roles, and where each sentence lands.
 *
 * The overlay hook is the real one, because the three properties under test are the ones it decides:
 * roving focus finds rows only through their role, its Escape must not reach the page underneath, and
 * the trigger must not count as a click outside. A stand-in for it would assert the stand-in.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is flushed by waiting rather than by wrapping.
 */
import { test, expect, afterEach } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { EntityMonitorMenu } from "./EntityMonitorMenu";
import { monitorMenu } from "./monitorMenuLogic";
import {
  ACTION_ADD_ALL_MISSING,
  ACTION_REFLECT_OWNED,
  ALL_SCENES_MARKS_THE_BACK_CATALOGUE,
  CAP_UNAVAILABLE_ON_THIS_GENERATION,
  SCOPE_ALL_SCENES,
  SCOPE_FUTURE_SCENES,
  SCOPE_IN_FORCE_IS_NOT_REPORTED,
  STOP_MONITORING_IN_WHISPARR,
} from "../common/ui/copy";
import type { EntityMonitoringView } from "../wire/api";

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

/**
 * The name assistive technology composes from an element: its text in document order, minus every
 * subtree removed from the accessibility tree.
 */
function accessibleName(element: Element): string {
  return [...element.childNodes]
    .map((node) => {
      if (node.nodeType === Node.TEXT_NODE) {
        return node.textContent ?? "";
      }
      if (!(node instanceof Element)) {
        return "";
      }
      return node.getAttribute("aria-hidden") === "true" ? "" : accessibleName(node);
    })
    .join("");
}

function viewOf(overrides: Partial<EntityMonitoringView>): EntityMonitoringView {
  return {
    kind: "studio",
    generation: "v3",
    monitored: false,
    refusal: "none",
    capabilities: ["monitorStudio"],
    scope: null,
    ...overrides,
  };
}

interface Mounted {
  container: HTMLElement;
  trigger: HTMLButtonElement;
  panel: () => Element | null;
  rows: () => HTMLButtonElement[];
}

// Torn down centrally rather than at the end of each case. A menu left mounted keeps its own
// document-level key listener, and the next case's arrow keys would then be answered by two menus.
const teardowns: (() => void)[] = [];
afterEach(() => {
  while (teardowns.length > 0) teardowns.pop()?.();
});

async function mount(
  node: (trigger: { current: HTMLElement | null }) => ReactNode,
): Promise<Mounted> {
  const container = document.createElement("div");
  document.body.append(container);

  // A real trigger in the document, because two of the properties under test are about events that
  // start on it.
  const trigger = document.createElement("button");
  trigger.textContent = "trigger";
  container.append(trigger);

  const host = document.createElement("div");
  container.append(host);
  const root = createRoot(host);
  root.render(node({ current: trigger }));
  await sleep(COMMIT_MS);

  teardowns.push(() => {
    root.unmount();
    container.remove();
  });

  return {
    container,
    trigger,
    // Queried from the document, because the menu is portaled out of the host page's own hero: that
    // container clips its overflow, so a panel left in the flow there would be cut off.
    panel: () => document.body.querySelector('[role="menu"]'),
    rows: () => [...document.body.querySelectorAll<HTMLButtonElement>('[role^="menuitem"]')],
  };
}

/** The wrapper each row's button and its sentences share. */
function rowOf(item: Element): HTMLElement {
  const wrapper = item.parentElement;
  if (wrapper === null) throw new Error("a menu row has no wrapper");
  return wrapper;
}

function press(key: string) {
  (document.activeElement ?? document.body).dispatchEvent(
    new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true }),
  );
}

test("every row carries a menu role, and the scope pair carries the radio role and its state", async () => {
  const menu = monitorMenu(viewOf({}), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitor in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  const rows = mounted.rows();
  expect(rows.length).toBe(2);
  expect(rows.map((row) => row.getAttribute("role"))).toEqual(["menuitemradio", "menuitemradio"]);
  expect(rows.map((row) => row.getAttribute("aria-checked"))).toEqual(["true", "false"]);
});

test("marks no radio at all, and says why, when the read reported no scope", async () => {
  const menu = monitorMenu(viewOf({ monitored: true, scope: null }), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitored in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  const rows = mounted.rows();
  expect(rows.length).toBeGreaterThan(0);
  expect(rows.map((row) => row.getAttribute("aria-checked"))).not.toContain("true");
  expect(mounted.panel()?.querySelectorAll('[aria-checked="true"]').length).toBe(0);
  expect(mounted.panel()?.textContent).toContain(SCOPE_IN_FORCE_IS_NOT_REPORTED);
});

test("marks the reported scope, and says nothing about not knowing one", async () => {
  const menu = monitorMenu(viewOf({ monitored: true, scope: "allScenes" }), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitored in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  const checked = [...(mounted.panel()?.querySelectorAll('[aria-checked="true"]') ?? [])];
  expect(checked.map((row) => accessibleName(row))).toEqual([SCOPE_ALL_SCENES]);
  expect(mounted.panel()?.textContent).not.toContain(SCOPE_IN_FORCE_IS_NOT_REPORTED);
});

test("the panel leaves the page's own container, which clips what it holds", async () => {
  const menu = monitorMenu(viewOf({}), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitor in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  const panel = mounted.panel();
  expect(panel).not.toBeNull();
  // In the document rather than beside the control, and positioned against the viewport, so an
  // ancestor hiding its overflow cannot cut the panel off. The placement is on the container the
  // panel and the outcome notice share, which is what stops the two stacking on one another.
  expect(mounted.container.contains(panel)).toBe(false);
  expect(panel?.parentElement?.classList.contains("fixed")).toBe(true);
});

test("the first scope option is Future Scenes and it is the one taken", async () => {
  const menu = monitorMenu(viewOf({}), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitor in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  const rows = mounted.rows();
  expect(accessibleName(rows[0])).toBe(SCOPE_FUTURE_SCENES);
  expect(rows[0].getAttribute("aria-checked")).toBe("true");
  expect(accessibleName(rows[1])).toBe(SCOPE_ALL_SCENES);
});

test("the All-Scenes cost is stated beside the All-Scenes option and beside no other", async () => {
  const menu = monitorMenu(viewOf({}), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitor in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  const carrying = mounted
    .rows()
    .filter((row) => rowOf(row).textContent.includes(ALL_SCENES_MARKS_THE_BACK_CATALOGUE));

  expect(carrying).toHaveLength(1);
  expect(accessibleName(carrying[0])).toBe(SCOPE_ALL_SCENES);
});

test("a sentence beneath a row stays out of that row's accessible name", async () => {
  const menu = monitorMenu(viewOf({}), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitor in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  // The sentence is on screen, and the row is still called only what it is called.
  expect(mounted.panel()?.textContent).toContain(ALL_SCENES_MARKS_THE_BACK_CATALOGUE);
  expect(accessibleName(mounted.rows()[1])).toBe(SCOPE_ALL_SCENES);
});

test("a capability the generation does not hold leaves its row present, dimmed and saying why", async () => {
  const menu = monitorMenu(viewOf({ monitored: true }), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitored in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  const addAllMissing = mounted
    .rows()
    .find((row) => row.textContent.startsWith(ACTION_ADD_ALL_MISSING));
  expect(addAllMissing, "the add-all-missing row is absent rather than dimmed").toBeDefined();
  expect(addAllMissing?.disabled).toBe(true);
  expect(accessibleName(addAllMissing!)).toBe(
    `${ACTION_ADD_ALL_MISSING}${CAP_UNAVAILABLE_ON_THIS_GENERATION}`,
  );
  expect(addAllMissing?.getAttribute("title")).toBe(
    `${ACTION_ADD_ALL_MISSING}, ${CAP_UNAVAILABLE_ON_THIS_GENERATION}`,
  );
});

test("arrow keys walk the rows, so the menu is reachable with no pointer", async () => {
  const menu = monitorMenu(viewOf({ monitored: true }), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitored in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  const rows = mounted.rows();
  expect(rows.length).toBeGreaterThan(2);
  // The hook focuses the first row as the menu opens.
  expect(document.activeElement).toBe(rows[0]);

  press("ArrowDown");
  expect(document.activeElement).toBe(rows[1]);
  press("ArrowDown");
  expect(document.activeElement).toBe(rows[2]);
  press("ArrowUp");
  expect(document.activeElement).toBe(rows[1]);
});

test("Escape closes once, and the page underneath never sees it", async () => {
  let closes = 0;
  const hostSaw: string[] = [];
  const hostHandler = (event: Event) => {
    hostSaw.push((event as KeyboardEvent).key);
  };
  document.addEventListener("keydown", hostHandler);

  const menu = monitorMenu(viewOf({}), false);
  await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitor in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => {
        closes += 1;
      },
    }),
  );

  press("Escape");

  expect(closes).toBe(1);
  expect(hostSaw).toEqual([]);

  document.removeEventListener("keydown", hostHandler);
});

test("a press on the trigger while the menu is open does not count as a click outside", async () => {
  let closes = 0;
  const menu = monitorMenu(viewOf({}), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitor in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => {
        closes += 1;
      },
    }),
  );

  mounted.trigger.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
  expect(closes, "the trigger closed the menu, so its own handler would reopen it").toBe(0);

  // A press anywhere else still closes it, so the exclusion is not the whole rule being off.
  document.body.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
  expect(closes).toBe(1);
});

test("choosing a row hands the caller the item, and the menu decides nothing itself", async () => {
  const chosen: string[] = [];
  const menu = monitorMenu(viewOf({}), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitor in Whisparr",
      triggerRef,
      onSelect: (item) => {
        chosen.push(item.item === "scope" ? item.scope : item.item);
      },
      onClose: () => undefined,
    }),
  );

  mounted.rows()[1].click();
  expect(chosen).toEqual(["allScenes"]);
});

test("an action already on its way disables every row and says what is being waited for", async () => {
  const menu = monitorMenu(viewOf({ monitored: true }), true);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitored in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  const rows = mounted.rows();
  expect(rows.length).toBeGreaterThan(0);
  expect(rows.every((row) => row.disabled)).toBe(true);
  expect(rows.every((row) => (row.getAttribute("title") ?? "").includes(", "))).toBe(true);
});

/**
 * The armed menu of a studio on the older generation, with the capability set that generation
 * actually holds: it registers no missing scene, so that one row is dimmed and two pressable rows
 * follow it.
 */
const V2_STUDIO_CAPABILITIES = [
  "outOfBandCallbackSecret",
  "monitorStudio",
  "reflectOwnedFiles",
  "searchMonitored",
] as const;

/**
 * A menu whose LAST row is dimmed, which needs a capability set no shipped generation answers with:
 * the older one holds the search and the newer one holds all three. Written out because the hook
 * being asserted is shared, and a set the wire admits is a set it will be handed.
 */
const NO_SEARCH_CAPABILITIES = ["monitorPerformer", "reflectOwnedFiles"] as const;

test("the arrow keys pass a disabled row to reach the one after it", async () => {
  const menu = monitorMenu(
    viewOf({ generation: "v2", monitored: true, capabilities: [...V2_STUDIO_CAPABILITIES] }),
    false,
  );
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitored in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  // Add all missing is the dimmed row and Reflect owned is the one after it. A press that stops on
  // the dimmed row leaves the focus there for good, because a disabled button ignores focus() and
  // the next press then reads the same index back.
  const rows = mounted.rows();
  expect(rows.map((row) => row.disabled)).toEqual([false, false, false, true, false, false]);

  press("ArrowDown");
  press("ArrowDown");
  press("ArrowDown");

  expect(accessibleName(document.activeElement!)).toBe(ACTION_REFLECT_OWNED);
});

test("the arrow keys wrap past a disabled last row", async () => {
  const menu = monitorMenu(
    viewOf({
      kind: "performer",
      monitored: true,
      capabilities: [...NO_SEARCH_CAPABILITIES],
    }),
    false,
  );
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitored in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  const rows = mounted.rows();
  expect(rows.map((row) => row.disabled)).toEqual([false, true, false, true]);

  press("ArrowDown");
  expect(accessibleName(document.activeElement!)).toBe(ACTION_REFLECT_OWNED);

  press("ArrowDown");
  expect(accessibleName(document.activeElement!)).toBe(STOP_MONITORING_IN_WHISPARR);
});

test("with every row disabled an arrow press moves nothing and raises nothing", async () => {
  const menu = monitorMenu(viewOf({ monitored: true }), true);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitored in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  expect(mounted.rows().every((row) => row.disabled)).toBe(true);
  const before = document.activeElement;

  expect(() => {
    press("ArrowDown");
  }).not.toThrow();
  expect(document.activeElement).toBe(before);
});

test("the menu panel scrolls rather than clipping", async () => {
  const menu = monitorMenu(viewOf({ monitored: true }), false);
  const mounted = await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitored in Whisparr",
      triggerRef,
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  // A class assertion is weak on its own. What carries the rest of the claim is the check-classes
  // gate, which rejects a class the host does not emit, and the measurement that the host emits
  // overflow-y-auto and no arbitrary max-height value.
  const panel = mounted.panel() as HTMLElement;
  expect(panel.classList.contains("overflow-y-auto")).toBe(true);
  expect(panel.classList.contains("overflow-hidden")).toBe(false);
  expect(panel.parentElement?.style.maxHeight).not.toBe("");
});

test("the room below the trigger bounds the container, and the notice cannot be squeezed out of it", async () => {
  const menu = monitorMenu(viewOf({ monitored: true }), false);
  await mount((triggerRef) =>
    createElement(EntityMonitorMenu, {
      menu,
      label: "Monitored in Whisparr",
      triggerRef,
      notice: "The instance declined the verb.",
      onSelect: () => undefined,
      onClose: () => undefined,
    }),
  );

  const panel = document.body.querySelector<HTMLElement>('[role="menu"]')!;
  const container = panel.parentElement!;
  const notice = document.body.querySelector<HTMLElement>('[role="status"]')!;

  // jsdom reports every element rectangle as zeroes, so the height the notice keeps out of the
  // room is not measurable here. What is measurable is which element carries the bound and which
  // of the two children the column may shrink: the panel may, the notice may not.
  expect(container.style.maxHeight).not.toBe("");
  expect(panel.style.maxHeight).toBe("");
  expect(container.classList.contains("flex")).toBe(true);
  expect(container.classList.contains("flex-col")).toBe(true);
  expect(panel.classList.contains("min-h-0")).toBe(true);
  expect(notice.classList.contains("shrink-0")).toBe(true);
  expect(notice.parentElement).toBe(container);
});
