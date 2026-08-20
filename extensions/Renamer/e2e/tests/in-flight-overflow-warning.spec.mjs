// The in-flight path-overflow warning, read off the two screens a user actually looks at: the pill on
// the flagged dry-run row, and the confirm gate shown before a bulk rename runs.
//
// WHY THIS EXISTS AS A BROWSER SPEC. Both ends of the data path were already covered without it — the
// server flag by ScanRowOverflowFlagTests, the label derivation by dryRunLogic.test.mjs, the confirm
// sentence by preview.test.mjs, and the join between them by a typecheck that requires the field. None
// of that is evidence that a pill is on the screen: a required field proves a value arrives, not that
// anything renders it, and every one of those expectations is checked against the module that produces
// it. The only thing that can contradict "the user sees this" is a browser reading the DOM of a real
// host, which is this tier.
//
// TWO RULES THIS FILE KEEPS, both load-bearing rather than stylistic:
//
//   * Every expected string below is transcribed BY HAND from the copy, never imported from the module
//     that composes it. An expectation derived from the code under test agrees with itself forever —
//     rename the label and an importing test follows it silently. These literals go red.
//   * NOTHING here renames a file. The whole point of the warning is that seeing it costs no mutation:
//     the dry run is a plan, and the confirm gate is CANCELLED rather than accepted. That is also why
//     the confirm gate is raised by hand below instead of through VideosPage.renameSelected(), which
//     ACCEPTS the dialog and would run the rename this spec must not run.
//
// HOW THE OVERFLOW IS REACHED. Not with a near-MAX_PATH fixture: the budget is a SETTING, so it is set
// low and ordinary paths overflow. The pair of volumes is real — /data is on the container's root
// filesystem and /data2 is a tmpfs, a different filesystem TYPE, so the two classify cross-volume
// through the mount-aware VolumeClassifier (measured; see tests/e2e/docker/docker-compose.yml).
//
// Its own instance per test (isolatedTest): FullPathMax is a GLOBAL Renamer option and the dry run
// scans the WHOLE library, so both the setting and the row set would leak through the worker-shared
// harness into every sibling spec, and a grid of unknown contents makes "the flagged row" a different
// set on every run.
//
// What is NOT asserted here, on purpose: that this particular pill takes the RED variant rather than
// the amber one. The red variant's own rendering is already proven against a released host by
// host-absent-styles.spec.mjs, and on this surface colour is never the only signal — the label is,
// which is what every assertion below reads. A pill that went amber would be a wrong severity, not an
// invisible warning.
import {
  isolatedTest as test,
  expect,
  seedVideo,
  createApiClient,
} from "../lib/renamer-fixtures.mjs";
import { VideosPage } from "@cove-extensions/e2e/pages/videos-page";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

const RENAMER_ID = "com.alextomas955.renamer";
const ROUTE = `/api/extensions/${RENAMER_ID}`;

// The characters a cross-volume copy is minted longer than its final path (CrossVolumeMover's ".rnm"
// marker plus 8 hex). Written out rather than imported: this is the number the SERVER decides, and a
// value read back from it could not disagree with it. DestAnchoredMaxPathTests writes the same bare 12
// for the same reason.
const IN_FLIGHT_SUFFIX_LENGTH = 12;

// Low enough that a short, readable path overflows; the shipped default is 259.
const FULL_PATH_MAX = 36;

const SOURCE_DIR = "/data";
const DEST_DIR = "/data2";

// One title whose planned path fits the budget but not the budget minus the minted suffix, and one that
// fits both. They differ in nothing else — same volume pair, same template, same instance — so the
// second row is the negative control the first needs: without it, a pill rendered unconditionally would
// read exactly like a pill rendered correctly.
const OVERFLOW_TITLE = "Overflow Warning Row";
const FITS_TITLE = "Fits";

const OVERFLOW_SOURCE_NAME = "overflow-row.mp4";
const FITS_SOURCE_NAME = "fits-row.mp4";

const OVERFLOW_DEST = `${DEST_DIR}/${OVERFLOW_TITLE}.mp4`;
const FITS_DEST = `${DEST_DIR}/${FITS_TITLE}.mp4`;

// The badge copy, hand-transcribed from dryRunLogic's IN_FLIGHT_OVERFLOW_LABEL.
const OVERFLOW_BADGE = "Too long to copy across drives";

// The confirm-gate line, hand-transcribed from buildConfirmSummary. Singular ("for it") because exactly
// one item is selected each time the gate is raised below.
const CONFIRM_WARNING =
  "⚠ 1 cannot be copied across drives — the temporary copy's path would be too long. " +
  "Shorten the destination folder or the filename template for it.";

test("the in-flight path-overflow warning renders on the flagged dry-run row and in the confirm gate, and on neither for a row that fits", async ({
  page,
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const container = isolatedHarness.container;
  const token = isolatedHarness.token;
  const api = createApiClient(baseUrl, token);

  // Replaced wholesale, so every unset field falls back to its default. "$title" with an empty folder
  // template makes the planned path exactly DEST_DIR + "/" + title + ext — nothing about the fixture's
  // own metadata can move it.
  const options = await api.put(
    `/api/extensions/${RENAMER_ID}/data/options`,
    JSON.stringify({
      FilenameTemplate: "$title",
      FolderTemplate: "",
      DefaultDestination: DEST_DIR,
      EnableDefaultRelocate: true,
      AllowedRoots: [SOURCE_DIR, DEST_DIR],
      FullPathMax: FULL_PATH_MAX,
    }),
  );
  expect(options.ok, `seeding the options blob returned ${options.status}: ${options.text}`).toBe(
    true,
  );

  const overflow = await seedVideo({ container, baseUrl, token, destName: OVERFLOW_SOURCE_NAME });
  const fits = await seedVideo({ container, baseUrl, token, destName: FITS_SOURCE_NAME });
  for (const [video, title] of [
    [overflow, OVERFLOW_TITLE],
    [fits, FITS_TITLE],
  ]) {
    const update = await api.put(`/api/videos/${video.id}`, { Title: title });
    expect(update.ok, `setting the title returned ${update.status}: ${update.text}`).toBe(true);
  }

  // ---------------------------------------------------------------------------------------------
  // The arithmetic, checked against the planner's OWN output before any pixel is read. Without this a
  // slip would put both items on the same side of the boundary, and two rows agreeing about a warning
  // that should differ between them would look like a correct answer.
  // ---------------------------------------------------------------------------------------------
  const preview = await api.post(`${ROUTE}/preview`, {
    EntityType: "video",
    EntityIds: [overflow.id, fits.id],
  });
  expect(preview.status).toBe(200);
  const plannedOverflow = preview.json.items.find(
    (item) => item.oldFullPath === `${SOURCE_DIR}/${OVERFLOW_SOURCE_NAME}`,
  );
  const plannedFits = preview.json.items.find(
    (item) => item.oldFullPath === `${SOURCE_DIR}/${FITS_SOURCE_NAME}`,
  );
  expect(plannedOverflow, `no plan item for ${SOURCE_DIR}/${OVERFLOW_SOURCE_NAME}`).toBeTruthy();
  expect(plannedFits, `no plan item for ${SOURCE_DIR}/${FITS_SOURCE_NAME}`).toBeTruthy();
  expect(plannedOverflow.newFullPath).toBe(OVERFLOW_DEST);
  expect(plannedFits.newFullPath).toBe(FITS_DEST);
  // Both are real acting plans, so neither flag below is what it is merely because its row was skipped.
  expect(plannedOverflow.status).toBe("move");
  expect(plannedFits.status).toBe("move");
  // And both really do cross volumes — the server's own classification over the real mount table, not
  // one computed here and compared with itself.
  expect(preview.json.summary.crossVolumeCount).toBe(2);
  expect(preview.json.summary.sameVolumeCount).toBe(0);

  // Why one overflows and the other does not, stated in the test rather than left to the reader.
  expect(
    OVERFLOW_DEST.length,
    "the overflowing path must be one the planner ACCEPTS — a rejected item is a skip, not a warning",
  ).toBeLessThanOrEqual(FULL_PATH_MAX);
  expect(
    OVERFLOW_DEST.length + IN_FLIGHT_SUFFIX_LENGTH,
    "the overflowing path's in-flight copy must not fit, or there is nothing to warn about",
  ).toBeGreaterThan(FULL_PATH_MAX);
  expect(
    FITS_DEST.length + IN_FLIGHT_SUFFIX_LENGTH,
    "the control path's in-flight copy must fit, or it is not a control",
  ).toBeLessThanOrEqual(FULL_PATH_MAX);
  expect(
    preview.json.summary.inFlightPathOverflowCount,
    "the server counted a different number of overflowing items than this spec set up",
  ).toBe(1);

  // ---------------------------------------------------------------------------------------------
  // SURFACE 1 — the pill on the dry-run row.
  // ---------------------------------------------------------------------------------------------
  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  await expect(settings.filenameTemplateInput).toBeVisible({ timeout: 30_000 });
  await settings.openDryRun();

  const overflowRow = settings.dryRunRowFor(OVERFLOW_SOURCE_NAME);
  const fitsRow = settings.dryRunRowFor(FITS_SOURCE_NAME);
  // The scan is a job the modal enqueues on mount and polls, so the first row arrives seconds after the
  // dialog does.
  await expect(
    overflowRow,
    "the dry-run scan never produced a row for the overflowing item",
  ).toBeVisible({
    timeout: 90_000,
  });
  await expect(fitsRow).toBeVisible({ timeout: 30_000 });

  // Read as text, not as a locator count: a failure then names what the row ACTUALLY said, which is the
  // difference between "the pill moved" and "the pill is gone".
  const overflowRowText = await overflowRow.innerText();
  expect(
    overflowRowText,
    "the flagged dry-run row does not carry the overflow pill's copy",
  ).toContain(OVERFLOW_BADGE);
  const fitsRowText = await fitsRow.innerText();
  expect(
    fitsRowText,
    "a row whose in-flight copy fits carries the overflow pill anyway — the pill is not keyed on the flag",
  ).not.toContain(OVERFLOW_BADGE);

  // The copy is a VISIBLE element of its own, not text that happens to be in the row's subtree with
  // something drawn over it. Exactly one, so a pill repeated per row could not satisfy it.
  await expect(
    overflowRow.getByText(OVERFLOW_BADGE, { exact: true }),
    "the overflow pill's copy is in the row but is not a visible element of its own",
  ).toHaveCount(1);
  await expect(overflowRow.getByText(OVERFLOW_BADGE, { exact: true })).toBeVisible();

  // Closed, never renamed. "Rename N files" is right beside this button and is exactly what a user
  // reading this warning must be able to decline.
  await settings.dryRunCloseButton.click();
  await expect(settings.dryRunDialog).toBeHidden({ timeout: 10_000 });

  // ---------------------------------------------------------------------------------------------
  // SURFACE 2 — the confirm gate, raised twice: once over the overflowing item and once over the one
  // that fits. The second round is what makes the first mean something; a gate whose text was read from
  // an empty selection would carry no warning line either.
  // ---------------------------------------------------------------------------------------------
  const videosPage = new VideosPage(page, baseUrl);

  const overflowGate = await raiseConfirmGate(page, videosPage, OVERFLOW_TITLE);
  expect(
    overflowGate,
    "the confirm gate offered the overflowing item without the warning that its copy will not fit",
  ).toContain(CONFIRM_WARNING);
  // Which item the gate is actually describing, so the presence of the line above is tied to THIS plan.
  expect(overflowGate).toContain(`${OVERFLOW_SOURCE_NAME}  →  ${OVERFLOW_TITLE}.mp4`);

  const fitsGate = await raiseConfirmGate(page, videosPage, FITS_TITLE);
  expect(fitsGate).toContain(`${FITS_SOURCE_NAME}  →  ${FITS_TITLE}.mp4`);
  expect(
    fitsGate,
    "the warning appeared for an item whose in-flight copy fits — the line is not keyed on the count",
  ).not.toContain(CONFIRM_WARNING);

  // Nothing moved. Both files are where they were seeded and neither destination exists, which is the
  // claim that separates "the user was warned" from "the user was warned after the fact".
  for (const name of [OVERFLOW_SOURCE_NAME, FITS_SOURCE_NAME]) {
    const source = await container.exec(["test", "-f", `${SOURCE_DIR}/${name}`]);
    expect(source.exitCode, `${SOURCE_DIR}/${name} is no longer on disk`).toBe(0);
  }
  const destContents = await container.exec(["sh", "-c", `ls -A ${DEST_DIR} | wc -l`], {
    user: "root",
  });
  expect(destContents.output.trim(), `${DEST_DIR} is not empty — something was moved`).toBe("0");
});

/**
 * Selects exactly one grid card and reads the confirm gate it raises, then CANCELS it.
 *
 * Deliberately not `VideosPage.renameSelected()`: that helper accepts the dialog, which runs the
 * rename. Cancelling is the whole subject here — the warning is readable before anything is touched —
 * and `renameSelected` returning the message it accepted cannot show that.
 *
 * The card is addressed by the title, because the card's accessible name follows a title once one is
 * set (recorded in rename-ui-coverage.spec.mjs). A fresh navigation each round clears the previous
 * round's selection, so the gate describes one item and its count reads as one.
 */
async function raiseConfirmGate(page, videosPage, cardName) {
  await videosPage.goto();
  const card = videosPage.cardByFilename(cardName);
  const selectButton = card.getByRole("button", { name: /^(Select|Deselect) item$/ });
  await expect(
    selectButton,
    `no single grid card named "${cardName}" — the confirm gate would then describe the wrong selection, or none`,
  ).toHaveCount(1, { timeout: 15_000 });
  await selectButton.click();

  const messages = [];
  let seen;
  const gateRaised = new Promise((resolve) => {
    seen = resolve;
  });
  const handler = async (dialog) => {
    messages.push(dialog.message());
    await dialog.dismiss();
    seen();
  };
  page.on("dialog", handler);
  try {
    await videosPage.renameSelectedButton.click();
    await Promise.race([
      gateRaised,
      page.waitForTimeout(15_000).then(() => {
        throw new Error(`the confirm gate never fired for "${cardName}" within 15s`);
      }),
    ]);
  } finally {
    page.off("dialog", handler);
  }
  return messages.join("\n");
}
