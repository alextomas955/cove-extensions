// The three verbs a monitored entity offers, driven from a real studio page against a real instance.
//
// Its two siblings cover the control and the selection bar. This one covers what only appears once
// the instance already monitors the entity, and the three facts about those rows that no tier inside
// this repository can reach:
//
// - Whether the rows exist at all on a monitored entity, and whether they are ABSENT rather than
//   disabled on one nothing monitors. Three routes were mounted for them, and mounting a route is
//   exactly the change that could make a row appear where the product says none should.
// - Whether the notice a settled action leaves is VISIBLE. It renders through a portal because the
//   host clips its entity hero, and a source reading cannot settle whether the escape works: only a
//   rendered page has the hero's own geometry.
// - Whether the row that downloads is reachable. It is asserted PRESSABLE and deliberately never
//   pressed.
//
// NO SEARCH IS EXECUTED ANYWHERE IN THIS SPEC. The grabbing verb is proven reachable on its row and
// in the emitted wire document, and its effect stays unmeasured by choice. The instance's own command
// roster is read at the end over a named dwell to say so.
//
// THE SENTENCES ARE IMPORTED FROM THE SHIPPED COPY MODULE, unlike in its two siblings, which
// transcribe by hand. Those two assert that a rendered sentence reads a particular way, and reading
// the same constant the component renders would assert that a string equals itself. Here the
// sentences are LOCATORS - which row to press, which reason a disabled row must carry - and a locator
// built from a copied literal stops finding the row the day the row is renamed, silently.
//
// WHAT THIS SPEC DELIBERATELY DOES NOT COVER, AND WHY NEITHER IS AN OMISSION.
//
// The add-all-missing row disabled on a v2 connection. The shared harness wires a catalogue seed for
// v3 only, so no v2 studio can be put in front of the host as monitored, and the secondary rows
// appear only on a monitored entity. That row stays covered where a capability set is an input a
// test supplies, in the menu's own unit tier.
//
// The sentence an action that never arrived states. It is reached from `state.actionError`, which is
// set only when the EXTENSION's own route answers non-2xx. An unreachable Whisparr is not that case:
// the route reaches it, fails to, and answers a refusal with a 200. The browser reads that refusal
// and states its own sentence for it, which is a different notice from this one. So no address this
// spec could point the extension at produces the never-arrived notice, and simulating a 500 to see
// it would assert a condition rather than observe one. The notice's own geometry is what this spec
// is for, and it is asserted below on the skip notice - the same portal path, the same anchoring
// hook, on a notice a real gesture really produced, and the same surface every refusal now rides.
//
// A spec that cannot reach a case must not pretend to.
//
// IF THIS SPEC GOES RED, read the log for a container-not-running line before debugging the UI. A red
// e2e in this repository is usually the Cove container dying rather than the page under test.
import { test as base, createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { attemptUntil } from "@cove-extensions/e2e/poll";
import { seedVideo } from "@cove-extensions/e2e/seed-media";
import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

import {
  ACTION_ADD_ALL_MISSING,
  ACTION_REFLECT_OWNED,
  ACTION_SEARCH_ALL_MONITORED,
  MONITORED_IN_WHISPARR,
  REFLECT_OWNED_SKIPPED,
} from "../../src/WhisparrSync.Ui/src/common/ui/copy.ts";
import {
  connectWhisparr,
  expect,
  extensionRoute,
  seedCoveStudio,
  SETTLE_DWELL_MS,
  STASHDB_ENDPOINT,
  whisparrAcquisitionSurface,
  whisparrActivity,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
} from "../lib/whisparr-sync-fixtures.mjs";

const __dirname = dirname(fileURLToPath(import.meta.url));

/** The emitted wire document, which is where a mounted route is reachable from without a request. */
const WIRE_DOCUMENT = join(__dirname, "..", "..", "wire", "openapi.json");

// A command whose name carries this is the observable form of a started search on both generations.
// A pattern rather than the three command names, so this file names no verb that downloads.
const SEARCH_COMMAND = /search/i;

// The command reflect owned issues, which is the observable form of the linking work. Named here so
// the skipped case can assert the instance was asked for nothing.
const MANUAL_IMPORT_COMMAND = "ManualImport";

// The instance setting reflect owned reads before it links anything.
const HARD_LINK_SETTING = "copyUsingHardlinks";
const MEDIA_MANAGEMENT_PATH = "/api/v3/config/mediamanagement";

// The narrower scope, in the wire spelling the server answers and binds it in - not the label the
// menu draws, which is what the imported copy module carries.
const SCOPE_FUTURE_SCENES_WIRE = "futureScenes";

// Each budget names the operation it bounds, so a failure says which one blew it rather than
// reporting the whole test as a timeout naming nothing.
const PAGE_BUDGET_MS = 60_000;
const PAGE_ATTEMPTS = 3;
const CONTROL_BUDGET_MS = 60_000;
const GESTURE_BUDGET_MS = 60_000;
const JOB_BUDGET_MS = 120_000;

const test = base.extend({
  secondaryHarness: [
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

  // The `page` fixture resolves its address through `baseUrl`, so without this override the browser
  // would drive the worker-shared instance while every API assertion addressed this one.
  baseUrl: async ({ secondaryHarness }, use) => {
    await use(secondaryHarness.baseUrl);
  },
});

const hostEditButton = (page) => page.getByRole("button", { name: "Edit", exact: true });

const monitoredControl = (page) =>
  page.getByRole("button", { name: new RegExp(`^${MONITORED_IN_WHISPARR}`) });

/** The menu a monitored entity's control opens. */
const monitoredMenu = (page) => page.getByRole("menu", { name: MONITORED_IN_WHISPARR });

/** Opens `path`, re-navigating while nothing the caller named has rendered. */
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
    `${label}: nothing rendered at ${baseUrl}${path} across ${String(PAGE_ATTEMPTS)} navigation(s) of ${String(PAGE_BUDGET_MS)}ms each; the page is now at ${page.url()}`,
  );
}

/**
 * Turns the instance's hard-link setting on or off.
 *
 * The whole resource is read and written back with one member changed. This generation's config
 * routes replace what they are sent, so a body carrying only the one flag would blank the rest.
 */
async function setHardLinks(instance, on) {
  const current = await instance.get(MEDIA_MANAGEMENT_PATH);
  if (current.status !== 200) {
    throw new Error(
      `setHardLinks: GET ${MEDIA_MANAGEMENT_PATH} answered ${String(current.status)}, so the setting reflect owned reads could not be arranged`,
    );
  }
  const written = await instance.put(MEDIA_MANAGEMENT_PATH, {
    ...current.json,
    [HARD_LINK_SETTING]: on,
  });
  if (written.status >= 300) {
    throw new Error(
      `setHardLinks: PUT ${MEDIA_MANAGEMENT_PATH} answered ${String(written.status)}: ${String(written.text).slice(0, 300)}`,
    );
  }
  const read = await instance.get(MEDIA_MANAGEMENT_PATH);
  if (read.json?.[HARD_LINK_SETTING] !== on) {
    throw new Error(
      `setHardLinks: the instance reports ${HARD_LINK_SETTING}=${JSON.stringify(read.json?.[HARD_LINK_SETTING])} after being asked for ${String(on)}, so the case below is not the one it names`,
    );
  }
}

/** How many commands whose name is `named` the instance holds right now. */
async function commandCount(instance, named) {
  const { commandNames } = await whisparrActivity(instance);
  return commandNames.filter((name) => name === named).length;
}

/**
 * Presses one row of a monitored entity's menu and answers the enqueued run's job id.
 *
 * The id is taken off the response the browser itself received, so the run followed below is the one
 * the gesture started rather than one found by searching for a job of the right shape.
 */
async function pressSecondary(page, label, route) {
  const answered = page.waitForResponse(
    (response) => new URL(response.url()).pathname.endsWith(`/${route}`),
    { timeout: GESTURE_BUDGET_MS },
  );
  // Opened only when it is not already. The menu deliberately STAYS OPEN across an action, so that
  // what a reader sees next is the state the instance answered rather than the menu they pressed
  // vanishing before anything changed - and a control press is a toggle, so pressing it here
  // unconditionally would close the menu the previous action left open.
  if (!(await monitoredMenu(page).isVisible())) {
    await monitoredControl(page).click();
  }
  await expect(
    monitoredMenu(page),
    `the monitored control did not open its menu before "${label}" could be pressed`,
  ).toBeVisible();
  const row = monitoredMenu(page).getByRole("menuitem", { name: label, exact: true });
  await expect(row, `the menu offers no "${label}" row`).toBeVisible();
  await expect(row, `the "${label}" row is not pressable on a monitored studio`).toBeEnabled();
  await row.click();

  const response = await answered;
  const body = await response.json().catch(() => null);
  return { status: response.status(), body };
}

/** Follows one enqueued run to a settled state through the extension's own status route. */
async function followJob(coveApi, jobId, label) {
  const { settled, value, note } = await attemptUntil(
    async (_signal, record) => {
      const status = await coveApi.get(extensionRoute(`job-status/${String(jobId)}`));
      record(`${String(status.status)} with state ${String(status.json?.status ?? "absent")}`);
      return status.json?.status === "completed" ? { value: status.json } : null;
    },
    { timeoutMs: JOB_BUDGET_MS, intervalMs: 1_000, label },
  );
  expect(
    settled,
    `${label} never reported itself complete within ${String(JOB_BUDGET_MS)}ms; its status route last answered ${note}`,
  ).toBe(true);
  return value;
}

/**
 * The line a settled run reports what it did on, or undefined where it reported none.
 *
 * Read from whichever field of the status carries it rather than from one named field. These runs
 * put their line on the final progress report's SUB-TASK, because the host's progress carries no
 * summary field of its own - so which field a reader finds it in is the host's business and not this
 * spec's subject. What is asserted is that the run said what it did.
 */
function reportedLine(status, shape) {
  return [status.subTask, status.summary].find(
    (line) => typeof line === "string" && shape.test(line),
  );
}

test("the three mounted verbs on a real monitored studio, and the notice a settled one leaves", async ({
  page,
  baseUrl,
  secondaryHarness,
}) => {
  // Two container pairs, an extension install, a browser and a real instance, and this spec drives
  // several gestures rather than one. Deliberately its own number rather than a raised default.
  test.setTimeout(900_000);

  const coveApi = createApiClient(
    () => secondaryHarness.baseUrl,
    () => secondaryHarness.token,
  );

  const whisparr = await startWhisparr({
    network: secondaryHarness.container.getNetworkNames()[0],
    generations: ["v3"],
  });

  try {
    const instance = whisparr.apiFor("v3");
    whisparr.v3.rootFolder = await registerRootFolder(
      whisparr.v3.container,
      instance,
      "v3",
      WHISPARR_ROOT,
    );

    // The bound on every never-searched claim below, read off the instance rather than assumed.
    expect(
      await whisparrAcquisitionSurface(instance),
      "the fixture instance has an indexer or a download client, so a search started here could acquire something and no never-searched claim may be taken against it",
    ).toEqual({ indexers: 0, downloadClients: 0 });

    const run = randomUUID().slice(0, 8);
    const monitoredForeignId = `cove-e2e-secondary-held-${run}`;
    const quietForeignId = `cove-e2e-secondary-quiet-${run}`;
    const sceneRemoteId = `cove-e2e-secondary-scene-${run}`;

    for (const foreignId of [monitoredForeignId, quietForeignId]) {
      const seeded = await whisparr.seedEntity("v3", {
        kind: "studio",
        foreignId,
        title: `Cove E2E Secondary ${foreignId}`,
      });
      expect(
        seeded.monitored,
        `the seeded studio ${foreignId} arrived already monitored, so this spec's own monitored and unmonitored cases would be the same case`,
      ).toBe(false);
    }

    const heldStudio = await seedCoveStudio(coveApi, {
      name: `Secondary Held ${run}`,
      remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: monitoredForeignId }],
    });
    const quietStudio = await seedCoveStudio(coveApi, {
      name: `Secondary Quiet ${run}`,
      remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: quietForeignId }],
    });

    // A file the library owns, carrying this studio and one scene identity: the folder is what
    // reflect owned streams, and the identity is what add all missing offers. Both verbs then have
    // something to act on rather than an empty source, which would settle nothing.
    const video = await seedVideo({
      container: secondaryHarness.container,
      baseUrl: secondaryHarness.baseUrl,
      token: secondaryHarness.token,
      destName: `secondary-${run}.mp4`,
    });
    const owned = await coveApi.put(`/api/videos/${String(video.id)}`, {
      studioId: heldStudio.id,
      remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: sceneRemoteId }],
    });
    expect(
      owned.status,
      `attaching the seeded video to the studio answered ${String(owned.status)}: ${String(owned.text).slice(0, 300)}`,
    ).toBeLessThan(300);

    await connectWhisparr(coveApi, whisparr, "v3");

    // ---- MON-8's quiet half: nothing monitors this studio, so none of the three rows exists. ----
    await visit(
      page,
      baseUrl,
      `/studio/${String(quietStudio.id)}`,
      hostEditButton(page),
      "the unmonitored studio's page",
    );
    const quietControl = page.getByRole("button", { name: /^Monitor in Whisparr/ });
    await expect(
      quietControl,
      `the unmonitored studio's control never became pressable within ${String(CONTROL_BUDGET_MS)}ms`,
    ).toBeEnabled({ timeout: CONTROL_BUDGET_MS });
    await quietControl.click();
    const quietMenu = page.getByRole("menu", { name: /^Monitor in Whisparr/ });
    await expect(quietMenu, "the unmonitored control opened no menu").toBeVisible();
    // Bounded by the named window for the reason every absence here is: a row the menu has not
    // rendered yet is indistinguishable from one it never will.
    await page.waitForTimeout(SETTLE_DWELL_MS);
    for (const label of [
      ACTION_ADD_ALL_MISSING,
      ACTION_REFLECT_OWNED,
      ACTION_SEARCH_ALL_MONITORED,
    ]) {
      await expect(
        quietMenu.getByRole("menuitem", { name: label, exact: true }),
        `the unmonitored studio's menu carries a "${label}" row. The three secondary verbs must be ABSENT on an entity nothing monitors, not present and disabled: a disabled row advertises a verb that is not the reader's to ask for yet.`,
      ).toHaveCount(0);
    }
    await page.keyboard.press("Escape");
    await expect(quietMenu, "Escape did not close the unmonitored studio's menu").toBeHidden();

    // Arranged over the API rather than by the gesture its sibling spec already drives: what this
    // spec is about starts at the menu a monitored entity opens.
    const monitored = await coveApi.post(
      extensionRoute(`entity/studio/${String(heldStudio.id)}/monitor`),
      { scope: SCOPE_FUTURE_SCENES_WIRE },
    );
    expect(
      monitored.json?.monitored,
      `arranging the studio as monitored answered ${String(monitored.status)} ${JSON.stringify(monitored.json)}`,
    ).toBe(true);

    // ---- The three rows on the monitored studio, and the one that downloads left unpressed. ----
    await visit(
      page,
      baseUrl,
      `/studio/${String(heldStudio.id)}`,
      hostEditButton(page),
      "the monitored studio's page",
    );
    await expect(
      monitoredControl(page),
      `the control never reported the studio as monitored within ${String(CONTROL_BUDGET_MS)}ms`,
    ).toBeVisible({ timeout: CONTROL_BUDGET_MS });
    await monitoredControl(page).click();
    await expect(monitoredMenu(page), "the monitored control opened no menu").toBeVisible();

    for (const label of [
      ACTION_ADD_ALL_MISSING,
      ACTION_REFLECT_OWNED,
      ACTION_SEARCH_ALL_MONITORED,
    ]) {
      await expect(
        monitoredMenu(page).getByRole("menuitem", { name: label, exact: true }),
        `the monitored studio's menu carries no "${label}" row, so this build mounted a route the menu does not offer`,
      ).toBeVisible();
    }

    // The grabbing verb: PRESSABLE, and that is the whole of what is asserted about it here. A
    // disabled row would mean the route it names is not reachable; a pressed one would spend a real
    // instance's bandwidth, which no spec in this phase does.
    await expect(
      monitoredMenu(page).getByRole("menuitem", { name: ACTION_SEARCH_ALL_MONITORED, exact: true }),
      `the "${ACTION_SEARCH_ALL_MONITORED}" row is disabled on a monitored studio, so the route this run mounted is not reachable from the menu that offers it`,
    ).toBeEnabled();
    await page.keyboard.press("Escape");
    await expect(
      monitoredMenu(page),
      "Escape did not close the monitored menu, so the rows below would be pressed through a menu nobody reopened",
    ).toBeHidden();

    // And the same verb's route in the emitted document, which is where its reachability is a fact
    // about what shipped rather than about what one page rendered.
    const raw = readFileSync(WIRE_DOCUMENT, "utf8");
    // Sliced rather than matched: a byte-order mark breaks JSON.parse, and a regex holding
    // the mark itself is an invisible character in the source.
    const wire = JSON.parse(raw.charCodeAt(0) === 0xfeff ? raw.slice(1) : raw);
    const mounted = Object.keys(wire.paths ?? {});
    for (const verb of ["reflect-owned", "add-all-missing", "search-all-monitored"]) {
      expect(
        mounted.filter((path) => path.endsWith(`/${verb}`)),
        `the emitted wire document declares no route ending in "${verb}", so a row the menu offers is served by nothing`,
      ).toHaveLength(1);
    }

    // ---- Reflect owned, acting. ----
    await setHardLinks(instance, true);
    const reflectOn = await pressSecondary(page, ACTION_REFLECT_OWNED, "reflect-owned");
    expect(
      reflectOn.body?.skipped ?? null,
      `with the instance's hard-link setting ON the reflect-owned route answered skipped=${JSON.stringify(reflectOn.body?.skipped)}, so it declined work the setting permits`,
    ).toBeNull();
    expect(
      reflectOn.body?.jobId,
      `the reflect-owned route answered ${String(reflectOn.status)} ${JSON.stringify(reflectOn.body)} with no job id, so nothing could follow the run it started`,
    ).toBeTruthy();
    const reflectRun = await followJob(coveApi, reflectOn.body.jobId, "the reflect-owned run");
    expect(
      reportedLine(reflectRun, /\d+ linked, \d+ refused/),
      `the reflect-owned run completed and reported no line saying what it linked. A run that reports nothing tells a reader neither what it attached nor that it attached nothing. The whole status was ${JSON.stringify(reflectRun)}`,
    ).toBeTruthy();

    // The menu is still open, which is the product's own rule rather than an accident: every row
    // disables until the state has been read back, so what a reader sees next is what the instance
    // answered instead of the menu they pressed disappearing before anything changed.
    await expect(
      monitoredMenu(page),
      "the menu closed itself over the action, so a reader loses the rows before the state they pressed for has been read back",
    ).toBeVisible();

    // ---- Reflect owned, skipped, and the notice that says so. ----
    await setHardLinks(instance, false);
    const importsBefore = await commandCount(instance, MANUAL_IMPORT_COMMAND);
    const reflectOff = await pressSecondary(page, ACTION_REFLECT_OWNED, "reflect-owned");
    expect(
      reflectOff.body?.skipped,
      `with the instance's hard-link setting OFF the reflect-owned route answered ${JSON.stringify(reflectOff.body)}; the skip is what keeps a file from being copied rather than linked`,
    ).toBe("hardLinksOff");
    expect(
      reflectOff.body?.jobId ?? null,
      "the skipped reflect-owned route still answered a job id, so a run was enqueued for work it had already declined",
    ).toBeNull();

    const skipNotice = page.getByRole("status").filter({ hasText: REFLECT_OWNED_SKIPPED });
    await expect(
      skipNotice,
      "the skipped action left no notice at the control, so a reader who pressed the row is told nothing happened and not why",
    ).toBeVisible({ timeout: GESTURE_BUDGET_MS });

    // WR-09's unsettled half. The notice portals to the document because the host's entity hero
    // clips its children; whether that escape works is the hero's own geometry and only a rendered
    // page has it. Both halves are asserted: that a clipping ancestor EXISTS, so the escape is about
    // something, and that the notice is not inside it.
    //
    // The escape is read as an ascent to the body rather than as a parent check, because with the
    // menu open the notice is a flow sibling of the `role="menu"` element inside the one anchored
    // container: its own parent is that container, and the container is the child of the body.
    const escape = await page.evaluate((sentence) => {
      const trigger = Array.from(document.querySelectorAll("button")).find((button) =>
        (button.getAttribute("aria-label") ?? "").startsWith("Monitored in Whisparr"),
      );
      const notice = Array.from(document.querySelectorAll('[role="status"]')).find((node) =>
        (node.textContent ?? "").includes(sentence),
      );
      if (trigger === undefined || notice === undefined) return null;

      // The ancestor-or-self the node reaches the body through, and how far below the body it sits.
      const ascent = (node) => {
        const chain = [];
        let root = node;
        while (root.parentElement !== null && root.parentElement !== document.body) {
          root = root.parentElement;
          chain.push(String(root.className).slice(0, 140));
        }
        const classes = String(root.className).split(/\s+/);
        return {
          depthBelowBody: chain.length,
          chain,
          rootClassName: String(root.className).slice(0, 140),
          rootIsAnchoredContainer: ["fixed", "z-50", "w-72"].every((name) =>
            classes.includes(name),
          ),
        };
      };

      const clipping = [];
      let insideAClipper = false;
      for (let node = trigger.parentElement; node !== null; node = node.parentElement) {
        const style = getComputedStyle(node);
        const clips =
          style.overflow === "hidden" ||
          style.overflowX === "hidden" ||
          style.overflowY === "hidden";
        if (!clips) continue;
        const box = node.getBoundingClientRect();
        clipping.push({
          className: String(node.className).slice(0, 140),
          top: Math.round(box.top),
          bottom: Math.round(box.bottom),
          left: Math.round(box.left),
          right: Math.round(box.right),
          // Whether this ancestor is a containing block for a fixed child. Where none of these
          // holds, `position: fixed` already escapes the clip and the portal is what makes that
          // independent of the host's own styling rather than what achieves it.
          containsFixed:
            style.transform !== "none" || style.filter !== "none" || style.willChange !== "auto",
        });
        if (node.contains(notice)) insideAClipper = true;
      }

      const box = notice.getBoundingClientRect();
      const rectangle = (node) => {
        const measured = node.getBoundingClientRect();
        return {
          top: Math.round(measured.top),
          bottom: Math.round(measured.bottom),
          height: Math.round(measured.height),
        };
      };
      // The panel the notice shares its container with, so a notice drawn past the viewport names
      // what pushed it there rather than only where it landed.
      const menu = document.querySelector('[role="menu"]');
      const container = menu === null ? null : menu.parentElement;

      return {
        noticeAscent: ascent(notice),
        triggerAscent: ascent(trigger),
        insideAClipper,
        clipping,
        notice: {
          top: Math.round(box.top),
          bottom: Math.round(box.bottom),
          left: Math.round(box.left),
          right: Math.round(box.right),
        },
        panel:
          menu === null
            ? null
            : {
                menu: rectangle(menu),
                menuOverflowY: getComputedStyle(menu).overflowY,
                menuScrolls: menu.scrollHeight > menu.clientHeight,
                menuScrollHeight: menu.scrollHeight,
                menuClientHeight: menu.clientHeight,
                container: container === null ? null : rectangle(container),
                // The bound is on the container, because the container is what has to fit the
                // room below the trigger: the notice takes its own height out of that room.
                containerMaxHeight: container === null ? "" : container.style.maxHeight,
                noticeSharesTheContainer: notice.parentElement === container,
              },
        viewport: { width: window.innerWidth, height: window.innerHeight },
      };
    }, REFLECT_OWNED_SKIPPED);

    expect(
      escape,
      "neither the monitored control nor its notice could be found in the page, so nothing below is about where the notice renders",
    ).not.toBeNull();
    expect(
      escape.clipping.length,
      "the host's entity hero has no clipping ancestor above the control on this image, so the portal this notice renders through is guarding against nothing and the guard's own reason has gone stale",
    ).toBeGreaterThan(0);
    expect(
      escape.insideAClipper,
      `the notice renders inside a clipping ancestor of the control (${JSON.stringify(escape.clipping)}), so the host's hero cuts it off with nothing to see and no error`,
    ).toBe(false);
    expect(
      escape.noticeAscent.depthBelowBody,
      `the notice sits ${escape.noticeAscent.depthBelowBody} elements below the body through ${JSON.stringify(escape.noticeAscent.chain)}, so it renders inside the page's own tree rather than in the anchored container it shares with the menu`,
    ).toBeLessThanOrEqual(1);
    expect(
      escape.noticeAscent.rootIsAnchoredContainer,
      `the notice reaches the body through an element carrying ${JSON.stringify(escape.noticeAscent.rootClassName)} rather than the anchored container's own classes, so it did not leave the hero through the portal path the menu uses`,
    ).toBe(true);
    // The same ascent applied to the control, which the host mounts inside its hero. A check that
    // reported an escape for every node would report one here too.
    expect(
      escape.triggerAscent.depthBelowBody,
      `the control the host mounted in its hero reads as ${escape.triggerAscent.depthBelowBody} elements below the body, so the ascent above cannot tell a portaled node from a nested one`,
    ).toBeGreaterThan(1);

    // What the source reading could not settle is whether the notice is where a reader can read it.
    // Playwright's own visibility is a non-empty box and a visible style; neither says an ancestor is
    // not cutting the box away. So the rect is read against the viewport, which is the frame a reader
    // actually has. The geometry is logged rather than asserted into a shape: the notice's position
    // is the host hero's to decide, and pinning it here would fail on a host that moved its own row.
    console.log(`the notice's own geometry: ${JSON.stringify(escape)}`);
    expect(
      escape.notice.top >= 0 &&
        escape.notice.left >= 0 &&
        escape.notice.bottom <= escape.viewport.height &&
        escape.notice.right <= escape.viewport.width,
      `the notice is drawn at ${JSON.stringify(escape.notice)} in a ${JSON.stringify(escape.viewport)} viewport, so part of it is off screen and a reader cannot read what the gesture did. The panel it shares its container with: ${JSON.stringify(escape.panel)}`,
    ).toBe(true);
    // Regression guards rather than discriminators: both already held before the notice was given
    // its own room. The panel is what the room is taken from, so it is the part that could be
    // driven off screen or left unscrollable by a change to how the room is divided.
    expect(
      escape.panel.menu.top >= 0 && escape.panel.menu.bottom <= escape.viewport.height,
      `the menu is drawn at ${JSON.stringify(escape.panel.menu)} in a ${JSON.stringify(escape.viewport)} viewport, so part of it is off screen`,
    ).toBe(true);
    expect(
      escape.panel.menuOverflowY,
      `the menu resolves overflow-y to ${escape.panel.menuOverflowY} while holding ${String(escape.panel.menuScrollHeight)}px of rows in ${String(escape.panel.menuClientHeight)}px, so a row past the bound cannot be reached with a pointer`,
    ).toBe("auto");

    // And nothing was asked of the instance for a verb it declined.
    await page.waitForTimeout(SETTLE_DWELL_MS);
    expect(
      await commandCount(instance, MANUAL_IMPORT_COMMAND),
      `the instance gained a ${MANUAL_IMPORT_COMMAND} command across a reflect-owned press the route reported as skipped, so the skip did not stop the work`,
    ).toBe(importsBefore);

    // ---- Add all missing, on a generation that carries it. ----
    await setHardLinks(instance, true);
    const scenesBefore = await instance.get("/api/v3/scene");
    const addAll = await pressSecondary(page, ACTION_ADD_ALL_MISSING, "add-all-missing");
    expect(
      addAll.body?.jobId,
      `the add-all-missing route answered ${String(addAll.status)} ${JSON.stringify(addAll.body)} with no job id`,
    ).toBeTruthy();
    const addAllRun = await followJob(coveApi, addAll.body.jobId, "the add-all-missing run");
    expect(
      reportedLine(
        addAllRun,
        /\d+ registered, \d+ already held, \d+ refused|carries an identifier/,
      ),
      `the add-all-missing run completed and reported no line saying what it offered the instance. The whole status was ${JSON.stringify(addAllRun)}`,
    ).toBeTruthy();

    // The load-bearing half: registering a scene is not acquiring one. Whatever the instance did
    // with the offer, it queued no transfer.
    const activityAfterAdd = await whisparrActivity(instance);
    expect(
      activityAfterAdd.queueTotal,
      `the instance's queue holds ${String(activityAfterAdd.queueTotal)} record(s) after add all missing, so registering a scene started a transfer`,
    ).toBe(0);
    // What the instance chose to answer the offer with is recorded rather than asserted: a synthetic
    // identifier is one its own metadata source cannot resolve, and 53-16 measured that build
    // refusing exactly that. So the catalogue is read for a DECREASE, which no outcome of this verb
    // may produce, rather than for a gain this fixture cannot honestly produce.
    const scenesAfter = await instance.get("/api/v3/scene");
    expect(
      (Array.isArray(scenesAfter.json) ? scenesAfter.json : []).length,
      "the instance's scene catalogue shrank across add all missing, which registers and never removes",
    ).toBeGreaterThanOrEqual((Array.isArray(scenesBefore.json) ? scenesBefore.json : []).length);

    // ---- The whole spec, and no search anywhere in it. ----
    await page.waitForTimeout(SETTLE_DWELL_MS);
    const settled = await whisparrActivity(instance);
    expect(
      settled.commandNames.filter((name) => SEARCH_COMMAND.test(name)),
      `the instance's command roster holds a searching command after a spec that presses two of the three verbs and never the third. The whole roster was ${JSON.stringify(settled.commandNames)}`,
    ).toEqual([]);
  } finally {
    // Before the harness's own stop: the daemon refuses to remove a network a container still holds
    // an endpoint on, and that failure names neither this spec nor its cause.
    await whisparr.stop();
  }
});
