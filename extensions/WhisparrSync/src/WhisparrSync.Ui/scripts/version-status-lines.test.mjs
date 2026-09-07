/**
 * Rendered-output contract for VersionStatusLines. The runner compiles the component and passes the compiled
 * module path in VERSION_STATUS_LINES_MODULE; this renders that exact artifact with react-dom/server and
 * asserts on the MARKUP, because the defect these cases exist for is invisible to a substring check.
 *
 * The failure mode: two bare `StatusText` (a `<span>`) as direct children of a `space-y-1` container. Vertical
 * rhythm is a margin-top on a following sibling, which an inline box ignores, so the two spans reflowed onto
 * one line and rendered "…verified just nowWhisparr last reachable just now" — a run-on with no separator.
 * A test asserting only that both sentences are present passes on that broken output, which is exactly the
 * degenerate shape these cases are written to avoid.
 */
import test from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

const { VersionStatusLines } = await import(process.env.VERSION_STATUS_LINES_MODULE);

// Ticks composed the way the product composes them, rather than written as an 18-digit literal a double
// cannot hold exactly. The exact wording each tick formats to is relativeTime's contract, not this file's.
const TICKS_AT_EPOCH = 62135596800000 * 10000;
const ticksMinutesAgo = (minutes) => TICKS_AT_EPOCH + (Date.now() - minutes * 60000) * 10000;

const SOME_TICKS = ticksMinutesAgo(17);
const OLDER_TICKS = ticksMinutesAgo(12);

function render(props) {
  return renderToStaticMarkup(createElement(VersionStatusLines, props));
}

/**
 * The text a user reads — `innerText`, not `innerHTML` with the tags stripped. The distinction IS the test:
 * a block-level boundary becomes a line break, while adjacent inline spans produce no boundary at all, so the
 * broken markup collapses to one run-on string here exactly as it did on screen. Stripping every tag
 * unconditionally would report the fixed markup as concatenated too and the case would assert nothing.
 */
function textOf(markup) {
  return markup
    .replace(/<\/(div|p|li|h[1-6])>/g, "\n")
    .replace(/<[^>]+>/g, "")
    .trim();
}

test("the two facts render in SEPARATE block-level wrappers, not as adjacent inline spans", () => {
  const markup = render({
    detectedVersion: "2.2.0.108",
    versionVerifiedTicks: SOME_TICKS,
    whisparrLastReachableTicks: OLDER_TICKS,
  });
  // Each StatusText span must sit inside its own div; two spans as direct siblings is the bug.
  assert.match(markup, /<div><span[^>]*>Whisparr reported version/);
  assert.match(markup, /<div><span[^>]*>Whisparr last reachable/);
  assert.equal(
    /<\/span><span/.test(markup),
    false,
    "two spans are adjacent siblings — they will render on one line as a run-on",
  );
});

test("the rendered text never joins the two sentences without a boundary", () => {
  const markup = render({
    detectedVersion: "3.3.4.794",
    versionVerifiedTicks: SOME_TICKS,
    whisparrLastReachableTicks: OLDER_TICKS,
  });
  const text = textOf(markup);
  // The live defect, verbatim: a relative-time phrase butted straight against the next sentence.
  assert.equal(
    /(ago|now)Whisparr/.test(text),
    false,
    `the two lines are concatenated: ${JSON.stringify(text)}`,
  );
  // And the specific pair a user actually saw.
  assert.equal(text.includes("nowWhisparr"), false);
  assert.equal(text.includes("agoWhisparr"), false);
});

test("the lines stay separate when BOTH ticks read the same age", () => {
  // The hardest case to see by eye: with both phrases identical a reader skims past the join, and on a live
  // instance whose two clocks are both fresh the run-on reads as "…just nowWhisparr last reachable just now".
  const now = ticksMinutesAgo(0);
  const text = textOf(
    render({
      detectedVersion: "3.3.4.794",
      versionVerifiedTicks: now,
      whisparrLastReachableTicks: now,
    }),
  );
  assert.equal(text.includes("nowWhisparr"), false);
  assert.equal(text.split("\n").filter((l) => l.trim().length > 0).length, 2);
});

test("both facts are still present — the separation fix did not drop one", () => {
  const text = textOf(
    render({
      detectedVersion: "2.2.0.108",
      versionVerifiedTicks: SOME_TICKS,
      whisparrLastReachableTicks: OLDER_TICKS,
    }),
  );
  assert.match(text, /Whisparr reported version 2\.2\.0\.108/);
  assert.match(text, /Whisparr last reachable/);
});

test("an empty version reads as not-yet-verified, never as a failed detection", () => {
  const text = textOf(
    render({
      detectedVersion: "",
      versionVerifiedTicks: null,
      whisparrLastReachableTicks: OLDER_TICKS,
    }),
  );
  assert.match(text, /Version not verified yet/);
  assert.equal(text.includes("Whisparr reported version"), false);
  // Still two lines, so the not-verified copy cannot run into the reachability line either.
  assert.equal(/(ago|now)Whisparr/.test(text), false);
});

test("a null verified tick renders the version with no time rather than the epoch", () => {
  const text = textOf(
    render({
      detectedVersion: "2.2.0.108",
      versionVerifiedTicks: null,
      whisparrLastReachableTicks: null,
    }),
  );
  assert.match(text, /Whisparr reported version 2\.2\.0\.108/);
  assert.equal(text.includes("verified"), false);
  assert.equal(text.includes("0001"), false);
});

test("a null reachability tick renders no reachability line at all", () => {
  const markup = render({
    detectedVersion: "2.2.0.108",
    versionVerifiedTicks: SOME_TICKS,
    whisparrLastReachableTicks: null,
  });
  assert.equal(textOf(markup).includes("last reachable"), false);
  assert.equal((markup.match(/<span/g) ?? []).length, 1);
});

test("the two ticks are rendered from their own sources — they are not the same clock", () => {
  const a = textOf(
    render({
      detectedVersion: "2.2.0.108",
      versionVerifiedTicks: SOME_TICKS,
      whisparrLastReachableTicks: OLDER_TICKS,
    }),
  );
  const b = textOf(
    render({
      detectedVersion: "2.2.0.108",
      versionVerifiedTicks: OLDER_TICKS,
      whisparrLastReachableTicks: SOME_TICKS,
    }),
  );
  // Swapping the two inputs must change the output; a component wiring both lines to one tick would not.
  assert.notEqual(a, b);
});
