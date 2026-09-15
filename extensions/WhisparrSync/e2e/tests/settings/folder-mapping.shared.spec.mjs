// The path an operator states for a library folder Whisparr could not be shown to hold, driven from
// the settings page against a running pair whose mounts differ on purpose.
//
// WHAT THIS ESTABLISHES. That a stated path is refused until a probe resolves the folder under it,
// and that one which does resolve makes the next run hand the instance the file. The evidence for
// the second half is the instance's own file rows for the seeded entry, read through the generation
// adapter: not this product's answer and not the line the run reports about itself.
//
// WHAT IT DOES NOT ESTABLISH. Nothing about more than one candidate resolving. A stated path builds
// exactly one candidate, so that outcome cannot arise through the save at all, and this harness has
// one filesystem and could not arrange two places holding the file even where it could.
//
// THE ARRANGEMENT. The instance mounts the library's volume at a path of its own and roots its
// catalogue under it, and no Cove library path names that root. Every path the product forms from
// the roots the instance declares is therefore one the instance cannot open, which is what leaves
// the first run with a folder nothing resolved for and the prompt with something to ask about.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the
// product. A red end-to-end run in this repository is usually the Cove container dying rather than
// the code under test.
import { attemptUntil } from "@cove-extensions/e2e/poll";
import { WHISPARR_DATA_MOUNT } from "@cove-extensions/e2e/whisparr";

import {
  FOLDER_AGREEMENT_SAVE,
  FOLDER_NOTHING_RESOLVED,
} from "../../../src/WhisparrSync.Ui/src/common/ui/copy.ts";
import { expect, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import { SETTINGS_PAGE_PATH } from "../../lib/contract.mjs";
import {
  followJob,
  linkIntoPlace,
  monitorStudio,
  openMonitoredMenu,
  pressReflectOwned,
  reportedLine,
} from "../../lib/reflect-owned-steps.mjs";
import { visit } from "../../lib/steps.mjs";

/** The volume as Cove reaches it, which is the library root the seeded file sits under. */
const COVE_SHARED = "/shared";

/** A path the instance holds nothing at, so the save under it is refused rather than stored. */
const WRONG_MOUNT = "/nowhere/at/all";

const CONTROL_BUDGET_MS = 60_000;
const JOB_BUDGET_MS = 120_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

/** The prompt for one Cove library folder, which the section keys by the folder it is about. */
const promptFor = (page, root) => page.locator(`li[data-root="${root}"]`);

/** Opens the settings page and waits for the prompt for `root` to be on it. */
async function visitPrompts(page, baseUrl, root) {
  const prompt = promptFor(page, root);
  await visit(page, baseUrl, SETTINGS_PAGE_PATH, prompt, "the settings page");
  await expect(
    prompt,
    `the settings page shows no prompt for ${root} within ${String(CONTROL_BUDGET_MS)}ms, so the run recorded nothing about it or the section never read what it recorded`,
  ).toBeVisible({ timeout: CONTROL_BUDGET_MS });
  return prompt;
}

/** Types `path` under one prompt and presses its own save, answering what the browser received. */
async function statePath(page, prompt, path) {
  const answered = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname.endsWith("/addressing/folder-mappings") &&
      response.request().method() === "PUT",
    { timeout: CONTROL_BUDGET_MS },
  );
  await prompt.getByRole("textbox").fill(path);
  await prompt.getByRole("button", { name: FOLDER_AGREEMENT_SAVE }).click();

  const response = await answered;
  const body = await response.json().catch(() => null);
  expect(
    body?.outcome,
    `the save answered ${String(response.status())} ${JSON.stringify(body)} with no outcome, so what it did about ${path} is not established`,
  ).toBeTruthy();
  return body;
}

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    test.use({ generation, ownedMedia: true, instanceMount: WHISPARR_DATA_MOUNT });

    test("a path stated at the settings page is refused until it resolves, and then the run links", async ({
      page,
      baseUrl,
      connected,
    }) => {
      const { adapter, api, instance, owned, run, studio, studioMonitored } = connected;

      await linkIntoPlace(instance);

      // The bound on the claim below, read off the instance. An entry already carrying a file would
      // make its rows after the run indistinguishable from its rows before it.
      expect(
        await adapter.ownedFileRows(instance, owned.entryId),
        "the seeded entry already holds a file, so a file found after the run would prove nothing",
      ).toEqual([]);

      await monitorStudio(page, baseUrl, studio, studioMonitored);
      const first = await followJob(api, (await pressReflectOwned(page)).jobId);
      expect(
        reportedLine(first),
        `the first run reported "${String(reportedLine(first))}", which does not say it could address no folder, so there is nothing for the settings page to ask about`,
      ).toMatch(/could be linked: /);

      const prompt = await visitPrompts(page, baseUrl, COVE_SHARED);
      const asked = await prompt.textContent();
      expect(
        asked,
        `the prompt for ${COVE_SHARED} reads "${String(asked)}" rather than saying nothing resolved for it`,
      ).toContain(FOLDER_NOTHING_RESOLVED);
      // The seeded file carries this execution's own id, so a prompt naming it names the path the
      // instance was really asked about rather than any path at all.
      expect(
        asked,
        `the prompt for ${COVE_SHARED} names no path the instance could not see, so a reader is told nothing they can act on`,
      ).toContain(run);

      const refused = await statePath(page, prompt, WRONG_MOUNT);
      expect(
        refused.outcome,
        `stating ${WRONG_MOUNT} answered ${JSON.stringify(refused)}; the instance holds nothing there, so nothing may be stored on the strength of it having been typed`,
      ).toBe("refused");
      await expect(
        prompt,
        `the prompt for ${COVE_SHARED} went on a save that resolved nothing`,
      ).toBeVisible();
      await expect(
        prompt,
        `the prompt does not name ${WRONG_MOUNT} after the save, so a reader is not told what was put to the instance`,
      ).toContainText(WRONG_MOUNT);

      const stored = await statePath(page, prompt, WHISPARR_DATA_MOUNT);
      expect(
        stored.outcome,
        `stating the instance's own mount answered ${JSON.stringify(stored)}; the instance holds the library's volume there, so the probe resolves`,
      ).toBe("stored");
      await expect(
        prompt,
        `the prompt for ${COVE_SHARED} is still standing after a path that resolved, so the run's own answer about it was not cleared`,
      ).toBeHidden({ timeout: CONTROL_BUDGET_MS });

      await openMonitoredMenu(page, baseUrl, studio);
      const second = await followJob(api, (await pressReflectOwned(page)).jobId);

      const {
        settled: linked,
        value: rows,
        note,
      } = await attemptUntil(
        async (_signal, record) => {
          const found = await adapter.ownedFileRows(instance, owned.entryId);
          record(`${String(found.length)} file row(s)`);
          return found.length > 0 ? { value: found } : null;
        },
        {
          timeoutMs: JOB_BUDGET_MS,
          intervalMs: 1_000,
          label: "the instance's own file rows for the seeded entry",
        },
      );
      expect(
        linked,
        `the run after the stated path reported "${String(reportedLine(second))}" and the instance holds no file for the entry the library named; its file rows last read ${note}`,
      ).toBe(true);
      const paths = rows.map((row) => String(row.relativePath ?? row.path));
      expect(
        paths.filter((path) => path.includes(run)).length,
        `the instance holds ${String(paths.length)} file(s) for the entry, which are not the one file the library named: ${paths.join(", ")}`,
      ).toBe(1);
    });
  });
}
