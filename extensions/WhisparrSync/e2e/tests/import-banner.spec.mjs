// The banner, on the settings page a user actually opens, against a live Cove and a live Whisparr.
//
// Three acts. The first asserts the banner is ABSENT before anything is delivered, so its later
// presence cannot have been true all along. The second builds one root's line up past the three paths
// it keeps. The third is the point of the whole design: a success under one root must clear that
// root's line and leave a root that is still failing exactly as it was.
//
// Every assertion is on the Cove side — what the page draws, and what the extension stored. The
// callback's status says the request was well formed and nothing about whether anything was
// registered, so it is a diagnostic here and never the subject.
//
// The store is polled between deliveries and the PAGE is opened once per act: the banner reads once
// per page lifetime, so a fresh navigation is what re-reads it, and polling a navigation would pay a
// cold panel load per attempt.
import {
  test as base,
  expect,
  createApiClient,
  isolatedHarnessFixture,
} from "@cove-extensions/e2e";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { randomUUID } from "node:crypto";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const SETTINGS_PATH = `/api/extensions/${EXTENSION_ID}/settings`;
const CALLBACK_PATH = `/api/extensions/${EXTENSION_ID}/callback`;
const CALLBACK_STATUS_PATH = `${CALLBACK_PATH}/status`;
const DATA_PATH = `/api/extensions/${EXTENSION_ID}/data`;
const PANEL_PATH = "/settings/whisparr-sync";

// What the panel's own banner read asks for. Matched as a tail rather than a whole address, because
// the page reaches it through the host's own client and the origin is the browser's.
const BANNER_ROUTE_TAIL = `/api/extensions/${EXTENSION_ID}/import/banner`;

// Transcribed by hand from the extension's own frozen constants and from the delivery the pinned
// build made. Written out rather than imported, so a rename on either side has to be made twice.
const SECRET_HEADER = "X-Cove-Whisparr-Sync-Secret";
const SECRET_QUERY_PARAMETER = "s";
const V3_USER_AGENT = "Whisparr/3.3.8.1097 (alpine 3.23.5)";

// Transcribed from the blob the backend serializes, which its own test pins.
const REFUSALS = "ImportRefusals";

// Transcribed by hand from the bundle's own copy module. A fragment rather than the whole sentence:
// the sentence carries a character this file would otherwise have to reproduce exactly to find it.
const BANNER_HEADING = "Cove can't find imported files";

// Two roots the instance declares for itself, and the Cove root the harness declares. The Whisparr
// spellings name content Cove reaches by another name, which is the deployment this surface exists
// for.
const WHISPARR_ROOT = "/whisparr-media";
const WHISPARR_OTHER_ROOT = "/whisparr-elsewhere";
const COVE_ROOT = "/data";

// Named per operation, so a failure says which act blew its budget rather than which line.
const ACT_TWO_REFUSAL_BUDGET_MS = 60_000;
const ACT_THREE_REFUSAL_BUDGET_MS = 60_000;
const ACT_THREE_IMPORT_BUDGET_MS = 120_000;

// A cold container serving the extension bundle for the first time is slow rather than broken and
// raises no signal to wait on.
const PANEL_ATTEMPT_BUDGET_MS = 60_000;
const PANEL_ATTEMPTS = 3;

// Between the banner's answer landing and the render it causes. A frame, not a container: the wait
// this covers is React committing, which every other wait in this file is far too long for.
const RENDER_SETTLE_MS = 500;

const CAPTURED_DELIVERY = join(
  import.meta.dirname,
  "..",
  "..",
  "src",
  "WhisparrSync.Tests",
  "TestSupport",
  "Fixtures",
  "whisparr-v3-3.3.8.1097-webhook-import.json",
);

const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),
  baseUrl: async ({ isolatedHarness }, use) => {
    await use(isolatedHarness.baseUrl);
  },
});

/** The delivery a real instance sent, with only the file it names rewritten. */
function deliveryNaming(reportedPath, size) {
  const body = JSON.parse(readFileSync(CAPTURED_DELIVERY, "utf8"));
  body.movieFile.path = reportedPath;
  body.movieFile.size = size;
  return body;
}

/** Points the extension at the fixture instance and stores its key. */
async function configure(api, whisparr) {
  const saved = await api.put(SETTINGS_PATH, {
    selectedGeneration: "v3",
    v3: {
      address: whisparr.v3.internalBaseUrl,
      keyWrite: "replace",
      apiKey: whisparr.apiKey,
    },
    v2: null,
  });
  expect(saved.status, `saving settings failed: ${saved.text.slice(0, 300)}`).toBe(200);
}

/** This installation's own callback secret, read out of the address the page offers. */
async function callbackSecret(api) {
  const status = await api.get(CALLBACK_STATUS_PATH);
  expect(status.status, `GET ${CALLBACK_STATUS_PATH} answered: ${status.text.slice(0, 300)}`).toBe(
    200,
  );

  const secret = new URL(status.json.copyableAddress).searchParams.get(SECRET_QUERY_PARAMETER);
  expect(secret, `the copyable address carried no ${SECRET_QUERY_PARAMETER}`).toBeTruthy();
  return secret;
}

/** The refusal aggregate the extension has stored, read through Cove's own bulk data route. */
async function refusalsIn(api) {
  const stored = await api.get(DATA_PATH);
  expect(stored.status, `GET ${DATA_PATH} answered: ${stored.text.slice(0, 300)}`).toBe(200);

  const options = stored.json?.options;
  return options ? (JSON.parse(options)[REFUSALS] ?? []) : [];
}

/** One root's stored line, or undefined while that root has none. */
const lineFor = (refusals, root) => refusals.find((entry) => entry.Root === root);

/** Every video Cove holds. */
async function videosIn(api) {
  const listed = await api.get("/api/videos?perPage=200");
  expect(listed.status, `GET /api/videos answered: ${listed.text.slice(0, 300)}`).toBe(200);
  return listed.json?.items ?? [];
}

/**
 * Opens the settings panel and waits for this extension's own component to render.
 *
 * The path is not one of the host's own routes. The host carries the unknown key only until it
 * finishes loading extensions, then answers a load that produced no matching tab by switching to its
 * first built-in tab and rewriting the address. Nothing after that rewrite can reach this panel, and
 * only a fresh navigation recovers it.
 */
async function openPanel(page, baseUrl) {
  const addressField = page.getByPlaceholder("http://whisparr:6969");
  let answered = false;
  for (let attempt = 1; attempt <= PANEL_ATTEMPTS; attempt++) {
    // Registered BEFORE the navigation. The banner's answer can land before the panel's own field is
    // visible, and a wait started afterwards would never see it — which would let an absent banner
    // stand for a read that never ran.
    const bannerRead = page
      .waitForResponse((response) => response.url().includes(BANNER_ROUTE_TAIL), {
        timeout: PANEL_ATTEMPT_BUDGET_MS,
      })
      .then(() => true)
      .catch(() => false);

    await page.goto(`${baseUrl}${PANEL_PATH}`);
    const rendered = await addressField
      .waitFor({ state: "visible", timeout: PANEL_ATTEMPT_BUDGET_MS })
      .then(() => true)
      .catch(() => false);
    answered = await bannerRead;
    if (rendered && answered) break;
  }

  await expect(
    addressField,
    `the panel never rendered at ${baseUrl}${PANEL_PATH}; the page is now at ${page.url()}`,
  ).toBeVisible();
  expect(
    answered,
    `the panel never asked for the banner at ${baseUrl}${PANEL_PATH}, so nothing it does or does not show is evidence`,
  ).toBe(true);

  // The answer has landed; this covers the render it causes, which is a frame rather than a
  // container.
  await page.waitForTimeout(RENDER_SETTLE_MS);
}

/**
 * The banner as it stands on the open panel: null when there is none, otherwise one entry per root
 * line in the order the block draws them.
 */
async function bannerOn(page) {
  const block = page.getByRole("alert").filter({ hasText: BANNER_HEADING });
  if ((await block.count()) === 0) {
    return null;
  }
  return block.first().locator(":scope > ul > li").allInnerTexts();
}

/** Opens the panel and reads the banner off it. */
async function bannerAfterOpening(page, baseUrl) {
  await openPanel(page, baseUrl);
  return bannerOn(page);
}

test("the banner names each failing root, bounds its list, and clears only the root that worked", async ({
  isolatedHarness,
  baseUrl,
  page,
}) => {
  // A Cove pair, a Whisparr container, six deliveries and five panel loads between them. The default
  // per-test budget covers none of it.
  test.setTimeout(900_000);

  const api = createApiClient(
    () => isolatedHarness.baseUrl,
    () => isolatedHarness.token,
  );

  const whisparr = await startWhisparr({
    network: isolatedHarness.container.getNetworkNames()[0],
    generations: ["v3"],
  });

  try {
    for (const root of [WHISPARR_ROOT, WHISPARR_OTHER_ROOT]) {
      await registerRootFolder(whisparr.v3.container, whisparr.apiFor("v3"), "v3", root);
    }
    await configure(api, whisparr);
    const secret = await callbackSecret(api);

    // Its own client, carrying no Cove credential: Whisparr holds none, and the secret plus the agent
    // are the whole of what a real delivery presents.
    const asWhisparr = createApiClient(() => isolatedHarness.baseUrl, undefined, {
      headers: { [SECRET_HEADER]: secret, "User-Agent": V3_USER_AGENT },
    });

    /** Posts the captured body, naming `reportedPath`, as a real instance would. */
    const deliver = async (reportedPath, size) => {
      const delivered = await asWhisparr.post(CALLBACK_PATH, deliveryNaming(reportedPath, size));
      // A diagnostic, not the evidence: a refused delivery surfacing as a poll timeout would name the
      // wrong cause entirely.
      expect(
        delivered.status,
        `the callback refused the delivery, so nothing below is about the banner: ${delivered.text.slice(0, 300)}`,
      ).toBeLessThan(400);
    };

    // ---- act one: nothing delivered, nothing shown ----
    expect(await refusalsIn(api), "the extension already held refusals").toEqual([]);
    expect(
      await bannerAfterOpening(page, baseUrl),
      "the banner was on the page before anything had been delivered",
    ).toBeNull();

    // ---- act two: one root, four refusals, three paths ----
    const tails = Array.from({ length: 4 }, () => `${randomUUID()}.mp4`);
    await deliver(`${WHISPARR_ROOT}/${tails[0]}`, 4096);

    await pollUntil(
      () => refusalsIn(api),
      (refusals) => lineFor(refusals, WHISPARR_ROOT)?.CountSinceLastSuccess === 1,
      {
        timeoutMs: ACT_TWO_REFUSAL_BUDGET_MS,
        intervalMs: 1_000,
        label: `act two: the first refusal counted against ${WHISPARR_ROOT}`,
      },
    );

    const first = await bannerAfterOpening(page, baseUrl);
    expect(first, "act two: the banner did not appear after the first refusal").not.toBeNull();
    expect(first).toHaveLength(1);
    expect(first[0]).toContain(WHISPARR_ROOT);
    expect(first[0]).toContain(tails[0]);
    expect(first[0], "act two: the listed path did not name why it was refused").toContain(
      "No Cove library folder holds this file.",
    );

    for (const [index, tail] of tails.slice(1).entries()) {
      await deliver(`${WHISPARR_ROOT}/${tail}`, 4096);
      await pollUntil(
        () => refusalsIn(api),
        (refusals) => lineFor(refusals, WHISPARR_ROOT)?.CountSinceLastSuccess === index + 2,
        {
          timeoutMs: ACT_TWO_REFUSAL_BUDGET_MS,
          intervalMs: 1_000,
          label: `act two: refusal ${index + 2} counted against ${WHISPARR_ROOT}`,
        },
      );
    }

    const bounded = await bannerAfterOpening(page, baseUrl);
    expect(bounded, "act two: the banner went away while refusals were outstanding").not.toBeNull();
    expect(bounded).toHaveLength(1);

    // Four refusals, three paths, and the one no longer listed is the oldest.
    expect(bounded[0], "act two: the count is not the stored one").toContain("4");
    for (const tail of tails.slice(1)) {
      expect(bounded[0], `act two: ${tail} is not listed`).toContain(tail);
    }
    expect(
      bounded[0],
      "act two: the oldest path is still listed, so the list is not bounded at three",
    ).not.toContain(tails[0]);

    // ---- act three: a second root fails, the first one starts working ----
    const otherTail = `${randomUUID()}.mp4`;
    await deliver(`${WHISPARR_OTHER_ROOT}/${otherTail}`, 4096);

    await pollUntil(
      () => refusalsIn(api),
      (refusals) => lineFor(refusals, WHISPARR_OTHER_ROOT) !== undefined,
      {
        timeoutMs: ACT_THREE_REFUSAL_BUDGET_MS,
        intervalMs: 1_000,
        label: `act three: a refusal counted against ${WHISPARR_OTHER_ROOT}`,
      },
    );

    const bothFailing = await bannerAfterOpening(page, baseUrl);
    expect(bothFailing, "act three: the banner went away with two roots failing").not.toBeNull();
    expect(bothFailing).toHaveLength(2);

    const otherLineBefore = bothFailing.find((line) => line.includes(WHISPARR_OTHER_ROOT));
    expect(otherLineBefore, `act three: no line named ${WHISPARR_OTHER_ROOT}`).toBeDefined();

    // A file that really resolves, from the FIRST root. Placed but not registered, so the item Cove
    // ends up holding is one this extension caused.
    const resolvingTail = `${randomUUID()}.mp4`;
    const placed = await placeVideoUnregistered({
      container: isolatedHarness.container,
      destPath: `${COVE_ROOT}/${resolvingTail}`,
    });
    const { output } = await isolatedHarness.exec(["stat", "-c", "%s", placed]);
    const size = Number(output.trim());
    expect(Number.isInteger(size) && size > 0, `stat reported ${output.trim()} for ${placed}`).toBe(
      true,
    );

    expect(
      (await videosIn(api)).map((video) => video.id),
      "act three: Cove already held a video before the resolving delivery",
    ).toEqual([]);

    await deliver(`${WHISPARR_ROOT}/${resolvingTail}`, size);

    const registered = await pollUntil(
      () => videosIn(api),
      (videos) => videos.length > 0,
      {
        timeoutMs: ACT_THREE_IMPORT_BUDGET_MS,
        intervalMs: 1_000,
        label: "act three: Cove to hold the video the resolving delivery caused",
      },
    );
    expect(registered, "act three: the delivery produced more than one item").toHaveLength(1);

    await pollUntil(
      () => refusalsIn(api),
      (refusals) => lineFor(refusals, WHISPARR_ROOT) === undefined,
      {
        timeoutMs: ACT_THREE_REFUSAL_BUDGET_MS,
        intervalMs: 1_000,
        label: `act three: ${WHISPARR_ROOT}'s line to be cleared by its own success`,
      },
    );

    const afterOneSucceeded = await bannerAfterOpening(page, baseUrl);
    expect(
      afterOneSucceeded,
      "act three: one root's success took the whole banner away while another root was still failing",
    ).not.toBeNull();
    expect(afterOneSucceeded).toHaveLength(1);
    expect(
      afterOneSucceeded[0],
      `act three: ${WHISPARR_ROOT}'s line survived its own success`,
    ).not.toContain(WHISPARR_ROOT);
    expect(
      afterOneSucceeded[0],
      `act three: ${WHISPARR_OTHER_ROOT}'s line changed although nothing happened under it`,
    ).toBe(otherLineBefore);

    // ---- the control: the second root works too, and the banner goes away entirely ----
    const lastTail = `${randomUUID()}.mp4`;
    const lastPlaced = await placeVideoUnregistered({
      container: isolatedHarness.container,
      destPath: `${COVE_ROOT}/${lastTail}`,
    });
    const last = await isolatedHarness.exec(["stat", "-c", "%s", lastPlaced]);
    const lastSize = Number(last.output.trim());
    expect(Number.isInteger(lastSize) && lastSize > 0, `stat reported ${last.output.trim()}`).toBe(
      true,
    );

    await deliver(`${WHISPARR_OTHER_ROOT}/${lastTail}`, lastSize);

    await pollUntil(
      () => refusalsIn(api),
      (refusals) => refusals.length === 0,
      {
        timeoutMs: ACT_THREE_IMPORT_BUDGET_MS,
        intervalMs: 1_000,
        label: `act three: ${WHISPARR_OTHER_ROOT}'s line to be cleared by its own success`,
      },
    );

    expect(
      await bannerAfterOpening(page, baseUrl),
      "act three: the banner is still on the page with nothing left to report",
    ).toBeNull();
  } finally {
    // Before the harness's own, which the isolated fixture runs after this test: the daemon refuses
    // to remove a network a container still holds an endpoint on.
    await whisparr.stop();
  }
});
