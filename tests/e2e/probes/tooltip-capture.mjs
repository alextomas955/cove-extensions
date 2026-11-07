// Whether a disabled control's native tooltip is actually painted, answered by an image.
//
// A native tooltip is browser chrome: absent from the DOM, absent from the accessibility tree, and
// absent from Playwright's own screenshot, which captures the page's compositor output only. So this
// drives a REAL cursor with `[System.Windows.Forms.Cursor]::Position` and grabs the screen at the OS
// level through `support/capture-screen.ps1`, headed, because a headless Chromium composites no
// platform widget at all.
//
// Two markup shapes are captured in one pass, both carrying the same title: the bare
// `<button disabled title>` that `monitoring/EntityMonitorButton.tsx` ships, and the wrapping
// `<span title>` that `common/ui/DisabledControl.tsx` uses. If the shipped shape paints nothing, the
// alternative is then decided by evidence already in hand.
//
// `--self-test` is the negative control and must pass first: two captures with the cursor moved
// between them and NO hover held, through the same exclusion code path, must report almost no change.
// Without it the exclusion rectangles are a mechanism nobody checked, and an unexcluded diff reports
// a change on every run including one where no tooltip painted.
//
// This file lives beside `tests/`, never inside it: Playwright globs a project's test directory, so
// a probe placed there would be swept into a suite run. It is not part of `npm test` and gates no
// merge.
import { spawnSync } from "node:child_process";
import { mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import process from "node:process";

import { chromium } from "@playwright/test";

const CAPTURE_SCRIPT = join(import.meta.dirname, "support", "capture-screen.ps1");
const REPO_ROOT = join(import.meta.dirname, "..", "..", "..");
const COPY_SOURCE = join(
  REPO_ROOT,
  "extensions/WhisparrSync/src/WhisparrSync.Ui/src/common/ui/copy.ts",
);

// Longer than the two seconds the verification report's own test description names, so a tooltip
// that is merely slow is not read as a tooltip that never painted.
const HOVER_DWELL_MS = 2700;
const PARK_DWELL_MS = 900;

// The cursor's own pixels are the one thing guaranteed to differ between the two captures, since it
// moved. 48 device pixels square is the floor a Windows cursor fits inside; a tooltip opens about 35
// device pixels below the hotspot, so this box does not swallow the thing being measured.
const CURSOR_BOX = 48;

// A changed region at least this large is the automatable presence signal. Smaller than the tooltip's
// own text row, so a real tooltip cannot slip under it.
const SIGNAL_WIDTH = 60;
const SIGNAL_HEIGHT = 16;

const SELF_TEST_RATIO = 0.001;

// Device pixels trimmed off each edge of the browser's page area before any rectangle is compared.
const PAGE_INSET = 40;

// CSS-pixel layout of the two-case page. The two controls sit far apart vertically so one tooltip
// cannot be mistaken for the other, and every parked and self-test position is on blank background
// clear of both.
const LAYOUT = {
  bare: { left: 200, top: 120, park: { x: 1000, y: 450 } },
  wrapped: { left: 200, top: 620, park: { x: 1000, y: 260 } },
  control: { width: 40, height: 40 },
  selfTest: { a: { x: 900, y: 200 }, b: { x: 1240, y: 200 } },
};

function parseArguments(argv) {
  let out = null;
  let selfTest = false;
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === "--out") {
      out = argv[i + 1];
      if (out === undefined) throw new Error("--out needs a directory.");
      i += 1;
    } else if (argv[i] === "--self-test") {
      selfTest = true;
    } else {
      throw new Error(
        `Unknown argument '${argv[i]}'. Usage: tooltip-capture.mjs --out <dir> [--self-test]`,
      );
    }
  }
  if (out === null)
    throw new Error(
      "--out <dir> is required. Usage: tooltip-capture.mjs --out <dir> [--self-test]",
    );
  return { out, selfTest };
}

// The expected string is READ out of the shipped copy module rather than typed here: a hand-typed
// expectation would agree with itself and not with what the control sets.
function expectedTitle() {
  const source = readFileSync(COPY_SOURCE, "utf8");
  const lines = source.split(/\r?\n/);
  const read = (name) => {
    const pattern = new RegExp(`^export const ${name} =\\s*("(?:[^"\\\\]|\\\\.)*")\\s*;`);
    for (let i = 0; i < lines.length; i += 1) {
      const match = pattern.exec(lines[i]);
      if (match !== null) return { value: JSON.parse(match[1]), line: i + 1 };
    }
    throw new Error(`${name} is not a single-line string literal in ${COPY_SOURCE}.`);
  };
  const name = read("MONITOR_IN_WHISPARR");
  const reason = read("CAP_UNAVAILABLE_ON_THIS_GENERATION");
  return {
    // The composition EntityMonitorButton.tsx forms at `spoken`, and the literal
    // EntityMonitorButton.test.ts asserts for the v2-performer case.
    value: `${name.value}, ${reason.value}`,
    from: {
      file: "extensions/WhisparrSync/src/WhisparrSync.Ui/src/common/ui/copy.ts",
      name: `MONITOR_IN_WHISPARR:${name.line}`,
      reason: `CAP_UNAVAILABLE_ON_THIS_GENERATION:${reason.line}`,
      composition:
        "monitoring/EntityMonitorButton.tsx `${name}, ${unavailable}`; asserted in monitoring/EntityMonitorButton.test.ts",
    },
  };
}

// windowsHide keeps the shell's own console window from taking the foreground, which would put a
// window over the browser in the very grab that is supposed to show the browser.
function powershell(args) {
  const result = spawnSync(
    "pwsh",
    ["-NoProfile", "-NonInteractive", "-File", CAPTURE_SCRIPT, ...args],
    { encoding: "utf8", windowsHide: true },
  );
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(
      `capture-screen.ps1 ${args.join(" ")} exited ${result.status}: ${result.stderr}`,
    );
  }
  const line = result.stdout.trim().split(/\r?\n/).filter(Boolean).pop();
  if (line === undefined) throw new Error(`capture-screen.ps1 ${args.join(" ")} printed nothing.`);
  return JSON.parse(line);
}

function capture({ out, moveTo, dwellMs }) {
  const args = ["-Out", out, "-DwellMs", String(dwellMs)];
  if (moveTo) args.push("-MoveTo", `${Math.round(moveTo.x)},${Math.round(moveTo.y)}`);
  return powershell(args);
}

function rectangleText(rectangle) {
  return `${Math.round(rectangle.x)},${Math.round(rectangle.y)},${Math.round(rectangle.width)},${Math.round(rectangle.height)}`;
}

function diff({ a, b, rect, exclude }) {
  const args = ["-Diff", "-A", a, "-B", b, "-Rect", rectangleText(rect)];
  // Every exclusion travels in one parameter, joined by ";": PowerShell reports a repeated
  // parameter as a binding error, and it splits a comma-bearing value before the script sees it.
  if (exclude.length > 0) args.push("-Exclude", exclude.map(rectangleText).join(";"));
  return powershell(args);
}

function cursorBox(position) {
  return {
    x: Math.round(position.x) - CURSOR_BOX / 2,
    y: Math.round(position.y) - CURSOR_BOX / 2,
    width: CURSOR_BOX,
    height: CURSOR_BOX,
  };
}

function intersect(rect, bound) {
  const x = Math.max(rect.x, bound.x);
  const y = Math.max(rect.y, bound.y);
  const right = Math.min(rect.x + rect.width, bound.x + bound.width);
  const bottom = Math.min(rect.y + rect.height, bound.y + bound.height);
  return { x, y, width: Math.max(0, right - x), height: Math.max(0, bottom - y) };
}

function pageHtml(title) {
  return `<!doctype html>
<html><head><meta charset="utf-8"><title>disabled control tooltip</title>
<style>
  html, body { margin: 0; padding: 0; background: #ffffff; height: 100%; }
  .control { width: ${LAYOUT.control.width}px; height: ${LAYOUT.control.height}px;
             border: 1px solid #333; background: #eee; box-sizing: border-box; }
  #bare { position: absolute; left: ${LAYOUT.bare.left}px; top: ${LAYOUT.bare.top}px; }
  #wrap { position: absolute; left: ${LAYOUT.wrapped.left}px; top: ${LAYOUT.wrapped.top}px;
          display: inline-flex; }
</style></head>
<body>
  <button id="bare" class="control" disabled aria-label="${title}" title="${title}"></button>
  <span id="wrap" title="${title}"><button id="wrapped" class="control" disabled aria-label="${title}"></button></span>
  <script>
    window.__moves = [];
    // Recorded so the coordinate transform is calibrated against what the page actually received
    // from the real cursor, rather than trusted from an arithmetic nobody checked.
    addEventListener("mousemove", (event) => {
      const target = event.target;
      window.__moves.push({
        clientX: event.clientX,
        clientY: event.clientY,
        screenX: event.screenX,
        screenY: event.screenY,
        target: target instanceof Element ? (target.id || target.tagName) : null,
        title: target instanceof Element ? (target.getAttribute("title") ?? null) : null,
      });
      if (window.__moves.length > 400) window.__moves.splice(0, 200);
    }, true);
  </script>
</body></html>`;
}

async function readFrame(page) {
  return page.evaluate(() => ({
    screenX: window.screenX,
    screenY: window.screenY,
    outerHeight: window.outerHeight,
    innerHeight: window.innerHeight,
    outerWidth: window.outerWidth,
    innerWidth: window.innerWidth,
    devicePixelRatio: window.devicePixelRatio,
    screenWidth: window.screen.width,
    screenHeight: window.screen.height,
  }));
}

// The transform the page's own numbers imply: CSS pixels to device pixels, with the chrome above the
// viewport taken as the difference between the outer and inner heights.
function assumedTransform(frame) {
  return {
    scaleX: frame.devicePixelRatio,
    scaleY: frame.devicePixelRatio,
    offsetX: frame.screenX * frame.devicePixelRatio,
    offsetY: (frame.screenY + (frame.outerHeight - frame.innerHeight)) * frame.devicePixelRatio,
  };
}

function applyTransform(transform, point) {
  return {
    x: transform.offsetX + transform.scaleX * point.x,
    y: transform.offsetY + transform.scaleY * point.y,
  };
}

async function lastMove(page) {
  return page.evaluate(() => window.__moves.at(-1) ?? null);
}

async function clearMoves(page) {
  await page.evaluate(() => {
    window.__moves.length = 0;
  });
}

// Two known screen points, and what the page reported receiving at each, solve the transform exactly.
// An assumed scale that is off by the display's scale factor is the one error that would put every
// hover on empty background and report an honest "nothing painted" about the wrong pixels.
async function calibrate(page, frame, outDir) {
  const assumed = assumedTransform(frame);
  const probes = [
    { css: { x: 600, y: 300 }, screen: applyTransform(assumed, { x: 600, y: 300 }) },
    { css: { x: 1100, y: 700 }, screen: applyTransform(assumed, { x: 1100, y: 700 }) },
  ];

  const observed = [];
  for (const probe of probes) {
    await clearMoves(page);
    capture({ out: join(outDir, "calibrate.png"), moveTo: probe.screen, dwellMs: 250 });
    observed.push({ ...probe, received: await lastMove(page) });
  }

  const usable = observed.every((entry) => entry.received !== null);
  if (!usable) {
    return { assumed, observed, solved: assumed, calibrated: false, agreesWithAssumed: false };
  }

  const [first, second] = observed;
  const scaleX =
    (second.screen.x - first.screen.x) / (second.received.clientX - first.received.clientX);
  const scaleY =
    (second.screen.y - first.screen.y) / (second.received.clientY - first.received.clientY);
  const solved = {
    scaleX,
    scaleY,
    offsetX: first.screen.x - scaleX * first.received.clientX,
    offsetY: first.screen.y - scaleY * first.received.clientY,
  };
  const agrees =
    Math.abs(solved.scaleX - assumed.scaleX) < 0.02 &&
    Math.abs(solved.scaleY - assumed.scaleY) < 0.02 &&
    Math.abs(solved.offsetX - assumed.offsetX) < 4 &&
    Math.abs(solved.offsetY - assumed.offsetY) < 4;
  return { assumed, observed, solved, calibrated: true, agreesWithAssumed: agrees };
}

// Every comparison is restricted to this, so a window behind the browser cannot contribute a
// changed pixel. Inset, because the transform is solved from two points inside the viewport and
// carries a few pixels of error at its edges: an over-reaching rectangle takes in whatever window
// sits under the browser's own bottom edge, and that window's pixels then read as something the
// page painted.
function pageAreaRect(frame, transform) {
  const topLeft = applyTransform(transform, { x: 0, y: 0 });
  return {
    x: topLeft.x + PAGE_INSET,
    y: topLeft.y + PAGE_INSET,
    width: frame.innerWidth * transform.scaleX - 2 * PAGE_INSET,
    height: frame.innerHeight * transform.scaleY - 2 * PAGE_INSET,
  };
}

async function launch() {
  const browser = await chromium.launch({
    headless: false,
    args: ["--window-position=0,0", "--window-size=1400,900"],
  });
  return browser;
}

async function runSelfTest(page, frame, transform, outDir) {
  const a = applyTransform(transform, LAYOUT.selfTest.a);
  const b = applyTransform(transform, LAYOUT.selfTest.b);
  const imageA = join(outDir, "selftest-a.png");
  const imageB = join(outDir, "selftest-b.png");

  const capturedA = capture({ out: imageA, moveTo: a, dwellMs: HOVER_DWELL_MS });
  const capturedB = capture({ out: imageB, moveTo: b, dwellMs: HOVER_DWELL_MS });

  const boxes = [cursorBox(a), cursorBox(b)];
  const spread = {
    x: Math.min(a.x, b.x) - 200,
    y: Math.min(a.y, b.y) - 200,
    width: Math.abs(b.x - a.x) + 400,
    height: 400,
  };
  const rect = intersect(spread, pageAreaRect(frame, transform));

  const withExclusions = diff({ a: imageA, b: imageB, rect, exclude: boxes });
  const withoutExclusions = diff({ a: imageA, b: imageB, rect, exclude: [] });

  return {
    positions: { a, b, separation: Math.round(Math.hypot(b.x - a.x, b.y - a.y)) },
    exclusionBoxes: boxes,
    dwellMs: HOVER_DWELL_MS,
    images: { a: imageA, b: imageB },
    bytes: { a: capturedA.bytes, b: capturedB.bytes },
    uniform: { a: capturedA.uniform, b: capturedB.uniform },
    cursorReadBack: { a: capturedA.cursor, b: capturedB.cursor },
    searchedRect: withExclusions.searchedRect,
    searchedPixels: withExclusions.searchedPixels,
    changedPixels: withExclusions.changedPixels,
    boundingBox: withExclusions.boundingBox,
    ratio: withExclusions.changedPixels / withExclusions.searchedPixels,
    threshold: SELF_TEST_RATIO,
    // The control on the control: with the cursor's own boxes NOT excluded, this is what the same
    // pair reports. It says whether the exclusion had anything to suppress in this environment.
    withoutExclusions: {
      changedPixels: withoutExclusions.changedPixels,
      boundingBox: withoutExclusions.boundingBox,
    },
  };
}

async function runCase(page, frame, transform, outDir, name, selector) {
  const box = await page.locator(selector).boundingBox();
  if (box === null) throw new Error(`${selector} has no bounding box.`);

  const centre = applyTransform(transform, { x: box.x + box.width / 2, y: box.y + box.height / 2 });
  const park = applyTransform(transform, LAYOUT[name].park);
  const controlRect = {
    ...applyTransform(transform, { x: box.x, y: box.y }),
    width: box.width * transform.scaleX,
    height: box.height * transform.scaleY,
  };

  const away = join(outDir, `away-${name}.png`);
  const hover = join(outDir, `hover-${name}.png`);

  await clearMoves(page);
  const capturedAway = capture({ out: away, moveTo: park, dwellMs: PARK_DWELL_MS });
  const parkedOn = await lastMove(page);

  await clearMoves(page);
  const capturedHover = capture({ out: hover, moveTo: centre, dwellMs: HOVER_DWELL_MS });
  const hoveredOn = await lastMove(page);

  // Down and right of the control, wide enough to hold the whole sentence: the composed string is
  // over sixty characters, so a 400-pixel window would clip its own subject.
  const wanted = {
    x: controlRect.x,
    y: controlRect.y,
    width: controlRect.width + 1400,
    height: controlRect.height + 500,
  };
  const rect = intersect(wanted, pageAreaRect(frame, transform));
  const boxes = [cursorBox(park), cursorBox(centre)];

  const measured = diff({ a: away, b: hover, rect, exclude: boxes });
  const withoutExclusions = diff({ a: away, b: hover, rect, exclude: [] });
  // If something painted, excluding exactly it must take the count to nothing. That is the one check
  // that says the exclusion rectangles are applied where they are aimed, on this run's own pixels.
  const exclusionControl =
    measured.boundingBox === null
      ? null
      : diff({
          a: away,
          b: hover,
          rect,
          exclude: [
            ...boxes,
            {
              x: measured.boundingBox.x - 2,
              y: measured.boundingBox.y - 2,
              width: measured.boundingBox.width + 4,
              height: measured.boundingBox.height + 4,
            },
          ],
        });

  const painted =
    measured.boundingBox !== null &&
    measured.boundingBox.width >= SIGNAL_WIDTH &&
    measured.boundingBox.height >= SIGNAL_HEIGHT;

  return {
    case: name,
    selector,
    controlScreenRect: controlRect,
    cursor: { park, hover: centre },
    exclusionBoxes: boxes,
    dwellMs: { park: PARK_DWELL_MS, hover: HOVER_DWELL_MS },
    images: { away, hover },
    bytes: { away: capturedAway.bytes, hover: capturedHover.bytes },
    uniform: { away: capturedAway.uniform, hover: capturedHover.uniform },
    cursorReadBack: { away: capturedAway.cursor, hover: capturedHover.cursor },
    // What the page itself received from the real cursor. A hover that landed on BODY rather than on
    // the control measures blank background, and no conclusion about a tooltip follows from it.
    pageSaw: { parked: parkedOn, hovered: hoveredOn },
    landedOnControl:
      hoveredOn !== null &&
      (hoveredOn.target === selector.replace("#", "") || hoveredOn.target === "wrap"),
    searchedRect: measured.searchedRect,
    searchedPixels: measured.searchedPixels,
    changedPixels: measured.changedPixels,
    boundingBox: measured.boundingBox,
    signal: { width: SIGNAL_WIDTH, height: SIGNAL_HEIGHT, painted },
    withoutExclusions: {
      changedPixels: withoutExclusions.changedPixels,
      boundingBox: withoutExclusions.boundingBox,
    },
    exclusionControl:
      exclusionControl === null
        ? null
        : {
            changedPixels: exclusionControl.changedPixels,
            boundingBox: exclusionControl.boundingBox,
          },
  };
}

async function main() {
  const { out, selfTest } = parseArguments(process.argv.slice(2));
  mkdirSync(out, { recursive: true });

  const title = expectedTitle();
  const browser = await launch();
  const record = {
    mode: selfTest ? "self-test" : "capture",
    startedAt: new Date().toISOString(),
    browser: { name: browser.browserType().name(), version: browser.version() },
    expected: title,
  };

  try {
    const page = await browser.newPage({ viewport: { width: 1360, height: 860 } });
    await page.setContent(pageHtml(title.value));
    await page.bringToFront();
    await page.waitForTimeout(500);

    const frame = await readFrame(page);
    const calibration = await calibrate(page, frame, out);
    const transform = calibration.solved;
    record.frame = frame;
    record.calibration = calibration;
    record.transform = transform;
    record.pageArea = pageAreaRect(frame, transform);

    if (selfTest) {
      record.selfTest = await runSelfTest(page, frame, transform, out);
    } else {
      record.cases = [
        await runCase(page, frame, transform, out, "bare", "#bare"),
        await runCase(page, frame, transform, out, "wrapped", "#wrapped"),
      ];
    }
  } finally {
    await browser.close();
  }

  const recordPath = join(out, selfTest ? "tooltip-capture-selftest.json" : "tooltip-capture.json");
  writeFileSync(recordPath, `${JSON.stringify(record, null, 2)}\n`, "utf8");
  console.log(`record: ${recordPath}`);

  if (selfTest) {
    const { changedPixels, searchedPixels, ratio, withoutExclusions } = record.selfTest;
    console.log(
      `self-test: ${changedPixels} of ${searchedPixels} pixels changed (${(ratio * 100).toFixed(4)}%), threshold ${(SELF_TEST_RATIO * 100).toFixed(1)}%; without the exclusions ${withoutExclusions.changedPixels}`,
    );
    if (record.selfTest.uniform.a || record.selfTest.uniform.b) {
      throw new Error(
        "a self-test capture is a uniform single colour, so the grab captured a blank or locked session.",
      );
    }
    if (ratio >= SELF_TEST_RATIO) {
      throw new Error(
        `the negative self-test changed ${changedPixels} of ${searchedPixels} pixels (${(ratio * 100).toFixed(4)}%), at or above the ${(SELF_TEST_RATIO * 100).toFixed(1)}% threshold: the exclusion rectangles are not doing their job, or something else on screen moved. Every hover diff is meaningless until this passes.`,
      );
    }
    return;
  }

  for (const entry of record.cases) {
    console.log(
      `${entry.case}: ${entry.signal.painted ? "a changed region" : "NO changed region"} ${entry.boundingBox === null ? "(nothing changed)" : `${entry.boundingBox.width}x${entry.boundingBox.height} at ${entry.boundingBox.x},${entry.boundingBox.y}`}, ${entry.changedPixels} pixels of ${entry.searchedPixels}; the page saw the hover land on ${entry.pageSaw.hovered?.target ?? "nothing"}`,
    );
    for (const image of [entry.images.away, entry.images.hover]) {
      if (statSync(image).size === 0) throw new Error(`${image} is 0 bytes, so the grab failed.`);
    }
  }
  if (record.cases.length < 2) throw new Error("fewer than two cases were captured.");
}

main().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
