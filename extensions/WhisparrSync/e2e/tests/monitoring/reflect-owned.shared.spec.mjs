// Handing the instance a file the library already holds, from the menu, on every generation that
// holds the verb.
//
// One scenario, collected once per generation, each execution against an installation of its own.
// The generation is a fixture option rather than anything this body reads: the fixture starts the
// instance, seeds the catalogue, places the owned file under the entry that generation carries and
// supplies the reads, so the gestures and the assertions below are the same words whichever
// generation is connected.
//
// ONE FILESYSTEM AT ONE PATH. The verb hands the instance a folder the LIBRARY names, so an instance
// mounting Cove's volume anywhere else is handed a path it cannot read: it links nothing and the run
// still completes reporting no failure. The fixture's `ownedMedia` option is what arranges that.
//
// WHAT THE EVIDENCE IS. The instance's own file rows, never this product's answer and never the line
// the run reports about itself. "0 linked, 0 refused" satisfies the line, so a spec resting on it
// passes a run that handed over nothing.
//
// BOTH HALVES OF THE SETTING. Linking is what the gesture does, and the instance's own hard-link
// setting decides whether it links or copies. With it off the product declines rather than doubling
// the disk, and the notice it leaves is the reader's only word that nothing happened and why. That
// notice renders through a portal because the host clips its entity hero, and whether the escape
// works is the hero's own geometry: only a rendered page has it.
//
// THE SENTENCES ARE IMPORTED FROM THE SHIPPED COPY MODULE, because here they are LOCATORS: which row
// to press and which notice to find. A locator built from a hand-copied literal stops finding its
// row the day the row is renamed, silently.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { attemptUntil, pollUntil } from "@cove-extensions/e2e/poll";

import {
  ACTION_REFLECT_OWNED,
  REFLECT_OWNED_SKIPPED,
  SCOPE_FUTURE_SCENES,
  WHISPARR_MONITORED,
  WHISPARR_NOT_MONITORED,
} from "../../../src/WhisparrSync.Ui/src/common/ui/copy.ts";
import {
  expect,
  extensionRoute,
  SETTLE_DWELL_MS,
  SPEC_BUDGET_MS,
  test,
} from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

// The instance setting the verb reads before it links anything, and where it is read.
const HARD_LINK_SETTING = "copyUsingHardlinks";
const MEDIA_MANAGEMENT_PATH = "/api/v3/config/mediamanagement";

// The command the linking work issues, so the skipped case can assert the instance was asked for
// nothing.
const MANUAL_IMPORT_COMMAND = "ManualImport";

const CONTROL_BUDGET_MS = 60_000;
const GESTURE_BUDGET_MS = 60_000;

// One attempt at getting the menu open, not the whole gesture: the block around it retries, and a
// long budget here would spend the gesture's own on a single attempt.
const MENU_OPEN_BUDGET_MS = 5_000;
const JOB_BUDGET_MS = 120_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

const monitoredMenu = (page) => page.getByRole("menu", { name: WHISPARR_MONITORED });

const monitoredControl = (page) =>
  page.getByRole("button", { name: new RegExp(`^${WHISPARR_MONITORED}`) }).first();

/**
 * Turns the instance's hard-link setting on or off.
 *
 * The whole resource is read and written back with one member changed: these config routes REPLACE
 * what they are sent, so a body carrying only the one flag would blank the rest. The read-back is
 * what says the case below is the one it names.
 */
async function setHardLinks(instance, on) {
  const current = await instance.get(MEDIA_MANAGEMENT_PATH);
  if (current.status !== 200) {
    throw new Error(
      `setHardLinks: GET ${MEDIA_MANAGEMENT_PATH} answered ${String(current.status)}, so the setting this verb reads could not be arranged`,
    );
  }
  const written = await instance.put(MEDIA_MANAGEMENT_PATH, {
    ...current.json,
    [HARD_LINK_SETTING]: on,
    enableMediaInfo: false,
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
async function commandCount(adapter, instance, named) {
  const { commandNames } = await adapter.activity(instance);
  return commandNames.filter((name) => name === named).length;
}

/**
 * Presses one row of a monitored entity's menu and answers what the browser itself received.
 *
 * The menu deliberately STAYS OPEN across an action, so what a reader sees next is the state the
 * instance answered rather than the menu they pressed vanishing before anything changed. A control
 * press is a toggle, so pressing it unconditionally here would close the menu a previous action left
 * open.
 */
async function pressRow(page, label, route) {
  const answered = page.waitForResponse(
    (response) => new URL(response.url()).pathname.endsWith(`/${route}`),
    { timeout: GESTURE_BUDGET_MS },
  );
  // Retried as a block. `isVisible` reads at an instant, so a menu caught part way through opening
  // reads as closed and the press below shuts it; retrying reopens it rather than failing on a menu
  // this helper closed itself.
  await expect(async () => {
    if (!(await monitoredMenu(page).isVisible())) {
      await monitoredControl(page).click();
    }
    await expect(
      monitoredMenu(page),
      `the monitored control did not open its menu before "${label}" could be pressed`,
    ).toBeVisible({ timeout: MENU_OPEN_BUDGET_MS });
  }).toPass({ timeout: GESTURE_BUDGET_MS });
  const row = monitoredMenu(page).getByRole("menuitem", { name: label, exact: true });
  await expect(row, `the menu offers no "${label}" row`).toBeVisible();
  await expect(row, `the "${label}" row is not pressable on a monitored studio`).toBeEnabled();
  await row.click();

  const response = await answered;
  return { status: response.status(), body: await response.json().catch(() => null) };
}

/** Follows one enqueued run to a settled state through the extension's own status route. */
async function followJob(api, jobId, label) {
  const { settled, value, note } = await attemptUntil(
    async (_signal, record) => {
      const status = await api.get(extensionRoute(`job-status/${String(jobId)}`));
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
 * Read from whichever field of the status carries it. These runs put their line on the final
 * progress report's sub-task, because the host's progress carries no summary field of its own, so
 * which field a reader finds it in is the host's business. What is asserted is that the run said
 * what it did.
 */
function reportedLine(status, shape) {
  return [status.subTask, status.summary].find(
    (line) => typeof line === "string" && shape.test(line),
  );
}

/**
 * Where the notice renders relative to the control the host mounted inside its clipped hero.
 *
 * The escape is read as an ascent to the body rather than as a parent check: with the menu open the
 * notice is a flow sibling of the `role="menu"` element inside the one anchored container, so its
 * own parent is that container and the container is the child of the body.
 */
const readNoticeEscape = (page, sentence, controlName) =>
  page.evaluate(
    ([text, name]) => {
      const trigger = Array.from(document.querySelectorAll("button")).find((button) =>
        (button.getAttribute("aria-label") ?? "").startsWith(name),
      );
      const notice = Array.from(document.querySelectorAll('[role="status"]')).find((node) =>
        (node.textContent ?? "").includes(text),
      );
      if (trigger === undefined || notice === undefined) return null;

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
          rootIsAnchoredContainer: ["fixed", "z-50", "w-72"].every((one) => classes.includes(one)),
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
          // Whether this ancestor is a containing block for a fixed child. Where none of these
          // holds, `position: fixed` already escapes the clip and the portal is what makes that
          // independent of the host's own styling rather than what achieves it.
          containsFixed:
            style.transform !== "none" || style.filter !== "none" || style.willChange !== "auto",
        });
        if (node.contains(notice)) insideAClipper = true;
      }

      const rectangle = (node) => {
        const measured = node.getBoundingClientRect();
        return {
          top: Math.round(measured.top),
          bottom: Math.round(measured.bottom),
          height: Math.round(measured.height),
        };
      };
      const box = notice.getBoundingClientRect();
      // The panel the notice shares its container with, so a notice drawn past the viewport names
      // what pushed it there rather than only where it landed.
      const menu = document.querySelector('[role="menu"]');

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
                menuScrollHeight: menu.scrollHeight,
                menuClientHeight: menu.clientHeight,
              },
        viewport: { width: window.innerWidth, height: window.innerHeight },
      };
    },
    [sentence, controlName],
  );

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    // Only this generation's installation and instance, and the library's own volume under the
    // instance's catalogue root, which is what this verb needs and the other scenarios do not.
    test.use({ generation, ownedMedia: true });

    test("the menu hands the instance a file the library holds, and says so when the instance's own setting forbids it", async ({
      page,
      baseUrl,
      connected,
    }) => {
      const { adapter, api, instance, owned, run, studio, studioMonitored } = connected;

      await setHardLinks(instance, true);

      // The bound on the claim below, read off the instance rather than assumed. An entry already
      // carrying a file would make its rows after the run indistinguishable from its rows before it.
      expect(
        await adapter.ownedFileRows(instance, owned.entryId),
        "the seeded entry already holds a file, so a file found after the run would prove nothing",
      ).toEqual([]);

      // The verb is offered only on an entity the instance already monitors, so the monitoring
      // gesture comes first. The narrower scope, which marks no back-catalogue wanted.
      const control = page
        .getByRole("button", { name: new RegExp(`^${WHISPARR_NOT_MONITORED}`) })
        .first();
      await visit(page, baseUrl, `/studio/${String(studio.id)}`, control, "the studio detail page");
      await expect(
        control,
        `the control does not report the seeded studio as unmonitored within ${String(CONTROL_BUDGET_MS)}ms, so the read did not reach the instance`,
      ).toBeVisible({ timeout: CONTROL_BUDGET_MS });
      await control.click();
      const menu = page.getByRole("menu", { name: WHISPARR_NOT_MONITORED });
      await expect(menu, "the control opened no menu").toBeVisible({ timeout: CONTROL_BUDGET_MS });
      await menu.getByRole("menuitemradio", { name: SCOPE_FUTURE_SCENES, exact: true }).click();
      await pollUntil(studioMonitored, (monitored) => monitored === true, {
        timeoutMs: GESTURE_BUDGET_MS,
        label: "the instance's own row after the monitor gesture",
      });
      await expect(
        monitoredMenu(page),
        "the menu closed on the monitor gesture, so the rows a monitored entity offers are out of reach",
      ).toBeVisible({ timeout: GESTURE_BUDGET_MS });

      // ---- The setting permits it, so the file is handed over. ----
      const reflectOn = await pressRow(page, ACTION_REFLECT_OWNED, "reflect-owned");
      expect(
        reflectOn.body?.skipped ?? null,
        `with the instance's hard-link setting ON the route answered skipped=${JSON.stringify(reflectOn.body?.skipped)}, so it declined work the setting permits`,
      ).toBeNull();
      expect(
        reflectOn.body?.jobId,
        `the route answered ${String(reflectOn.status)} ${JSON.stringify(reflectOn.body)} with no job id, so nothing could follow the run it started`,
      ).toBeTruthy();

      // The gesture enqueues and answers; the linking happens in the run it started. So the answer
      // says only that the run was accepted, and what it did is read off the instance once it is
      // done.
      const reflectRun = await followJob(api, reflectOn.body.jobId, "the reflect-owned run");
      expect(
        reflectRun?.error ?? null,
        `the linking run faulted: ${String(reflectRun?.error)}`,
      ).toBeNull();
      expect(
        reportedLine(reflectRun, /\d+ linked, \d+ refused/),
        `the run completed and reported no line saying what it linked. A run that reports nothing tells a reader neither what it attached nor that it attached nothing. The whole status was ${JSON.stringify(reflectRun)}`,
      ).toBeTruthy();

      const {
        settled: linked,
        value: linkedFiles,
        note: linkedNote,
      } = await attemptUntil(
        async (_signal, record) => {
          const rows = await adapter.ownedFileRows(instance, owned.entryId);
          record(`${String(rows.length)} file row(s)`);
          return rows.length > 0 ? { value: rows } : null;
        },
        {
          timeoutMs: JOB_BUDGET_MS,
          intervalMs: 1_000,
          label: "the instance's own file rows for the seeded entry",
        },
      );
      expect(
        linked,
        `the run reported "${String(reportedLine(reflectRun, /\d+ linked, \d+ refused/))}" and the instance holds no file for the entry the library named; its file rows last read ${linkedNote}`,
      ).toBe(true);
      const paths = linkedFiles.map((row) => String(row.relativePath ?? row.path));
      expect(
        paths.filter((path) => path.includes(run)).length,
        `the instance holds ${String(paths.length)} file(s) for the entry, which are not the one file the library named: ${paths.join(", ")}`,
      ).toBe(1);

      // The entry's own state and its file rows are separate facts: a file can be registered and
      // attached to nothing, which leaves the entry still reading as one the instance holds none for.
      await pollUntil(
        () => adapter.ownedEntryHoldsFile(instance, owned.entryId),
        (holds) => holds === true,
        {
          timeoutMs: GESTURE_BUDGET_MS,
          intervalMs: 2_000,
          label: "the instance's own catalogue entry reads as holding a file",
        },
      );

      // The menu is still open, which is the product's own rule rather than an accident: every row
      // disables until the state has been read back, so what a reader sees next is what the instance
      // answered instead of the menu they pressed disappearing before anything changed.
      await expect(
        monitoredMenu(page),
        "the menu closed itself over the action, so a reader loses the rows before the state they pressed for has been read back",
      ).toBeVisible();

      // ---- The setting forbids it, so the verb is declined and the notice says so. ----
      await setHardLinks(instance, false);
      const importsBefore = await commandCount(adapter, instance, MANUAL_IMPORT_COMMAND);
      const reflectOff = await pressRow(page, ACTION_REFLECT_OWNED, "reflect-owned");
      expect(
        reflectOff.body?.skipped,
        `with the instance's hard-link setting OFF the route answered ${JSON.stringify(reflectOff.body)}; the skip is what keeps a file from being copied rather than linked`,
      ).toBe("hardLinksOff");
      expect(
        reflectOff.body?.jobId ?? null,
        "the skipped route still answered a job id, so a run was enqueued for work it had already declined",
      ).toBeNull();

      const skipNotice = page.getByRole("status").filter({ hasText: REFLECT_OWNED_SKIPPED });
      await expect(
        skipNotice,
        "the skipped action left no notice at the control, so a reader who pressed the row is told nothing happened and not why",
      ).toBeVisible({ timeout: GESTURE_BUDGET_MS });

      // Both halves of the escape: that a clipping ancestor EXISTS, so the escape is about
      // something, and that the notice is not inside it.
      const escape = await readNoticeEscape(page, REFLECT_OWNED_SKIPPED, WHISPARR_MONITORED);
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
        `the notice sits ${String(escape.noticeAscent.depthBelowBody)} elements below the body through ${JSON.stringify(escape.noticeAscent.chain)}, so it renders inside the page's own tree rather than in the anchored container it shares with the menu`,
      ).toBeLessThanOrEqual(1);
      expect(
        escape.noticeAscent.rootIsAnchoredContainer,
        `the notice reaches the body through an element carrying ${JSON.stringify(escape.noticeAscent.rootClassName)} rather than the anchored container's own classes, so it did not leave the hero through the portal path the menu uses`,
      ).toBe(true);
      // The same ascent applied to the control, which the host mounts inside its hero. A check that
      // reported an escape for every node would report one here too.
      expect(
        escape.triggerAscent.depthBelowBody,
        `the control the host mounted in its hero reads as ${String(escape.triggerAscent.depthBelowBody)} elements below the body, so the ascent above cannot tell a portaled node from a nested one`,
      ).toBeGreaterThan(1);

      // Playwright's own visibility is a non-empty box and a visible style; neither says an ancestor
      // is not cutting the box away. So the rect is read against the viewport, which is the frame a
      // reader actually has. The geometry is logged rather than pinned to a shape: where the notice
      // sits is the host hero's to decide.
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

      // And nothing was asked of the instance for a verb it declined. Watched over the named window
      // rather than read the moment the press returned: an absence bounded by whatever delay the
      // gesture happened to have passes on a broken instance as readily as on a correct one.
      await page.waitForTimeout(SETTLE_DWELL_MS);
      expect(
        await commandCount(adapter, instance, MANUAL_IMPORT_COMMAND),
        `the instance gained a ${MANUAL_IMPORT_COMMAND} command across a press the route reported as skipped, so the skip did not stop the work`,
      ).toBe(importsBefore);
    });
  });
}
