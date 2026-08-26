// The one-time name→id options conversion, driven the only way it can be driven end to end: against
// a real containerized Cove that is STARTED ON TOP of a stored legacy blob, with the outcome read off
// the settings panel a user would open.
//
// Why this exists when the backend suite already covers the conversion: every one of those tests hands
// a hand-written blob to `OptionsMigration.Scan`/`Convert` or to the initialize seam directly, and none
// of them starts a host. So none can answer the question a user actually has — "do my settings survive
// the upgrade?" — because the answer depends on three things those tiers replace with a double:
//
//   1. that the host runs `InitializeAsync` (and therefore the conversion) before it serves the panel;
//   2. that the elevated library read returns real rows through Cove's own authorization filters,
//      which exist only under Npgsql and so are absent from the SQLite L1 tier;
//   3. that the panel then RENDERS the converted ids as entity names rather than as numbers, empty
//      fields, or the host's "Loading tag..." placeholder.
//
// This spec is the only place all three are real at once. entity-id-rules.spec.mjs covers the
// conversion's two decisive API-level outcomes — one field converting, and the refusal to convert
// against an unreadable library. What is here is the rest of the blob (both groups, the exclusion
// list, the destination map), the narrowing cases, and the render.
//
// What it is NOT: a test against data a real installation accumulated. The blob below is written by
// this test, so it is realistic by construction rather than by history — six migrated fields in their
// name-keyed form, both groups carrying the empty-array shape a real install always emitted, and
// three unrelated fields whose survival is the preservation proof.
import { test as base, createApiClient, isolatedHarnessFixture } from "@cove-extensions/e2e";
import { expect, pollUntil, RENAMER_EXTENSION } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

const RENAMER_ID = "com.alextomas955.renamer";

// Its own Cove instance, for a reason stronger than data isolation: this test RESTARTS the host. A
// restart re-binds the published port and invalidates every token minted before it, so running it
// against the per-worker instance would pull the base URL out from under every sibling test sharing
// that worker. The fixture installs the extension with NO stored blob, which is what makes the seed
// below reachable: the conversion returns before stamping when there is nothing to convert, so the
// schema stamp is still unset when this test writes its legacy blob.
const test = base.extend({
  migrationHarness: isolatedHarnessFixture(RENAMER_EXTENSION),
});

function clientFor(harness) {
  return createApiClient(
    () => harness.baseUrl,
    () => harness.token,
  );
}

/** The extension's stored options blob, parsed, or undefined when the key is absent. */
async function storedOptions(api) {
  const all = await api.get(`/api/extensions/${RENAMER_ID}/data`);
  expect(all.ok, `reading the extension store answered ${all.status}: ${all.text}`).toBe(true);
  const blob = (all.json ?? {}).options;
  return blob ? JSON.parse(blob) : undefined;
}

/**
 * The root element of a titled `GroupCard` — heading → title box → header row → card root, the hop
 * count read off `primitives.tsx` rather than matched on a class, which would silently follow a
 * restyle onto the wrong element instead of failing.
 */
function groupCard(page, title) {
  return page.getByRole("heading", { name: title, exact: true }).locator("xpath=../../..");
}

/** The root `<section>` of a `ToggleHeaderCard`, whose header nests its heading exactly as deeply. */
function toggleCard(page, title) {
  return page.getByRole("heading", { name: title, exact: true }).locator("xpath=../../..");
}

/**
 * One `Field` within a scope. Two selector fields share the Performers card and two share the Tags
 * card, so the card alone cannot separate a whitelist chip from a blacklist chip; `Field` renders a
 * `<label>` whose first span is the field name, which is the narrowest scope that can.
 */
function field(scope, label) {
  return scope.locator("label").filter({ hasText: label }).first();
}

test("a legacy blob stored before the host starts converts at initialize, and the panel renders the surviving rules as entity names", async ({
  page,
  migrationHarness,
}) => {
  // Two container lifecycles (a boot for the isolated instance, then a full restart on top of it)
  // plus a browser-driven panel run do not fit the project's default per-test budget.
  test.setTimeout(360_000);

  const errors = [];
  page.on("pageerror", (err) => errors.push(err.message));

  const stamp = Date.now();
  const seedApi = clientFor(migrationHarness);

  // ── The library the stored NAMES will be resolved against ───────────────────────────────────────
  const names = {
    tagKeep: `Qzmig Keep Tag ${stamp}`,
    // Two performers differing ONLY by letter case. Created in this order deliberately: the converter
    // keeps the LOWEST id so the choice is decided by the data rather than by the order rows came back
    // in, and Postgres hands out ascending ids, so `caseFirst` is the predictable survivor.
    caseFirst: `qzmig case performer ${stamp}`,
    caseSecond: `QZMIG CASE PERFORMER ${stamp}`,
    tagRoute: `Qzmig Route Tag ${stamp}`,
    tagExclude: `Qzmig Exclude Tag ${stamp}`,
    performerKeep: `Qzmig Keep Performer ${stamp}`,
    performerBlock: `Qzmig Block Performer ${stamp}`,
  };
  // Named by the blob, deliberately never created — a rule pointing at something the library no
  // longer has, which every real upgrade carries at least one of.
  const vanishedTag = `Qzmig Vanished Tag ${stamp}`;

  const ids = {};
  for (const key of ["tagKeep", "tagRoute", "tagExclude"]) {
    const created = await seedApi.post("/api/tags", { name: names[key] });
    expect(
      created.ok,
      `POST /api/tags "${names[key]}" answered ${created.status}: ${created.text}`,
    ).toBe(true);
    ids[key] = created.json.id;
  }
  for (const key of ["performerKeep", "performerBlock"]) {
    const created = await seedApi.post("/api/performers", { name: names[key] });
    expect(
      created.ok,
      `POST /api/performers "${names[key]}" answered ${created.status}: ${created.text}`,
    ).toBe(true);
    ids[key] = created.json.id;
  }
  // Each spelling carries its OWN disambiguation, which is what lets a host hold both: a performer's
  // identity is the name key paired with the disambiguation key, so one name covers several ids only
  // where those disambiguations differ.
  for (const key of ["caseFirst", "caseSecond"]) {
    const created = await seedApi.post("/api/performers", {
      name: names[key],
      disambiguation: `${key} ${stamp}`,
    });
    expect(
      created.ok,
      `POST /api/performers "${names[key]}" answered ${created.status}: ${created.text} — a 409 here means this host folds performer names by case even with distinct disambiguations, which would make the case-collapse assertion below unreachable rather than merely failing`,
    ).toBe(true);
    ids[key] = created.json.id;
  }
  expect(
    ids.caseFirst < ids.caseSecond,
    `the case-variant performers were created as ${ids.caseFirst} and ${ids.caseSecond} — not ascending, so the collapse below has no predictable survivor`,
  ).toBe(true);

  // ── The blob a pre-migration install left behind ────────────────────────────────────────────────
  // All six migrated fields in their name-keyed form, both groups carrying BOTH legacy keys including
  // the empty array a real install always emitted (the panel serialized its whole defaults object),
  // and three fields the converter does not model at all, so preservation is proven rather than
  // assumed. The template names $performers and $tags because the panel renders those two token cards
  // only for tokens the template uses — which makes it an unrelated-field check and a precondition at
  // once. The two routing toggles are on because a `ToggleHeaderCard` renders no children while it is
  // off, so a user who wrote these rules had them on.
  const legacyBlob = {
    FilenameTemplate: "$title - $performers [$tags]",
    AllowedRoots: ["/data"],
    PathDestinations: [{ Pattern: "/data/legacy", Dest: "/data/archive", IsRegex: false }],
    EnableAdvancedRouting: true,
    EnableTagDestinations: true,
    Performers: {
      Whitelist: [names.performerKeep, names.caseFirst, names.caseSecond],
      Blacklist: [names.performerBlock],
    },
    Tags: { Whitelist: [names.tagKeep], Blacklist: [] },
    ExcludeTags: [names.tagExclude, vanishedTag],
    TagDestinations: { [names.tagRoute]: "/data/routed" },
  };
  // Single JSON.stringify: the [FromBody] string binder wants exactly one JSON string literal.
  const seeded = await seedApi.put(
    `/api/extensions/${RENAMER_ID}/data/options`,
    JSON.stringify(legacyBlob),
  );
  expect(
    seeded.ok,
    `seeding the legacy options blob answered ${seeded.status}: ${seeded.text}`,
  ).toBe(true);

  // ── Start the host over on top of it ────────────────────────────────────────────────────────────
  // The conversion runs at InitializeAsync and nowhere else, so there is no way to reach it while the
  // host stays up. Everything after this reads the RESTARTED instance's base URL: a restart can
  // re-bind the published port and re-mints the token.
  await migrationHarness.restart();
  const api = clientFor(migrationHarness);

  // The gate for every panel assertion below, and the first thing that fails when the conversion never
  // runs at all: the stored blob has flipped to the id-keyed vocabulary. Read from the store rather
  // than from the panel because the panel mounts once and would then show the pre-conversion state
  // forever, so a page opened too early would fail for the wrong reason.
  await pollUntil(
    () => storedOptions(api),
    (o) => o !== undefined && Array.isArray(o.Performers?.WhitelistIds),
    {
      timeoutMs: 120_000,
      label: "the initialize-time conversion to rewrite the stored blob to ids",
    },
  );

  const baseUrl = migrationHarness.baseUrl;
  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();

  // ── The surviving rules render as entity NAMES ──────────────────────────────────────────────────
  const tagsCard = groupCard(page, "Tags");
  const tagWhitelist = field(tagsCard, "Whitelist");
  await expect(
    tagWhitelist.getByRole("button", { name: `Remove ${names.tagKeep}`, exact: true }),
    `the tag whitelist rule stored as the name "${names.tagKeep}" is not on the panel as a chip carrying that name`,
  ).toBeVisible({ timeout: 15_000 });
  await expect(
    tagWhitelist.getByRole("button", { name: /^Remove / }),
    "the one stored tag whitelist name must land as exactly one chip",
  ).toHaveCount(1);
  await expect(
    field(tagsCard, "Blacklist").getByRole("button", { name: /^Remove / }),
    "the empty legacy Blacklist a real install always emitted must convert to an empty id list — not to a chip, and not by stranding the whole conversion on a half it had nothing to resolve",
  ).toHaveCount(0);

  const performersCard = groupCard(page, "Performers");
  const performerWhitelist = field(performersCard, "Whitelist");
  await expect(
    performerWhitelist.getByRole("button", {
      name: `Remove ${names.performerKeep}`,
      exact: true,
    }),
    "the performer whitelist rule did not survive as a named chip",
  ).toBeVisible();
  await expect(
    performerWhitelist.getByRole("button", { name: `Remove ${names.caseFirst}`, exact: true }),
    "the surviving half of the case-variant pair must be the LOWEST id, whose name is the first-created spelling",
  ).toBeVisible();
  await expect(
    performerWhitelist.getByRole("button", { name: `Remove ${names.caseSecond}`, exact: true }),
    "both case variants survived as separate chips — the two stored names resolve to one id, so this rule now covers one performer where it covered two, and that narrowing is what the changelog discloses",
  ).toHaveCount(0);
  await expect(
    performerWhitelist.getByRole("button", { name: /^Remove / }),
    "three stored names must land as exactly two chips: the keep performer, plus one survivor of the case-variant pair",
  ).toHaveCount(2);
  await expect(
    field(performersCard, "Blacklist").getByRole("button", {
      name: `Remove ${names.performerBlock}`,
      exact: true,
    }),
    "the performer blacklist rule did not survive as a named chip — the two groups convert independently, so a whitelist that landed says nothing about a blacklist that did not",
  ).toBeVisible();

  // ── The unresolvable name is gone ───────────────────────────────────────────────────────────────
  await page.getByRole("button", { name: /^Excludes/ }).click();
  const excludeTagCard = groupCard(page, "Exclude by tag");
  await expect(
    excludeTagCard.getByRole("button", { name: `Remove ${names.tagExclude}`, exact: true }),
    'the exclusion that DID resolve is missing, so nothing below distinguishes "the vanished name was dropped" from "the whole field was emptied"',
  ).toBeVisible({ timeout: 15_000 });
  await expect(
    excludeTagCard.getByRole("button", { name: /^Remove / }),
    "the exclusion list holds more than the one rule that could resolve — a name matching nothing in the library must be dropped, because it could never have matched anything",
  ).toHaveCount(1);
  const excludeText = await excludeTagCard.innerText();
  expect(excludeText, `the dropped name "${vanishedTag}" is still on the panel`).not.toContain(
    vanishedTag,
  );
  expect(
    excludeText,
    "a chip is stuck on the host loading placeholder, which is what an id resolving to no entity looks like — the conversion wrote an id the library does not have",
  ).not.toContain("Loading tag...");

  // ── The name-keyed destination map re-keyed to ids, and reads back as a name ────────────────────
  const tagDestinations = toggleCard(page, "Per-tag destinations");
  // The committed row, addressed by the remove button its own map key names. The `has:` locator is
  // rooted on `page` rather than on the card: Playwright re-anchors an inner locator at each candidate,
  // so one built from the card would require the card's heading to sit inside the row. `.last()` picks
  // the innermost matching div, i.e. the row itself rather than the editor wrapping it.
  const routedRow = tagDestinations
    .locator("div")
    .filter({ has: page.getByRole("button", { name: `Remove ${ids.tagRoute}`, exact: true }) })
    .last();
  await expect(
    routedRow,
    "the committed per-tag destination row is gone — its map key did not survive re-keying from the tag name to that tag's id",
  ).toBeVisible({ timeout: 15_000 });
  await expect(
    routedRow.getByText(names.tagRoute, { exact: true }),
    "the destination row shows its opaque id rather than the tag name the host resolves it to",
  ).toBeVisible();
  // The map key is re-keyed AND the stored path is split: a destination now names one of Cove's library
  // paths plus a folder rendered under it, so the single legacy string becomes the library path that
  // holds it and the remainder. Asserting both halves is what distinguishes a real split from a row
  // preserved in name only, and from a path dropped into the template with no root behind it.
  await expect(
    routedRow.getByRole("combobox"),
    "the stored path did not become the library path holding it",
  ).toHaveValue("/data");
  await expect(
    routedRow.getByRole("textbox"),
    "the remainder of the stored path did not survive as the folder rendered under that root",
  ).toHaveValue("routed");

  // ── Fields the conversion does not model are untouched ──────────────────────────────────────────
  await expect(
    settings.filenameTemplateInput,
    "the filename template changed across a conversion that has no business touching it — this is the field a typed converter would have reset to its default, and the whole reason the conversion works on raw JSON",
  ).toHaveValue(legacyBlob.FilenameTemplate);
  const advancedRouting = toggleCard(page, "Advanced routing & safety");
  await expect(
    advancedRouting.getByRole("button", { name: "Remove /data", exact: true }),
    "the stored allowed root did not survive the conversion",
  ).toHaveCount(1);
  await expect(
    field(advancedRouting, "Source path").getByRole("textbox"),
    "the stored source-path rule did not survive the conversion",
  ).toHaveValue("/data/legacy");
  // The rule's destination splits the same way the per-tag one above does, so both halves are read: a
  // row preserved in name only fails the first, and a path dropped whole into the folder field fails
  // the second.
  await expect(
    field(advancedRouting, "Under").getByRole("combobox"),
    "the source-path rule survived but its destination root did not",
  ).toHaveValue("/data");
  await expect(
    field(advancedRouting, "Folder template").getByRole("textbox"),
    "the source-path rule's destination root survived but the folder under it did not",
  ).toHaveValue("archive");

  expect(errors, `the settings surface raised page errors: ${errors.join("; ")}`).toEqual([]);
});
