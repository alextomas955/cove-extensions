// The scene tab on a video detail page, in a real containerized host.
//
// THREE TESTS, AND THE SECOND IS ABOUT AN ABSENCE. On the newer generation the tab states what the
// instance holds, and its four controls are pressed against a real instance. On the older one the
// registration never reaches the manifest, so nothing Whisparr-shaped reaches the page: not the
// control, and not a host wrapper left behind with nothing in it. Those are different DOM states
// and only one of them is what is promised.
//
// WHY THIS SPEC EXISTS. Nothing below the browser can see the whole path this tab needs. Four
// strings bind it across two repositories: the manifest's page type, its tab key, its component
// name and the key the bundle registers a component under. The host resolves the last pair by exact
// string and renders nothing, with no error anywhere, when they differ. Behind the tab sit the read
// route, the identity resolution off the library's own stored row, the read against the instance
// and the projection, and a break in any one of them shows up as a tab that draws nothing.
//
// WHAT IT NEEDS. A Cove container, an installed extension and a real Whisparr instance. No metadata
// credential: the identifier the scene is named by is the library's own stored row, and this surface
// reaches no provider at all.
//
// THE ASSERTED STATE IS THE INSTANCE'S OWN. The seed answers with the entity as the instance
// projects it, and the expected chip label is derived from that answer. A label derived from what
// the seed asked for would agree with itself if the read or the projection dropped the value.
//
// WHY THE TITLE MATTERS. Playwright's --grep matches the concatenated title and never the filename,
// so the describe title below is what selects this file's tests. Every test added here goes inside
// the same block.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";

import {
  test as base,
  connectWhisparr,
  expect,
  EXTENSION_ID,
  seedCoveVideo,
  SETTLE_DWELL_MS,
  STASHDB_ENDPOINT,
  whisparrActivity,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
} from "../lib/whisparr-sync-fixtures.mjs";

// The tab's label, transcribed by hand from the manifest that advertises it. A spec importing the
// same constant the manifest declares would be asserting that a string equals itself.
const TAB_LABEL = "Whisparr";

// The label of the tab's own state row, transcribed the same way.
const STATE_ROW_LABEL = "State";

// The two fact labels a long instance-supplied name can reach, transcribed the same way.
const PROFILE_ROW_LABEL = "Quality profile";
const CUTOFF_ROW_LABEL = "Cutoff";

// The two values in the fact block a reader can make any length, and the only two: a quality
// profile's own name, and the name of the quality group a profile's cutoff resolves to. Every other
// value the block can hold comes out of the instance's own quality vocabulary, which is 29 fixed
// names whose longest is 12 characters - a renamed quality DEFINITION does not reach the name the
// scene's row carries, measured against this pinned build.
const LONG_PROFILE_NAME =
  "A quality profile a reader named at length, eighty-four characters all told, no less";
const LONG_CUTOFF_GROUP_NAME =
  "A quality group a reader named at length, eighty-three characters all told, no less";

// The same two names before a reader lengthens them, short enough to fit their box at the narrowest
// width. The block is measured with these first, on the same scene, so the only thing that differs
// between the two readings is what the instance answers.
const SHORT_PROFILE_NAME = "Short";
const SHORT_CUTOFF_GROUP_NAME = "Small";

// What makes a name long enough to be worth measuring. Asserted rather than trusted: a later edit
// that shortened either name above would leave a value that fits its box, and a measurement of
// truncation on a value that does not overflow reports nothing.
const LONG_NAME_FLOOR = 80;

// The two widths the fact block is measured at: the default the rest of this suite drives, and the
// narrowest a reader is offered.
const WIDE_VIEWPORT = { width: 1280, height: 900 };
const NARROW_VIEWPORT = { width: 360, height: 740 };

// The labels a scene's state can read as, transcribed from the shipped vocabulary.
const MONITORED = "Monitored";
const UNMONITORED = "Unmonitored";
const NOT_ADDED = "Not added";
const EXCLUDED = "Excluded";
const STATUS_UNKNOWN = "Status unknown";

// The control names and the one confirmation sentence, transcribed by hand from the shipped copy. A
// spec importing the constants would be asserting that a string equals itself.
const ADD = "Add to Whisparr";
const MONITOR = "Monitor in Whisparr";
const STOP_MONITORING = "Stop monitoring in Whisparr";
const SEARCH = "Search now";
const EXCLUDE = "Exclude from Whisparr";
const REMOVE_EXCLUSION = "Remove exclusion";
const SEARCH_IS_WITH_WHISPARR = "Whisparr has the search.";

// Each budget names the operation it bounds, so a failure says which one blew it rather than
// reporting the whole test as a timeout naming nothing.
const PAGE_BUDGET_MS = 60_000;
const PAGE_ATTEMPTS = 3;
const TAB_BUDGET_MS = 30_000;
const REGION_BUDGET_MS = 90_000;

const test = base.extend({
  sceneHarness: [
    async ({}, use) => {
      const harness = await startHarness();
      try {
        harness.owner = await harness.bootstrapOwner();
        await harness.installExtension(WHISPARR_SYNC_EXTENSION);
        await use(harness);
      } finally {
        await harness.stop();
      }
    },
    { scope: "test" },
  ],

  // Read through the handle AFTER the install. The install restarts the container, which re-mints
  // the token and can republish the instance on a different host port.
  baseUrl: async ({ sceneHarness }, use) => {
    await use(sceneHarness.baseUrl);
  },
});

/** The tab, by the only name the host draws it under. */
const whisparrTab = (page) => page.getByRole("tab", { name: TAB_LABEL, exact: true }).first();

/** The host's own detail-tab strip, which tells a page that has rendered from one still loading. */
const hostDetailTabs = (page) => page.getByRole("tablist").first();

/**
 * The host's own placeholder for a contributed tab whose component it could not resolve.
 *
 * The empty-versus-absent distinction in its tab form. A registration the host kept and could not
 * fill draws this; a registration that never reached the manifest draws nothing at all.
 */
const unresolvedExtensionComponent = (page) => page.getByText(/Extension component not found/i);

/**
 * Every video-page tab this extension registers in the manifest the browser is served.
 *
 * The DOM cannot report this on its own: which tab strip a detail page draws follows its viewport,
 * so a registration is read from the manifest the page was built from.
 */
async function registeredVideoTabs(api) {
  const manifest = await api.get("/api/extensions/manifest");
  expect(manifest.status, `GET the extension manifest answered ${String(manifest.status)}`).toBe(
    200,
  );
  return (manifest.json?.tabs ?? [])
    .filter((entry) => entry.extensionId === EXTENSION_ID && entry.pageType === "video")
    .map((entry) => entry.key);
}

/**
 * Opens `path`, re-navigating while nothing the caller named has rendered.
 *
 * The host paints its own error boundary in place of a page whose lazily-imported chunk failed to
 * fetch, on the correct URL and indefinitely. Only a fresh navigation recovers it, and the retry is
 * bounded so a permanent failure is not turned into a hung test.
 */
async function visit(page, baseUrl, path, present, label) {
  for (let attempt = 1; attempt <= PAGE_ATTEMPTS; attempt++) {
    await page.goto(`${baseUrl}${path}`);
    const rendered = await present
      .waitFor({ state: "visible", timeout: PAGE_BUDGET_MS })
      .then(() => true)
      .catch(() => false);
    if (rendered) return;
  }
  throw new Error(
    `${label}: nothing rendered at ${baseUrl}${path} across ${PAGE_ATTEMPTS} navigation(s) of ${PAGE_BUDGET_MS}ms each; the page is now at ${page.url()}`,
  );
}

/**
 * Seeds one scene on both sides and answers with the Cove video and the state the INSTANCE reports.
 *
 * The instance's entry is written into its own datastore rather than added through its API: an add
 * resolves the identifier against the vendor's metadata service, so a scene's mere existence would
 * depend on someone else's uptime. The seed reads the row back through the instance's own API, and
 * the expected label is derived from that read.
 */
async function seedScene(coveApi, whisparr, { label, monitored, qualityProfileId }) {
  const remoteId = randomUUID();

  const onInstance = await whisparr.seedEntity("v3", {
    kind: "scene",
    foreignId: remoteId,
    title: `Whisparr ${label}`,
    monitored,
    ...(qualityProfileId === undefined ? {} : { qualityProfileId }),
  });

  const title = `${label} ${remoteId.slice(0, 8)}`;
  const video = await seedCoveVideo(coveApi, {
    title,
    remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId }],
  });

  expect(
    typeof onInstance.monitored,
    `the instance projected the seeded scene "${remoteId}" without a monitored flag, so there is no state to compare the tab against`,
  ).toBe("boolean");

  return { ...video, title, remoteId, statedAs: onInstance.monitored ? MONITORED : UNMONITORED };
}

/**
 * The state label the INSTANCE's own answer puts this scene in, read off the instance every time.
 *
 * Exclusion is read first, matching the shipped vocabulary's own order: a scene that is both
 * excluded and held reads as excluded. Nothing here is derived from what a press asked for, so a
 * tab that painted an optimistic state disagrees with this rather than agreeing with itself.
 */
async function stateOnInstance(api, remoteId) {
  const exclusions = await api.get("/api/v3/exclusions");
  const rows = Array.isArray(exclusions.json) ? exclusions.json : [];
  if (rows.some((row) => row.foreignId === remoteId)) return EXCLUDED;

  const held = await api.get(`/api/v3/movie?stashId=${encodeURIComponent(remoteId)}`);
  const row = Array.isArray(held.json) ? held.json[0] : held.json;
  if (row === undefined || row === null) return NOT_ADDED;
  return typeof row.monitored === "boolean"
    ? row.monitored
      ? MONITORED
      : UNMONITORED
    : STATUS_UNKNOWN;
}

/**
 * One control, found by the accessible name it announces.
 *
 * Anchored at the start rather than matched exactly: a disabled control announces its own name and
 * then its reason, and a reason-only match would pass a control whose name was lost.
 */
const sceneControl = (page, label) => page.getByRole("button", { name: new RegExp(`^${label}`) });

/**
 * The tab's own fact block, found by the label of the row the chip sits in.
 *
 * Scoped rather than page-wide: the host draws pills of its own on a video page, and a bare shape
 * locator would find one of those on a tab that drew nothing.
 */
const sceneFacts = (page) =>
  page.locator("dl").filter({ has: page.getByText(STATE_ROW_LABEL, { exact: true }) });

/**
 * The state chip, located by its shape and asserted on by its words.
 *
 * The chip's label shares its element with an aria-hidden glyph, so the element carries both and an
 * exact-text locator finds nothing. Same reason library-status.spec.mjs locates its card badges
 * this way.
 */
const stateChip = (page) => sceneFacts(page).locator("span.rounded-full");

/** Waits until the tab's own chip reads the state the instance answered, and answers with it. */
async function chipAgreesWithInstance(page, api, remoteId, what) {
  const stated = await stateOnInstance(api, remoteId);
  await expect(
    stateChip(page),
    `${what}: the instance answers "${stated}" for this scene and the tab does not read it, so the tab is painting a state of its own rather than reading one back`,
  ).toHaveText(new RegExp(`${stated}$`), { timeout: REGION_BUDGET_MS });
  return stated;
}

/**
 * A quality profile body carrying the two names a caller decides, composed off a profile the
 * instance itself holds.
 *
 * Composed rather than written out: the profile resource carries a 25-item quality list and a
 * format-item list, and a hand-written body is a transcription of the instance's schema that goes
 * stale under it. Only the two names and the cutoff are this test's.
 *
 * The cutoff is pointed at a GROUP because a group's name is the reader's own. A cutoff resolving to
 * a single quality reads that quality's name out of the instance's fixed vocabulary, which no reader
 * can lengthen.
 */
function profileNamed(template, profileName, groupName) {
  const group = (template.items ?? []).find(
    (item) => Array.isArray(item.items) && item.items.length > 0,
  );
  expect(
    group,
    "the instance's own profile declares no quality group, so there is no name a reader could have made long",
  ).toBeDefined();
  return {
    ...template,
    name: profileName,
    cutoff: group.id,
    items: template.items.map((item) =>
      item.id === group.id ? { ...item, name: groupName, allowed: true } : item,
    ),
  };
}

/**
 * Reads the two names back off the instance.
 *
 * The write's own answer is not the evidence. The names are what the tab is measured against, and a
 * name the instance shortened would make the measurement agree with itself.
 */
async function expectProfileNames(api, id, profileName, groupName) {
  const held = await api.get(`/api/v3/qualityprofile/${String(id)}`);
  expect(held.json?.name, "the instance holds a profile name other than the one it was sent").toBe(
    profileName,
  );
  const cutoffItem = (held.json?.items ?? []).find((item) => item.id === held.json.cutoff);
  expect(
    cutoffItem?.name,
    "the instance's cutoff does not resolve to the named group, so the Cutoff row has no such value to render",
  ).toBe(groupName);
}

/** Puts a quality profile carrying the two names on the instance, and answers with its id. */
async function seedProfile(api, { profileName, groupName }) {
  const profiles = await api.get("/api/v3/qualityprofile");
  expect(
    profiles.status,
    `GET the instance's quality profiles answered ${String(profiles.status)}`,
  ).toBe(200);
  const template = Array.isArray(profiles.json) ? profiles.json[0] : undefined;
  expect(
    template,
    "the instance offers no quality profile to compose one from, so there is nothing to seed",
  ).toBeDefined();

  const body = profileNamed(template, profileName, groupName);
  delete body.id;
  const made = await api.post("/api/v3/qualityprofile", body);
  expect(
    made.status,
    `the instance refused a quality profile named "${profileName}": ${String(made.status)} ${made.text.slice(0, 300)}`,
  ).toBe(201);
  await expectProfileNames(api, made.json.id, profileName, groupName);
  return made.json.id;
}

/** Renames a profile the instance holds, and the group its cutoff resolves to. */
async function renameProfile(api, id, { profileName, groupName }) {
  const held = await api.get(`/api/v3/qualityprofile/${String(id)}`);
  expect(held.status, `GET the profile to rename answered ${String(held.status)}`).toBe(200);
  const answered = await api.put(
    `/api/v3/qualityprofile/${String(id)}`,
    profileNamed(held.json, profileName, groupName),
  );
  expect(
    answered.status,
    `the instance refused a ${String(profileName.length)}-character profile name: ${String(answered.status)} ${answered.text.slice(0, 300)}`,
  ).toBeLessThan(300);
  await expectProfileNames(api, id, profileName, groupName);
}

/**
 * The rendered geometry of the fact block, read off the elements the host laid out.
 *
 * Runs in the page because none of it is in the DOM: a value that truncates is one whose text is
 * wider than its own box, which is a pair of layout reads, and a class name cannot report either.
 */
const FACT_BLOCK_GEOMETRY = (dl) => {
  const box = (el) => {
    const rect = el.getBoundingClientRect();
    return {
      x: Math.round(rect.x),
      right: Math.round(rect.right),
      width: Math.round(rect.width),
      height: Math.round(rect.height),
    };
  };
  // The host's own content column. The block is laid out inside it, so it is what a block that grew
  // would have to grow past.
  const column = dl.closest("section");
  return {
    block: box(dl),
    blockOverflow: { scrollWidth: dl.scrollWidth, clientWidth: dl.clientWidth },
    column: column === null ? null : box(column),
    rows: Array.from(dl.children).map((row) => {
      const dt = row.querySelector("dt");
      const dd = row.querySelector("dd");
      return {
        label: dt === null ? null : dt.textContent.trim(),
        labelX: dt === null ? null : Math.round(dt.getBoundingClientRect().x),
        value: dd === null ? null : dd.textContent.trim(),
        title: dd === null ? null : dd.getAttribute("title"),
        valueScrollWidth: dd === null ? null : dd.scrollWidth,
        valueClientWidth: dd === null ? null : dd.clientWidth,
        height: box(row).height,
      };
    }),
  };
};

/**
 * Measures the open tab's fact block at `viewport`.
 *
 * The tab is re-opened when the resize left it closed: which tab strip a detail page draws follows
 * its viewport, so a resize can re-mount the strip.
 */
async function factBlockAt(page, viewport, what) {
  await page.setViewportSize(viewport);
  await expect(whisparrTab(page), `${what}: no ${TAB_LABEL} tab at this width`).toBeVisible({
    timeout: TAB_BUDGET_MS,
  });
  if ((await sceneFacts(page).count()) === 0) await whisparrTab(page).click();
  await expect(
    sceneFacts(page).first(),
    `${what}: the tab drew no fact block, so there is no layout to measure`,
  ).toBeVisible({ timeout: REGION_BUDGET_MS });
  return sceneFacts(page).first().evaluate(FACT_BLOCK_GEOMETRY);
}

/** The row a label names, in a geometry reading. */
const factRow = (geometry, label) => geometry.rows.find((row) => row.label === label);

/**
 * Asserts that the two long values truncate inside the block instead of widening it.
 *
 * `short` is the same block at the same width with the values the instance's own defaults produce,
 * measured in the same run rather than read from a record of an earlier one.
 */
function expectLongNamesTruncate(long, short, at) {
  for (const [label, named] of [
    [PROFILE_ROW_LABEL, LONG_PROFILE_NAME],
    [CUTOFF_ROW_LABEL, LONG_CUTOFF_GROUP_NAME],
  ]) {
    const row = factRow(long, label);
    expect(row, `${at}: the block draws no ${label} row`).toBeDefined();
    expect(
      row.value,
      `${at}: the ${label} row reads "${String(row.value)}" and not the long name the instance holds, so nothing long was rendered here`,
    ).toBe(named);
    expect(
      row.title,
      `${at}: the ${label} row truncates and carries "${String(row.title)}" on the element, so a reader cannot recover the whole name`,
    ).toBe(named);
    expect(
      row.valueScrollWidth,
      `${at}: the ${label} value is ${String(row.valueScrollWidth)}px of text in a ${String(row.valueClientWidth)}px box, so it is not overflowing and nothing here measures truncation`,
    ).toBeGreaterThan(row.valueClientWidth);
    expect(
      row.height,
      `${at}: the ${label} row is ${String(row.height)}px tall against ${String(factRow(short, label).height)}px with a short value, so the long name wrapped rather than truncating`,
    ).toBe(factRow(short, label).height);
  }

  expect(long.column, `${at}: the block sits in no host content column`).not.toBeNull();
  expect(
    long.column.right,
    `${at}: the host's content column ends at ${String(long.column.right)} with a long name and at ${String(short.column.right)} with a short one, so the value the instance answered moved the host's own layout`,
  ).toBe(short.column.right);
  expect(
    long.block.right,
    `${at}: the fact block ends at ${String(long.block.right)} and the host's content column ends at ${String(long.column.right)}, so a long name pushed the block past the column`,
  ).toBeLessThanOrEqual(long.column.right);
  expect(
    long.block.right,
    `${at}: the fact block ends at ${String(long.block.right)} with a long name and at ${String(short.block.right)} with a short one, so its width follows what the instance answered`,
  ).toBe(short.block.right);
  expect(
    long.blockOverflow.scrollWidth,
    `${at}: the block holds ${String(long.blockOverflow.scrollWidth)}px of content in ${String(long.blockOverflow.clientWidth)}px, so it overflows itself`,
  ).toBe(long.blockOverflow.clientWidth);

  const labelColumns = [...new Set(long.rows.map((row) => row.labelX))];
  expect(
    labelColumns,
    `${at}: the four labels sit at ${labelColumns.join(", ")}, so one of them is out of alignment with the rest`,
  ).toHaveLength(1);
  expect(
    long.rows.map((row) => row.labelX),
    `${at}: the labels moved from where the short values left them`,
  ).toEqual(short.rows.map((row) => row.labelX));
}

/**
 * Asserts that the two short values fit their boxes, which is what makes them a baseline.
 *
 * A short value that already truncated would give the long reading nothing to be compared against.
 */
function expectShortNamesFit(short, at) {
  for (const label of [PROFILE_ROW_LABEL, CUTOFF_ROW_LABEL]) {
    const row = factRow(short, label);
    expect(row, `${at}: the block draws no ${label} row`).toBeDefined();
    expect(
      row.valueScrollWidth,
      `${at}: the short ${label} value already overflows its own box, so it is no baseline for a long one`,
    ).toBe(row.valueClientWidth);
  }
}

/**
 * Opens the tab on `path` and measures its fact block at both widths.
 *
 * `expected` is text the instance answered, waited for before anything is measured, so the geometry
 * is read off a completed read rather than off whatever a row held while one was in flight.
 */
async function factBlockOnPage(page, baseUrl, path, expected, what) {
  await page.setViewportSize(NARROW_VIEWPORT);
  await visit(page, baseUrl, path, hostDetailTabs(page), what);
  await expect(whisparrTab(page), `${what}: the page drew no ${TAB_LABEL} tab`).toBeVisible({
    timeout: TAB_BUDGET_MS,
  });
  await whisparrTab(page).click();
  await expect(
    sceneFacts(page).first(),
    `${what}: the tab never drew "${expected}", which is what the instance holds for this scene`,
  ).toContainText(expected, { timeout: REGION_BUDGET_MS });

  return {
    narrow: await factBlockAt(page, NARROW_VIEWPORT, `${what} at 360`),
    wide: await factBlockAt(page, WIDE_VIEWPORT, `${what} at 1280`),
  };
}

/**
 * One scene's whole movie resource, as the instance holds it.
 */
async function sceneResource(api, remoteId, what) {
  const held = await api.get(`/api/v3/movie?stashId=${encodeURIComponent(remoteId)}`);
  expect(held.status, `${what}: the instance answered ${String(held.status)}`).toBe(200);
  const row = Array.isArray(held.json) ? held.json[0] : held.json;
  expect(row, `${what}: the instance holds no row for this scene`).toBeDefined();
  return row;
}

/**
 * Every member path at which two resources differ, walked whole and to the leaves.
 *
 * A whole-object walk rather than a list of the fields someone thought to name: the field a request
 * carries away unnoticed is exactly the one no list has on it.
 */
function differingPaths(before, after, path = "") {
  const shape = (value) =>
    value === null ? "null" : Array.isArray(value) ? "array" : typeof value;
  const here = path === "" ? "the whole resource" : path;
  if (shape(before) !== shape(after)) return [here];
  if (shape(before) === "object" || shape(before) === "array") {
    const members = [...new Set([...Object.keys(before), ...Object.keys(after)])];
    return members.flatMap((member) =>
      differingPaths(before[member], after[member], path === "" ? member : `${path}.${member}`),
    );
  }
  return before === after ? [] : [here];
}

test.describe("scene tab", () => {
  test("states the state Whisparr holds for the scene", async ({ page, baseUrl, sceneHarness }) => {
    // A container pair, an extension install, a browser and a real instance. Well above the shared
    // per-test budget, and deliberately its own number rather than a raised default for every spec.
    test.setTimeout(900_000);

    const coveApi = createApiClient(
      () => sceneHarness.baseUrl,
      () => sceneHarness.token,
    );

    // Everything the browser reported, so a bundle-load throw is named by this spec rather than
    // left as a blank region someone has to go and explain.
    const consoleErrors = [];
    page.on("console", (message) => {
      if (message.type() === "error") consoleErrors.push(message.text());
    });
    page.on("pageerror", (failure) => {
      consoleErrors.push(String(failure));
    });

    const whisparr = await startWhisparr({
      network: sceneHarness.container.getNetworkNames()[0],
      generations: ["v3"],
    });

    try {
      whisparr.v3.rootFolder = await registerRootFolder(
        whisparr.v3.container,
        whisparr.apiFor("v3"),
        "v3",
        WHISPARR_ROOT,
      );
      await connectWhisparr(coveApi, whisparr, "v3");

      const scene = await seedScene(coveApi, whisparr, { label: "Held", monitored: true });

      await visit(
        page,
        baseUrl,
        `/video/${String(scene.id)}`,
        hostDetailTabs(page),
        "the video detail page",
      );

      // The host wires its own tab list per page type and adds the extension tabs the manifest
      // declares, so an absent tab here is this extension's registration rather than the host's
      // reach.
      await expect(
        whisparrTab(page),
        `the video detail page: the host drew its own detail tabs and no ${TAB_LABEL} tab, so this extension's tab registration did not reach the manifest the host served.`,
      ).toBeVisible({ timeout: TAB_BUDGET_MS });

      await whisparrTab(page).click();

      await expect(
        page.getByText(STATE_ROW_LABEL, { exact: true }),
        "the tab mounted and drew no state row. A blank region is what a wrong component-map key looks like: it resolves to nothing, renders nothing and reports nothing.",
      ).toBeVisible({ timeout: REGION_BUDGET_MS });

      await expect(
        stateChip(page),
        `the tab drew no state chip reading "${scene.statedAs}", which is what the instance itself answered for the seeded scene`,
      ).toHaveText(new RegExp(`${scene.statedAs}$`), { timeout: REGION_BUDGET_MS });

      // A LONG NAME THE INSTANCE SUPPLIED, IN THE SAME CONTAINER. Its own test would need a second
      // Cove, a second install, a second browser and a second Whisparr for one page read, which is
      // the cost the five-case test below is batched to avoid.
      //
      // MEASURED, NOT INSPECTED. The value element carries `truncate` and a `title`, and neither
      // says whether the text overflows its own box or whether the block grew to fit it. Both are
      // layout reads: text wider than its box, and a block no wider than the column it sits in and
      // no wider than the same block with a short value.
      expect(
        [LONG_PROFILE_NAME.length, LONG_CUTOFF_GROUP_NAME.length].every(
          (length) => length >= LONG_NAME_FLOOR,
        ),
        `the names driven here are ${String(LONG_PROFILE_NAME.length)} and ${String(LONG_CUTOFF_GROUP_NAME.length)} characters, under the ${String(LONG_NAME_FLOOR)} this measurement needs to be about truncation at all`,
      ).toBe(true);

      // ONE SCENE, READ TWICE. The profile is renamed between the two readings rather than a second
      // scene being seeded under a second profile: the width of the host's content column follows
      // the page it is on, so two pages cannot tell a block widened by a value from a block sitting
      // in a wider column. Same video, same tab, same two viewports, and the instance's answer is
      // the only thing that differs.
      const instance = whisparr.apiFor("v3");
      const profileId = await seedProfile(instance, {
        profileName: SHORT_PROFILE_NAME,
        groupName: SHORT_CUTOFF_GROUP_NAME,
      });
      const measured = await seedScene(coveApi, whisparr, {
        label: "Named",
        monitored: true,
        qualityProfileId: profileId,
      });
      const measuredPath = `/video/${String(measured.id)}`;

      const short = await factBlockOnPage(
        page,
        baseUrl,
        measuredPath,
        SHORT_PROFILE_NAME,
        "the fact block with the short names",
      );
      expectShortNamesFit(short.wide, "at 1280");
      expectShortNamesFit(short.narrow, "at 360");

      await renameProfile(instance, profileId, {
        profileName: LONG_PROFILE_NAME,
        groupName: LONG_CUTOFF_GROUP_NAME,
      });
      const long = await factBlockOnPage(
        page,
        baseUrl,
        measuredPath,
        LONG_PROFILE_NAME,
        "the fact block with the long names",
      );

      expectLongNamesTruncate(long.wide, short.wide, "at 1280");
      expectLongNamesTruncate(long.narrow, short.narrow, "at 360");

      // The Quality row's own value is out of a reader's reach: it is the name of the file's
      // quality, and the instance's quality vocabulary is fixed. Asserted so a build that made it
      // unbounded is not left measured by a value that no longer bounds it.
      const vocabulary = await instance.get("/api/v3/qualitydefinition");
      const longestQualityName = (vocabulary.json ?? [])
        .map((definition) => definition.quality?.name ?? "")
        .reduce((longest, name) => (name.length > longest.length ? name : longest), "");
      expect(
        longestQualityName.length,
        `the instance's longest quality name is now "${longestQualityName}" at ${String(longestQualityName.length)} characters, so the Quality row can carry a name this measurement never drove through it`,
      ).toBeLessThan(LONG_NAME_FLOOR);

      // Does NOT depend on the tab rendering. A wrong export name throws an ESM SyntaxError at
      // bundle load, and the host loads every extension bundle under one promise, so that one throw
      // takes down every extension surface on the page with no build failure anywhere.
      const loadFailures = consoleErrors.filter((line) =>
        /SyntaxError|component not found|does not provide an export/i.test(line),
      );
      expect(
        loadFailures,
        `the browser reported a bundle-load failure: ${loadFailures.join(" | ")}`,
      ).toEqual([]);
    } finally {
      await whisparr.stop();
    }
  });

  test("older generation draws no scene tab, and no wrapper for one either", async ({
    page,
    baseUrl,
    sceneHarness,
  }) => {
    test.setTimeout(900_000);

    const coveApi = createApiClient(
      () => sceneHarness.baseUrl,
      () => sceneHarness.token,
    );

    const whisparr = await startWhisparr({
      network: sceneHarness.container.getNetworkNames()[0],
      generations: ["v2"],
    });

    try {
      await connectWhisparr(coveApi, whisparr, "v2");

      // No entry on the instance and none needed. Nothing is asked of it on this generation, and a
      // seeded entry would make an absent tab look like a tab with nothing to say.
      const video = await seedCoveVideo(coveApi, {
        title: `Older ${randomUUID().slice(0, 8)}`,
        remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: randomUUID() }],
      });

      // The registration is what removes the surface, so the set the page was built from is read
      // before the page is. The tab strip a page draws depends on its viewport, and the
      // registration does not.
      expect(
        await registeredVideoTabs(coveApi),
        "the older generation registers a video-page tab, so a surface it has no meaning on reached the manifest the host served",
      ).toEqual([]);

      await visit(
        page,
        baseUrl,
        `/video/${String(video.id)}`,
        hostDetailTabs(page),
        "the video detail page on the older generation",
      );
      await page.waitForTimeout(SETTLE_DWELL_MS);

      await expect(
        whisparrTab(page),
        `the older generation drew a ${TAB_LABEL} tab on the video detail page, so the registration is not conditional on the stored generation`,
      ).toHaveCount(0);

      await expect(
        unresolvedExtensionComponent(page),
        "the host drew its placeholder for a contributed tab it could not resolve, so a tab surface renders empty rather than being absent",
      ).toHaveCount(0);
    } finally {
      await whisparr.stop();
    }
  });

  // FIVE CASES IN ONE TEST, and the reason is cost rather than convenience. Each case needs a Cove
  // container, an extension install, a browser and a real Whisparr, and the verbs act on the same
  // scene in an order that matters: exclude changes the state monitor and search read back, so it
  // goes last. This is the shape missing-card.spec.mjs already uses for the same reason.
  //
  // EVERY STATE ASSERTION IS THE INSTANCE'S OWN. No case supplies the state it then asserts: after
  // each press the instance is read and the tab is asserted to agree with what it answered. A tab
  // painting the state the browser asked for disagrees with that read whenever a verb is refused,
  // which is the failure this spec exists to catch.
  test("controls: add, monitor, search now and exclude, driven against the instance", async ({
    page,
    baseUrl,
    sceneHarness,
  }) => {
    test.setTimeout(900_000);

    const coveApi = createApiClient(
      () => sceneHarness.baseUrl,
      () => sceneHarness.token,
    );

    // Registered before the first press. No control on this tab asks for confirmation, so a dialog
    // opening at all is the failure; dismissing it keeps the run from hanging on the way to saying
    // so.
    const dialogs = [];
    page.on("dialog", async (dialog) => {
      dialogs.push(dialog.type());
      await dialog.dismiss();
    });

    const whisparr = await startWhisparr({
      network: sceneHarness.container.getNetworkNames()[0],
      generations: ["v3"],
    });

    try {
      whisparr.v3.rootFolder = await registerRootFolder(
        whisparr.v3.container,
        whisparr.apiFor("v3"),
        "v3",
        WHISPARR_ROOT,
      );
      await connectWhisparr(coveApi, whisparr, "v3");
      const instance = whisparr.apiFor("v3");

      // CASE 1. Add, on a scene the instance holds no entry for. The Cove video carries an identity
      // row and the instance carries nothing for it, which is the only state the add control is
      // offered in.
      const absentRemoteId = randomUUID();
      const absent = await seedCoveVideo(coveApi, {
        title: `Absent ${absentRemoteId.slice(0, 8)}`,
        remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: absentRemoteId }],
      });

      await visit(
        page,
        baseUrl,
        `/video/${String(absent.id)}`,
        hostDetailTabs(page),
        "the video detail page for a scene the instance does not hold",
      );
      await expect(whisparrTab(page)).toBeVisible({ timeout: TAB_BUDGET_MS });
      await whisparrTab(page).click();
      await expect(page.getByText(STATE_ROW_LABEL, { exact: true })).toBeVisible({
        timeout: REGION_BUDGET_MS,
      });

      // The three controls this state stops each announce their own name first and their reason
      // after it. A control whose name was lost still carries its reason, so the reason alone
      // proves nothing.
      for (const label of [MONITOR, SEARCH]) {
        await expect(
          sceneControl(page, label),
          `${label} is unavailable on a scene the instance does not hold and did not announce itself by name`,
        ).toBeDisabled();
      }
      await expect(sceneControl(page, ADD)).toBeEnabled();

      // An enabled control announces its own name and nothing after it, so an exact match finds
      // it. A control carrying a reason it is not disabled for would fail this and pass the
      // anchored match above.
      for (const label of [ADD, EXCLUDE]) {
        await expect(
          page.getByRole("button", { name: label, exact: true }),
          `${label} is available and announces something beyond its own name`,
        ).toBeEnabled();
      }

      await sceneControl(page, ADD).click();
      await chipAgreesWithInstance(page, instance, absentRemoteId, "after Add");

      // CASE 2. Monitor, on a scene the instance holds and is not monitoring. The instance's answer
      // before the press is recorded, so the assertion is that the press moved it rather than that
      // it landed on a value this test named.
      const held = await seedScene(coveApi, whisparr, { label: "Held", monitored: false });
      await visit(
        page,
        baseUrl,
        `/video/${String(held.id)}`,
        hostDetailTabs(page),
        "the video detail page for a scene the instance holds",
      );
      await expect(whisparrTab(page)).toBeVisible({ timeout: TAB_BUDGET_MS });
      await whisparrTab(page).click();
      await chipAgreesWithInstance(page, instance, held.remoteId, "before Monitor");

      const beforeMonitor = await stateOnInstance(instance, held.remoteId);
      // The whole resource, not the flag. The composed body carries one member and a unit test pins
      // that; what the instance does with the members the body leaves out is a fact about the
      // instance, and only a read of everything it holds before and after can report it.
      const resourceBefore = await sceneResource(instance, held.remoteId, "before Monitor");
      await sceneControl(page, MONITOR).click();
      await expect(sceneControl(page, STOP_MONITORING)).toBeVisible({
        timeout: REGION_BUDGET_MS,
      });
      const afterMonitor = await chipAgreesWithInstance(
        page,
        instance,
        held.remoteId,
        "after Monitor",
      );
      expect(
        afterMonitor,
        "the instance answers the same state before and after the press, so nothing reached it",
      ).not.toBe(beforeMonitor);

      const resourceAfter = await sceneResource(instance, held.remoteId, "after Monitor");
      const moved = differingPaths(resourceBefore, resourceAfter);
      expect(
        moved,
        `the press moved ${moved.join(", ")} on the instance. Setting the monitored flag is meant to leave every other field the instance holds exactly as it was.`,
      ).toEqual(["monitored"]);

      // CASE 3. Search now, on the monitored scene. The tab is asserted to state that the instance
      // holds the command, and nothing about a file, a queue or a download: confirming receipt is
      // all the read behind that sentence establishes.
      await sceneControl(page, SEARCH).click();
      await expect(
        page.getByText(SEARCH_IS_WITH_WHISPARR, { exact: false }),
        "the tab said nothing after a search, so a reader has no way to know the instance took it",
      ).toBeVisible({ timeout: REGION_BUDGET_MS });

      const activity = await whisparrActivity(instance);
      expect(
        activity.commandNames.some((name) => /search/i.test(name)),
        `the instance's own command roster names no search after the press: ${activity.commandNames.join(", ")}`,
      ).toBe(true);

      // CASE 4. Exclude, and CASE 5, its return leg. One control with two labels, so a reader who
      // excludes the wrong scene fixes it where they broke it.
      await sceneControl(page, EXCLUDE).click();
      await expect(
        sceneControl(page, REMOVE_EXCLUSION),
        "the exclusion control kept its adding label, so the tab offers no way back from the press",
      ).toBeVisible({ timeout: REGION_BUDGET_MS });
      const afterExclude = await chipAgreesWithInstance(
        page,
        instance,
        held.remoteId,
        "after Exclude",
      );
      expect(afterExclude).not.toBe(afterMonitor);

      await sceneControl(page, REMOVE_EXCLUSION).click();
      await expect(
        sceneControl(page, EXCLUDE),
        "the control did not return to its adding label, so the toggle only goes one way",
      ).toBeVisible({ timeout: REGION_BUDGET_MS });
      const afterReturn = await chipAgreesWithInstance(
        page,
        instance,
        held.remoteId,
        "after Remove exclusion",
      );
      expect(afterReturn, "removing the exclusion left the scene reading as excluded").not.toBe(
        afterExclude,
      );

      // Watched for as long as an absence is watched for anywhere here: a dialog that has not been
      // raised yet is indistinguishable from one that never will be.
      await page.waitForTimeout(SETTLE_DWELL_MS);
      expect(
        dialogs,
        `a native browser dialog opened during a press: ${dialogs.join(", ")}. No control on this tab asks for confirmation.`,
      ).toEqual([]);
    } finally {
      await whisparr.stop();
    }
  });
});
