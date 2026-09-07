/**
 * Rendered-output contract for the Missing tab's refusal line. The runner compiles MissingSceneCard.tsx with its
 * whole import closure and passes the compiled card's path in MISSING_CARD_REFUSAL_MODULE; this renders that exact
 * artifact through react-dom/server and asserts on the text a user reads.
 *
 * WHOLE LINES, never containment. The defect these cases exist for is a refusal that renders nothing at all, and
 * its near neighbour is a refusal that renders run-on against the line beside it — both halves of a run-on contain
 * their own substrings, so a containment assertion passes on the broken output.
 *
 * It also calls the composition DIRECTLY, from a real ApiError carrying the status and body the server sends. The
 * import-free wording gate cannot: it is deliberately blind to the SDK's error type, so without this arm the
 * narrowing that keeps a raw body out of rendered text would only ever be exercised through a string this file
 * passed in by hand.
 *
 * The configuration-health hook reads a module-level store whose server snapshot is a frozen still-loading state,
 * so under the server renderer no prop combination can reach the guarded state. The loader below serves a stand-in
 * for that one module, which makes the pre-click configuration state reachable offline; what it substitutes is the
 * store's FETCHING, never the components' rendering rule, which is the thing under test.
 */
import test from "node:test";
import assert from "node:assert/strict";
import { registerHooks } from "node:module";

/** The configuration fact the stand-in store reports for the render in progress. */
const CONFIG_LOADING = { missingRequiredOptions: [], loading: true };
globalThis.__whisparrConfigHealth = CONFIG_LOADING;

// The vendored host SDK ships bundler-shaped ESM: its dist/index.js imports "./define", "./api" and "./hooks"
// with no extension, which Node's own ESM resolver rejects. The gate loads the real shipped SDK (identity of
// ApiError is what the narrowing under test compares against), so the extension is supplied at resolve time and
// only for specifiers whose parent is inside that package.
registerHooks({
  resolve(specifier, context, nextResolve) {
    const parent = context.parentURL ?? "";
    if (
      parent.includes("/@cove/extension-sdk/dist/") &&
      specifier.startsWith(".") &&
      !/\.[cm]?js$/.test(specifier)
    ) {
      return nextResolve(`${specifier}.js`, context);
    }
    return nextResolve(specifier, context);
  },
  load(url, context, nextLoad) {
    if (url.endsWith("/configHealthStore.js")) {
      return {
        format: "module",
        shortCircuit: true,
        source: "export function useConfigHealth() { return globalThis.__whisparrConfigHealth; }",
      };
    }
    return nextLoad(url, context);
  },
});

const cardModuleUrl = process.env.MISSING_CARD_REFUSAL_MODULE;
const { MissingSceneCard } = await import(cardModuleUrl);
const { mutationFailureLine } = await import(
  new URL("../common/lib/useWhisparrMutation.js", cardModuleUrl).href
);
const { MissingSelectionBar } = await import(
  new URL("./MissingSelectionBar.js", cardModuleUrl).href
);
const { ApiError } = await import("@cove/extension-sdk");
const { createElement } = await import("react");
const { renderToStaticMarkup } = await import("react-dom/server");

// The verb phrase the tab hands the composition for a per-card Monitor, and the sentence the server's own
// configuration refusal selects. Both are pinned as literals: this file is the tie between the shipped
// wording and the line the live driver splits and compares.
const MONITOR_LABEL = "mark this scene wanted";
// WhisparrMissingTab's ACTION_LABELS, verbatim — one per mutating call site on the tab. Every phrase is pinned
// here because a phrase that named no verb, or carried a spaced em dash of its own, would break the split the
// live driver performs on the rendered line.
const CARD_LABELS = {
  monitor: MONITOR_LABEL,
  unmonitor: "remove this scene from the wanted list",
  search: "search for this scene now",
};
const BAR_LABELS = {
  bulkMonitor: "mark the selected scenes wanted",
  bulkUnmonitor: "remove the selected scenes from the wanted list",
  bulkSearch: "search for the selected scenes now",
  monitorAll: "mark every missing scene wanted",
};
const CONFIG_SENTENCE =
  "Set the Whisparr URL in Whisparr Sync settings (Connection) before acting — without it, every Whisparr action fails as though Whisparr were not running.";
const GENERIC_SENTENCE = "Something went wrong. Try again in a moment.";
const MONITOR_REFUSAL_LINE = `Couldn't ${MONITOR_LABEL} — ${CONFIG_SENTENCE}`;
// SEARCH_NOT_ADDED_COPY verbatim: a 200 that reported nothing to search for is an OUTCOME, and the case below
// pins that it never reads as a failure-verb line.
const SEARCH_OUTCOME_LINE =
  "Whisparr has no entry for this scene yet, so there is nothing to search for — mark it wanted first.";

const CONFIG_INCOMPLETE_BODY = JSON.stringify({
  code: "CONFIG_INCOMPLETE",
  options: ["baseUrl"],
});

const ROW = Object.freeze({
  sourceId: "scene-1",
  title: "A Scene",
  coverUrl: null,
  posterUrl: null,
  releaseDate: null,
  studioName: null,
  overview: null,
  performers: [],
  tags: [],
  wanted: false,
  status: "notAdded",
});

function renderCard(props) {
  return renderToStaticMarkup(
    createElement(MissingSceneCard, {
      row: ROW,
      versionSupported: true,
      versionDisabledTitle: "Currently available on Whisparr v3 (Eros)",
      selected: false,
      selecting: false,
      searching: false,
      onToggleSelect: () => {},
      onMonitor: () => {},
      onUnmonitor: () => {},
      onSearch: () => {},
      ...props,
    }),
  );
}

function renderBar(props) {
  return renderToStaticMarkup(
    createElement(MissingSelectionBar, {
      selectedCount: 2,
      monitoring: false,
      unmonitoring: false,
      searching: false,
      versionSupported: true,
      versionDisabledTitle: "Currently available on Whisparr v3 (Eros)",
      onMonitor: () => {},
      onUnmonitor: () => {},
      onSearch: () => {},
      onSelectAll: () => {},
      onInvert: () => {},
      onClear: () => {},
      ...props,
    }),
  );
}

/** The five entities react-dom/server escapes; a rendered apostrophe would otherwise defeat whole-line equality. */
function decode(text) {
  return text
    .replaceAll("&#x27;", "'")
    .replaceAll("&#39;", "'")
    .replaceAll("&quot;", '"')
    .replaceAll("&lt;", "<")
    .replaceAll("&gt;", ">")
    .replaceAll("&amp;", "&");
}

/**
 * The text a user reads — block-level boundaries become line breaks, adjacent inline spans do not. Stripping
 * every tag unconditionally would report a run-on as two clean lines and the cases would assert nothing.
 */
function linesOf(markup) {
  return decode(markup.replace(/<\/(div|p|li|h[1-6])>/g, "\n").replace(/<[^>]+>/g, ""))
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

/** The text of each `role="status"` region, in document order — the exact DOM node the contract names. */
function statusLines(markup) {
  return [...markup.matchAll(/<div role="status"[^>]*>(.*?)<\/div>/g)].map((m) =>
    decode(m[1].replace(/<[^>]+>/g, "")).trim(),
  );
}

/**
 * The adjacent-span check the card cases use does NOT transfer to the bar: three `<span title>` wrappers sit side
 * by side there on purpose, because a disabled button fires no hover and cannot carry its own tooltip.
 *
 * The selection bar is a WRAPPING FLEX ROW, so `linesOf` cannot answer "on its own line" there: a document-order
 * line break comes from a closing block tag, while separation inside a flex row comes from the child's basis. The
 * two things this asserts instead are the ones that actually decide it — the region is a block child of the bar,
 * and it takes the full basis that gives it a row beneath the controls.
 */
function barStatusRowsOwnARow(markup) {
  const rows = [...markup.matchAll(/<div role="status"([^>]*)>/g)].map((m) => m[1]);
  return rows.length > 0 && rows.every((attrs) => /class="[^"]*\bw-full\b/.test(attrs));
}

const failureVerbLines = (lines) => lines.filter((line) => line.startsWith("Couldn't "));

/** Render with the stand-in store reporting a known-unmet setting, then put it back. */
function withUnmetSetting(keys, render) {
  globalThis.__whisparrConfigHealth = { missingRequiredOptions: keys, loading: false };
  try {
    return render();
  } finally {
    globalThis.__whisparrConfigHealth = CONFIG_LOADING;
  }
}

const occurrences = (markup, text) => markup.split(text).length - 1;

/** Every `aria-label` in document order, decoded. */
function accessibleNames(markup) {
  return [...markup.matchAll(/aria-label="([^"]*)"/g)].map((m) => decode(m[1]));
}

const nameStartingWith = (markup, stem) =>
  accessibleNames(markup).find((name) => name.startsWith(stem)) ?? null;

// The full sentence the connection-global fact is stated with, and the short requirement one control carries.
const UNMET_SENTENCE = CONFIG_SENTENCE;
const UNMET_REQUIREMENT = "Needs the Whisparr URL setting (Connection)";

test("an unmet setting puts NO configuration line on a card — the requirement rides its Monitor", () => {
  const markup = withUnmetSetting(["baseUrl"], () => renderCard({}));
  assert.deepEqual(statusLines(markup), []);
  assert.equal(
    occurrences(markup, UNMET_SENTENCE),
    0,
    "the connection-global sentence must not render once per card",
  );
  assert.equal(
    nameStartingWith(markup, "Mark this scene wanted"),
    `Mark this scene wanted${" — "}${UNMET_REQUIREMENT}`,
  );
  assert.equal(markup.includes(`title="${UNMET_REQUIREMENT}"`), true);
});

test("a card whose configuration is complete is untouched by the guard", () => {
  const markup = renderCard({});
  assert.deepEqual(statusLines(markup), []);
  assert.equal(nameStartingWith(markup, "Mark this scene wanted"), "Mark this scene wanted");
  assert.equal(occurrences(markup, UNMET_REQUIREMENT), 0);
});

test("a card on a generation without per-scene monitor states the capability reason, not the setting", () => {
  const capability = "Currently available on Whisparr v3 (Eros)";
  const markup = withUnmetSetting(["baseUrl"], () =>
    renderCard({ versionSupported: false }),
  );
  assert.equal(
    nameStartingWith(markup, "Mark this scene wanted"),
    `Mark this scene wanted${" — "}${capability}`,
  );
  assert.equal(occurrences(markup, UNMET_REQUIREMENT), 0);
});

test("the selection bar states the full sentence exactly ONCE, selection or none", () => {
  for (const selectedCount of [2, 0]) {
    const markup = withUnmetSetting(["baseUrl"], () => renderBar({ selectedCount }));
    assert.equal(
      occurrences(markup, UNMET_SENTENCE),
      1,
      `selectedCount ${selectedCount}: the surface statement must render exactly once`,
    );
    assert.equal(barStatusRowsOwnARow(markup), true);
  }
});

test("the bar draws for the configuration statement alone, with no count and no verbs", () => {
  const markup = withUnmetSetting(["baseUrl"], () => renderBar({ selectedCount: 0 }));
  assert.deepEqual(statusLines(markup), [UNMET_SENTENCE]);
  assert.equal(markup.includes("selected"), false);
  assert.equal(markup.includes("Unmonitor"), false);
});

test("the bar's bulk Monitor names itself before its reason, and keeps the reason on hover", () => {
  const markup = withUnmetSetting(["baseUrl"], () => renderBar({}));
  assert.deepEqual(accessibleNames(markup), [`Monitor${" — "}${UNMET_REQUIREMENT}`]);
  assert.equal(markup.includes(`title="${UNMET_REQUIREMENT}"`), true);
  // Unmonitor and Search flip or grab an existing record, so neither is guarded by this setting.
  assert.equal(occurrences(markup, 'disabled=""'), 1);
});

const abstainingCard = () =>
  renderCard({
    row: { ...ROW, status: "unknown" },
    versionSupported: false,
    statusReason: null,
  });

test("a card whose set already states the abstention keeps the label and drops the sentence", () => {
  const markup = abstainingCard();
  // The whole value, because a partial one is exactly the failure this replaces: the label must survive intact.
  assert.equal(occurrences(markup, ">Status unknown</span>"), 1);
  for (const [, value] of markup.matchAll(/title="([^"]*)"/g)) {
    assert.notEqual(
      decode(value),
      "The connected Whisparr can't report a per-scene status here, so Cove doesn't guess.",
      "the per-card abstention sentence survived the set-wide statement",
    );
  }
});

test("the same card still explains every verb the generation refuses", () => {
  const capability = "Currently available on Whisparr v3 (Eros)";
  const markup = abstainingCard();
  assert.equal(
    nameStartingWith(markup, "Mark this scene wanted"),
    `Mark this scene wanted${" — "}${capability}`,
  );
  // Search carries its reason on hover alone; its accessible name appends only a refusal the server returned.
  assert.equal(nameStartingWith(markup, `Search for ${ROW.title}`), `Search for ${ROW.title} now`);
  // Both refused verbs, and nothing else on the card, hold a reason on hover.
  assert.equal(occurrences(markup, `title="${capability}"`), 2);
});

test("a bar on a generation without per-scene monitor states no setting advice, selection or none", () => {
  const capability = "Currently available on Whisparr v3 (Eros)";
  for (const selectedCount of [2, 0]) {
    const markup = withUnmetSetting(["baseUrl"], () =>
      renderBar({ selectedCount, versionSupported: false }),
    );
    assert.equal(
      occurrences(markup, UNMET_SENTENCE),
      0,
      `selectedCount ${selectedCount}: advice a setting cannot act on must not be stated here`,
    );
    assert.equal(occurrences(markup, UNMET_REQUIREMENT), 0);
  }
  // The bar keeps drawing its verbs, and the one the setting also guards still explains itself with the reason
  // that actually won.
  const withSelection = withUnmetSetting(["baseUrl"], () =>
    renderBar({ versionSupported: false }),
  );
  assert.equal(nameStartingWith(withSelection, "Monitor"), `Monitor${" — "}${capability}`);
  assert.equal(withSelection.includes(`title="${capability}"`), true);
});

test("the composition turns the server's own refusal into the whole per-card line", () => {
  const line = mutationFailureLine(
    MONITOR_LABEL,
    "Currently available on Whisparr v3 (Eros)",
    new ApiError(400, CONFIG_INCOMPLETE_BODY, "/discovery/action"),
  );
  assert.equal(line, MONITOR_REFUSAL_LINE);
  // The whole point of the narrowing: neither the status nor the body reaches the rendered text.
  assert.equal(line.includes("400"), false);
  assert.equal(line.includes("CONFIG_INCOMPLETE"), false);
  assert.equal(line.includes("baseUrl"), false);
  assert.equal(line.includes("/discovery/action"), false);
});

test("a caught value that is not an ApiError yields the generic sentence, no property read off it", () => {
  const line = mutationFailureLine(MONITOR_LABEL, "unused", {
    status: 400,
    body: CONFIG_INCOMPLETE_BODY,
  });
  assert.equal(line, `Couldn't ${MONITOR_LABEL} — ${GENERIC_SENTENCE}`);
  assert.equal(
    mutationFailureLine(MONITOR_LABEL, "unused", new Error("boom")),
    `Couldn't ${MONITOR_LABEL} — ${GENERIC_SENTENCE}`,
  );
  assert.equal(mutationFailureLine(MONITOR_LABEL, "unused", "boom").includes("boom"), false);
});

test("a refused card states the whole line as its own line, in its own role=status region", () => {
  const markup = renderCard({ refusalReason: MONITOR_REFUSAL_LINE });
  const lines = linesOf(markup);
  assert.deepEqual(
    statusLines(markup),
    [MONITOR_REFUSAL_LINE],
    `role=status regions: ${JSON.stringify(statusLines(markup))}`,
  );
  assert.equal(
    lines.includes(MONITOR_REFUSAL_LINE),
    true,
    `the whole line is not a line of its own: ${JSON.stringify(lines)}`,
  );
  assert.equal(
    /<\/span><span/.test(markup),
    false,
    "two spans are adjacent siblings — they render on one line as a run-on",
  );
});

test("a refusal and a search outcome are TWO whole lines, exactly one of them a failure-verb line", () => {
  const markup = renderCard({
    refusalReason: MONITOR_REFUSAL_LINE,
    searchReason: SEARCH_OUTCOME_LINE,
  });
  const lines = linesOf(markup);
  assert.deepEqual(
    statusLines(markup),
    [MONITOR_REFUSAL_LINE, SEARCH_OUTCOME_LINE],
    `role=status regions: ${JSON.stringify(statusLines(markup))}`,
  );
  assert.equal(
    failureVerbLines(lines).length,
    1,
    `failure-verb lines: ${JSON.stringify(failureVerbLines(lines))} of ${JSON.stringify(lines)}`,
  );
  assert.equal(
    /<\/span><span/.test(markup),
    false,
    "two spans are adjacent siblings — the refusal and the search line would render as a run-on",
  );
});

test("a card with no refusal carries no failure-verb line at all", () => {
  const lines = linesOf(renderCard({}));
  assert.deepEqual(
    failureVerbLines(lines),
    [],
    `unexpected failure-verb line in ${JSON.stringify(lines)}`,
  );
  assert.deepEqual(statusLines(renderCard({})), []);
});

test("every per-card verb composes its OWN whole line and the card renders it", () => {
  for (const label of Object.values(CARD_LABELS)) {
    const line = mutationFailureLine(
      label,
      "Currently available on Whisparr v3 (Eros)",
      new ApiError(400, CONFIG_INCOMPLETE_BODY, "/discovery/action"),
    );
    assert.equal(line, `Couldn't ${label} — ${CONFIG_SENTENCE}`);
    const markup = renderCard({ refusalReason: line });
    assert.deepEqual(statusLines(markup), [line], `card status for ${label}`);
    assert.equal(/<\/span><span/.test(markup), false, `run-on for ${label}`);
  }
});

test("every bulk verb composes its OWN whole line and the selection bar renders it beneath its verbs", () => {
  for (const label of Object.values(BAR_LABELS)) {
    const line = mutationFailureLine(
      label,
      "Currently available on Whisparr v3 (Eros)",
      new ApiError(400, CONFIG_INCOMPLETE_BODY, "/discovery/action-all"),
    );
    assert.equal(line, `Couldn't ${label} — ${CONFIG_SENTENCE}`);
    const markup = renderBar({ refusalReason: line });
    assert.deepEqual(statusLines(markup), [line], `bar status for ${label}`);
    assert.equal(
      barStatusRowsOwnARow(markup),
      true,
      `the bar's status region shares a row with the verbs for ${label}: ${markup}`,
    );
  }
});

test("a mark-all refusal with nothing selected still states its line, with no count and no verbs", () => {
  // Mark-all takes no selection, so the bar has none to draw; a refusal-only bar is the only shape that can
  // carry the statement at the control's own row.
  const line = `Couldn't ${BAR_LABELS.monitorAll} — ${CONFIG_SENTENCE}`;
  const markup = renderBar({ selectedCount: 0, refusalReason: line });
  assert.deepEqual(statusLines(markup), [line]);
  assert.deepEqual(linesOf(markup), [line]);
  assert.equal(barStatusRowsOwnARow(markup), true);
  assert.equal(markup.includes("selected"), false);
  assert.equal(markup.includes("Monitor"), false);
  // And with no refusal there is still nothing at all.
  assert.equal(renderBar({ selectedCount: 0 }), "");
});

test("two refused rows put two DIFFERENT lines on two cards, never one line on both", () => {
  const first = `Couldn't ${CARD_LABELS.monitor} — ${CONFIG_SENTENCE}`;
  const second = `Couldn't ${CARD_LABELS.unmonitor} — ${CONFIG_SENTENCE}`;
  const rowA = renderCard({ refusalReason: first });
  const rowB = renderCard({
    row: { ...ROW, sourceId: "scene-2", title: "Another Scene" },
    refusalReason: second,
  });
  assert.deepEqual(statusLines(rowA), [first]);
  assert.deepEqual(statusLines(rowB), [second]);
  assert.notEqual(first, second);
  assert.equal(statusLines(rowA).includes(second), false);
  assert.equal(statusLines(rowB).includes(first), false);
});

test("the search OUTCOME sentence is never a failure-verb line", () => {
  // A 200 reporting nothing to search for and a refused request are different facts; only the second opens with
  // the failure verb, and the card must be able to carry either alone.
  const lines = linesOf(renderCard({ searchReason: SEARCH_OUTCOME_LINE }));
  assert.deepEqual(statusLines(renderCard({ searchReason: SEARCH_OUTCOME_LINE })), [
    SEARCH_OUTCOME_LINE,
  ]);
  assert.deepEqual(failureVerbLines(lines), []);
  assert.equal(SEARCH_OUTCOME_LINE.startsWith("Couldn't "), false);
});
