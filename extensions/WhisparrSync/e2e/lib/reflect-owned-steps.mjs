// The steps a spec takes to get a reflect-owned run started and follow it to a settled state.
//
// None of these asserts anything about the folder this product hands an instance. They turn the
// instance's hard-link setting on, monitor the seeded studio, press the row and watch the job: the
// arrangement a spec needs before its own assertions begin. A copy in every spec that drives a run
// drifts, and a drifted copy is a spec doing something slightly different from the one beside it for
// no reason anybody chose.
import { attemptUntil, pollUntil } from "@cove-extensions/e2e/poll";

import {
  ACTION_REFLECT_OWNED,
  SCOPE_FUTURE_SCENES,
  WHISPARR_MONITORED,
  WHISPARR_NOT_MONITORED,
} from "../../src/WhisparrSync.Ui/src/common/ui/copy.ts";
import { expect, extensionRoute } from "./connected-fixture.mjs";
import { visit } from "./steps.mjs";

const HARD_LINK_SETTING = "copyUsingHardlinks";
const MEDIA_MANAGEMENT_PATH = "/api/v3/config/mediamanagement";

const CONTROL_BUDGET_MS = 60_000;
const GESTURE_BUDGET_MS = 60_000;
const JOB_BUDGET_MS = 120_000;

/** The menu a monitored entity's control opens. */
export const monitoredMenu = (page) => page.getByRole("menu", { name: WHISPARR_MONITORED });

/**
 * Turns the instance's hard-link setting on.
 *
 * A spec taking it on is one whose outcome is decided by the path rather than by the setting. The
 * whole resource is read and written back with one member changed: these config routes REPLACE what
 * they are sent, so a body carrying only the one flag would blank the rest.
 */
export async function linkIntoPlace(instance) {
  const current = await instance.get(MEDIA_MANAGEMENT_PATH);
  if (current.status !== 200) {
    throw new Error(
      `linkIntoPlace: GET ${MEDIA_MANAGEMENT_PATH} answered ${String(current.status)}, so the setting this verb reads could not be arranged`,
    );
  }
  await instance.put(MEDIA_MANAGEMENT_PATH, {
    ...current.json,
    [HARD_LINK_SETTING]: true,
    enableMediaInfo: false,
  });
  const read = await instance.get(MEDIA_MANAGEMENT_PATH);
  if (read.json?.[HARD_LINK_SETTING] !== true) {
    throw new Error(
      `linkIntoPlace: the instance reports ${HARD_LINK_SETTING}=${JSON.stringify(read.json?.[HARD_LINK_SETTING])}, so the run below is not about the path`,
    );
  }
}

/** Monitors the seeded studio from its own page, and waits for the instance's row to say so. */
export async function monitorStudio(page, baseUrl, studio, studioMonitored) {
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
}

/**
 * Re-opens the monitored studio's menu, for a spec driving a second run after leaving the page.
 *
 * {@link monitorStudio} leaves the menu open, so a first run needs none of this.
 */
export async function openMonitoredMenu(page, baseUrl, studio) {
  const control = page.getByRole("button", { name: new RegExp(`^${WHISPARR_MONITORED}`) }).first();
  await visit(page, baseUrl, `/studio/${String(studio.id)}`, control, "the studio detail page");
  await control.click();
  await expect(
    monitoredMenu(page),
    "the monitored control opened no menu, so the rows it offers are out of reach",
  ).toBeVisible({ timeout: CONTROL_BUDGET_MS });
}

/** Presses the reflect-owned row and answers what the browser itself received. */
export async function pressReflectOwned(page) {
  const answered = page.waitForResponse(
    (response) => new URL(response.url()).pathname.endsWith("/reflect-owned"),
    { timeout: GESTURE_BUDGET_MS },
  );
  const row = monitoredMenu(page).getByRole("menuitem", {
    name: ACTION_REFLECT_OWNED,
    exact: true,
  });
  await expect(row, `the menu offers no "${ACTION_REFLECT_OWNED}" row`).toBeVisible();
  await row.click();

  const response = await answered;
  const body = await response.json().catch(() => null);
  expect(
    body?.jobId,
    `the route answered ${String(response.status())} ${JSON.stringify(body)} with no job id, so nothing could follow the run it started`,
  ).toBeTruthy();
  return body;
}

/** Follows one enqueued run to a settled state through the extension's own status route. */
export async function followJob(api, jobId) {
  const { settled, value, note } = await attemptUntil(
    async (_signal, record) => {
      const status = await api.get(extensionRoute(`job-status/${String(jobId)}`));
      record(`${String(status.status)} with state ${String(status.json?.status ?? "absent")}`);
      return status.json?.status === "completed" ? { value: status.json } : null;
    },
    { timeoutMs: JOB_BUDGET_MS, intervalMs: 1_000, label: "the reflect-owned run" },
  );
  expect(
    settled,
    `the reflect-owned run never reported itself complete within ${String(JOB_BUDGET_MS)}ms; its status route last answered ${note}`,
  ).toBe(true);
  expect(value?.error ?? null, `the linking run faulted: ${String(value?.error)}`).toBeNull();
  return value;
}

/**
 * The line a settled run reports what it did on.
 *
 * The run puts its line on the final progress report's sub-task, because the host's progress carries
 * no summary field of its own, so which field a reader finds it in is the host's business.
 */
export const reportedLine = (status) =>
  [status.subTask, status.summary].find((line) => typeof line === "string" && line.length > 0);
