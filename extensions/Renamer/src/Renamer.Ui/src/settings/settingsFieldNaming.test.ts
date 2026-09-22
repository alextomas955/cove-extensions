// @vitest-environment jsdom
/**
 * Two standing invariants over the whole settings panel, asserted against the real sections rendered
 * on the real shared primitives.
 *
 * G1: a label element never forwards a click to a button that is one control among several. A
 * `<label>` activates its first labelable descendant, and `button` is labelable, so a heading over a
 * chip row activates a chip — a silent configuration change, or a deletion, from a click on a word.
 *
 * G2: every text input, select and textarea carries an accessible name, save the one allowance below.
 *
 * Only Cove's own runtime modules stand in, because `@cove/runtime/*` resolves inside a running
 * Cove and nowhere else, so the test projects alias those two names at the shared package's
 * run-time stand-ins. The shared primitives are the subject here, so stubbing them would assert the
 * stub's DOM instead.
 */
import { test, expect } from "vitest";
import { createElement, createRef, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { someOptions } from "./testOptions";
// Reached by path, not through the barrel: a stand-in for a host module is not public surface.
import { HOST_SELECTOR_MARK } from "../../../../../../shared/ui-shared/src/coveRuntimeComponentsStub";
import type { LibraryPathsState, RenamerOptions } from "./options";

import { AdvancedSection } from "./AdvancedSection";
import { DestinationRoutingSection } from "./DestinationRoutingSection";
import { FilenameSection } from "./FilenameSection";
import { TokenSettingsSection } from "./TokenSettingsSection";
import { WhatGetsRenamedSection } from "./WhatGetsRenamedSection";

/**
 * Options seeded so every conditional control is actually on screen. An empty list draws no chip and
 * no button, and a format matching a preset reveals no custom input, so a run against the defaults
 * would pass on a panel where half the controls it claims to cover were never rendered.
 */
function seededOptions(): RenamerOptions {
  const base = someOptions();
  return {
    ...base,
    // Each token group renders only for a template that uses its token.
    filenameTemplate: "$performers $tags $date $duration $title",
    // A separator outside the preset list is what reveals the chip row's custom input.
    performers: {
      ...base.performers,
      separator: " ~ ",
      ignoreGenders: ["Male"],
      genderOrder: ["Female", "Male"],
    },
    tags: { ...base.tags, separator: " ~ ", ignoreGenders: ["Male"], genderOrder: ["Female"] },
    // A non-empty replacement is what puts the segmented control into replace mode, where it reveals
    // its input; a format matching no listed option is what reveals the example select's.
    illegalReplacement: "_",
    spaceReplacement: ".",
    durationFormat: String.raw`ss`,
    associatedExtensions: ["srt"],
    dropOrder: ["tags"],
    requiredFields: ["title"],
    articles: ["the"],
    excludePaths: [{ pattern: "/tmp", isRegex: false }],
    fieldReplacers: [{ targetToken: "title", find: "a", replace: "b" }],
    pathDestinations: [
      { pattern: "/tmp", dest: { root: "/media", template: "$studio" }, isRegex: false },
    ],
    tagDestinations: { "7": { root: "/media", template: "$studio" } },
    unorganizedDestination: { root: "/media", template: "$studio" },
  };
}

const LIBRARY: LibraryPathsState = { paths: ["/media"], loading: false, failed: false };

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Waits for a rendered condition rather than a span, so a slow machine does not decide the result. */
async function waitFor(what: string, holds: () => boolean, timeoutMs = 4000) {
  const deadline = Date.now() + timeoutMs;
  while (!holds()) {
    if (Date.now() > deadline) throw new Error(`timed out waiting for ${what}`);
    await sleep(10);
  }
}

/** Every section that renders a field, each in a block naming it, in one document. */
function Panel() {
  const options = seededOptions();
  const noop = () => undefined;
  const activeTemplateRef: { current: "filename" | "folder" } = { current: "filename" };
  const section = (name: string, node: ReactNode) =>
    createElement("div", { key: name, "data-section": name }, node);

  return createElement(
    "div",
    null,
    section(
      "WhatGetsRenamedSection",
      createElement(WhatGetsRenamedSection, { options, set: noop }),
    ),
    section(
      "FilenameSection",
      createElement(FilenameSection, {
        options,
        set: noop,
        insertToken: noop,
        filenameRef: createRef<HTMLInputElement>(),
        folderRef: createRef<HTMLInputElement>(),
        activeTemplateRef,
        emptySamples: [],
        recoveredFromBadBlob: false,
        pendingNameMigration: false,
        pendingDestinationMigration: false,
        library: LIBRARY,
      }),
    ),
    section(
      "TokenSettingsSection",
      createElement(TokenSettingsSection, {
        options,
        set: noop,
        setMulti: noop,
        insertToken: noop,
      }),
    ),
    section(
      "DestinationRoutingSection",
      createElement(DestinationRoutingSection, { options, set: noop, library: LIBRARY }),
    ),
    section("AdvancedSection", createElement(AdvancedSection, { options, set: noop })),
  );
}

async function renderPanel() {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(Panel));
  await waitFor("the panel to mount", () => container.querySelector("[data-section]") !== null);

  // Every Advanced panel is collapsed by default and renders no children until opened, so the fields
  // inside one are not on screen to be checked.
  const collapsed = () => [
    ...container.querySelectorAll<HTMLElement>('button[aria-expanded="false"]'),
  ];
  for (const header of collapsed()) header.click();
  await waitFor("every collapsible panel to open", () => collapsed().length === 0);

  return {
    container,
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

/**
 * The HTML labelable elements. `button` is on this list, which is the whole reason a heading over a
 * chip row activates a chip.
 */
const LABELABLE = 'button, input:not([type="hidden"]), select, textarea, meter, output, progress';

/** What clicking a label activates: its `for` target when it has one, else its first labelable child. */
function activationTarget(label: HTMLLabelElement): Element | null {
  const forId = label.getAttribute("for");
  if (forId !== null) return label.ownerDocument.getElementById(forId);
  return label.querySelector(LABELABLE);
}

/** The heading a user reads for this label — the Field's own text, not the control's contents. */
function headingOf(label: Element): string {
  const first = label.firstElementChild;
  if (first?.tagName === "SPAN") {
    const own = first.textContent.trim();
    if (own) return own;
  }
  return label.textContent.trim().split("\n")[0].slice(0, 60) || "(no text)";
}

function sectionOf(el: Element): string {
  return el.closest("[data-section]")?.getAttribute("data-section") ?? "(no section)";
}

/**
 * The nearest card heading above an element. Two fields share the label "Separator" — one per token
 * group — so a report naming only the section and the label cannot say which of them is broken.
 */
function cardOf(el: Element): string {
  for (let scope = el.parentElement; scope !== null; scope = scope.parentElement) {
    const heading = scope.querySelector("h3, h4");
    const above =
      heading !== null &&
      Boolean(heading.compareDocumentPosition(el) & Node.DOCUMENT_POSITION_FOLLOWING);
    if (above) return heading.textContent.trim();
    if (scope.hasAttribute("data-section")) break;
  }
  return "(no card)";
}

function accessibleName(el: Element): string {
  const own = (el.getAttribute("aria-label") ?? "").trim();
  if (own) return own;

  const labelledBy = el.getAttribute("aria-labelledby");
  if (labelledBy) {
    const text = labelledBy
      .split(/\s+/)
      .map((id) => el.ownerDocument.getElementById(id)?.textContent ?? "")
      .join(" ")
      .trim();
    if (text) return text;
  }

  if (el.id) {
    const paired = el.ownerDocument.querySelector(`label[for="${CSS.escape(el.id)}"]`);
    const text = (paired?.textContent ?? "").trim();
    if (text) return text;
  }

  return (el.closest("label")?.textContent ?? "").trim();
}

/**
 * The one shape where a label activating a button is right: the label wraps that button and nothing
 * else labelable, so it names exactly the control it operates. A switch is the case in hand.
 */
function namesOnlyThatControl(label: HTMLLabelElement, target: Element): boolean {
  const labelable = [...label.querySelectorAll(LABELABLE)];
  return labelable.length === 1 && labelable[0] === target;
}

/**
 * The gap this allowance records: the host draws the entity selector's input, and on the 1.4.1 floor
 * this extension declares it exposes neither an id to point `htmlFor` at nor a name hook, so that
 * input carries no accessible name of its own. Its block is named instead, and a group name does not
 * name a nested textbox. A Cove release exposing a name hook on the selector closes it.
 */
const HOST_SELECTOR_INPUT = {
  reason: "the host entity selector's own search input, unnamed on the declared Cove floor",
  matches: (el: Element) => el.closest(`[${HOST_SELECTOR_MARK}]`) !== null,
};

test("no label forwards a click to a button that is one control among several", async () => {
  const view = await renderPanel();

  const allowed: string[] = [];
  const violations: string[] = [];
  for (const label of view.container.querySelectorAll("label")) {
    const target = activationTarget(label);
    if (target?.tagName !== "BUTTON") continue;
    if (namesOnlyThatControl(label, target)) {
      allowed.push(`${sectionOf(label)} / ${headingOf(label)}`);
      continue;
    }
    violations.push(`${sectionOf(label)} / ${cardOf(label)} / "${headingOf(label)}"`);
  }

  // A label naming exactly one switch is correct, so the carve-out above must still describe
  // something real; if it matches nothing it has outlived its subject.
  expect(allowed.length, "the single-control carve-out matched nothing").toBeGreaterThan(0);

  // Lead with the count on a line of its own, so a run is read for how many sites are broken rather
  // than for which words happen to appear: two of them share the label text "Separator".
  console.log(`G1 violations: ${String(violations.length)}`);
  for (const v of violations) console.log(`  ${v}`);

  expect(violations, `G1 violations: ${String(violations.length)}`).toEqual([]);

  view.unmount();
});

test("every text input, select and textarea is named, save the recorded host-selector gap", async () => {
  const view = await renderPanel();

  const allowed: Element[] = [];
  const unnamed: string[] = [];
  for (const control of view.container.querySelectorAll<HTMLElement>(
    'input:not([type="hidden"]):not([type="checkbox"]), select, textarea',
  )) {
    if (accessibleName(control)) continue;
    if (HOST_SELECTOR_INPUT.matches(control)) {
      allowed.push(control);
      continue;
    }
    unnamed.push(`${sectionOf(control)} / <${control.tagName.toLowerCase()}>`);
  }

  // An allowance matching nothing means the gap closed and the exemption outlived it.
  expect(allowed.length, HOST_SELECTOR_INPUT.reason).toBeGreaterThan(0);

  console.log(`G2 unnamed controls: ${String(unnamed.length)}`);
  for (const u of unnamed) console.log(`  ${u}`);

  expect(unnamed, `G2 unnamed controls: ${String(unnamed.length)}`).toEqual([]);

  view.unmount();
});

/**
 * The selector `extensions/Renamer/e2e/tests/options-migration.spec.mjs` addresses a field block
 * with, copied verbatim. Both shapes are candidates and a candidate containing another is not one.
 */
const E2E_FIELD_SELECTOR =
  'label:not(:has(label, [role="group"])), [role="group"]:not(:has(label, [role="group"]))';

/** The e2e's `field(scope, label)`: candidates under a scope whose text holds the label. */
function e2eField(scope: Element, label: string): Element[] {
  return [...scope.querySelectorAll(E2E_FIELD_SELECTOR)].filter((el) =>
    el.textContent.includes(label),
  );
}

/** The e2e's `groupCard`/`toggleCard`: a heading, then the three hops up to the card root. */
function e2eCard(scope: Element, title: string): Element {
  const heading = [...scope.querySelectorAll("h3")].find((h) => h.textContent.trim() === title);
  const root = heading?.parentElement?.parentElement?.parentElement;
  if (!root) throw new Error(`no card titled ${title}`);
  return root;
}

/** A group's own name. A group names the block; it never inherits one from an ancestor. */
function groupName(el: Element): string {
  const own = (el.getAttribute("aria-label") ?? "").trim();
  if (own) return own;
  return (el.getAttribute("aria-labelledby") ?? "")
    .split(/\s+/)
    .filter(Boolean)
    .map((id) => el.ownerDocument.getElementById(id)?.textContent ?? "")
    .join(" ")
    .trim();
}

test("every field block the e2e addresses still resolves to exactly one node", async () => {
  const view = await renderPanel();
  const page = view.container;

  // Each entry is one `field(scope, label)` call in options-migration.spec.mjs, in its own scope.
  const addressed: [string, Element, string][] = [
    ["Tags / Only include", e2eCard(page, "Tags"), "Only include"],
    ["Tags / Never include", e2eCard(page, "Tags"), "Never include"],
    ["Performers / Only include", e2eCard(page, "Performers"), "Only include"],
    ["Performers / Never include", e2eCard(page, "Performers"), "Never include"],
    ["page / Exclude by tag", page, "Exclude by tag"],
    [
      "Source-path destinations / Source path",
      e2eCard(page, "Source-path destinations"),
      "Source path",
    ],
    ["Source-path destinations / Under", e2eCard(page, "Source-path destinations"), "Under"],
    [
      "Source-path destinations / Folder template",
      e2eCard(page, "Source-path destinations"),
      "Folder template",
    ],
  ];

  const counts = Object.fromEntries(
    addressed.map(([name, scope, label]) => [name, e2eField(scope, label).length]),
  );
  expect(counts).toEqual(Object.fromEntries(addressed.map(([name]) => [name, 1])));

  view.unmount();
});

test("no field block's group name collides with a per-kind row's", async () => {
  const view = await renderPanel();

  // The four labels `renamer-settings-page.mjs` passes to `getByRole("group", { name })`, transcribed
  // from that page object rather than read from KIND_LABELS: a rename there must fail here, not agree.
  const KIND_ROW_NAMES = ["Videos", "Images", "Audio", "Text documents"];

  const names = [...view.container.querySelectorAll('[role="group"]')].map(groupName);
  const perKind = Object.fromEntries(
    KIND_ROW_NAMES.map((kind) => [kind, names.filter((n) => n === kind).length]),
  );
  expect(perKind).toEqual(Object.fromEntries(KIND_ROW_NAMES.map((kind) => [kind, 1])));

  view.unmount();
});
