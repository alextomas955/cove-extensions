// The one-time name→id options conversion, driven the only way it can be driven end to end: against
// a real containerized Cove that is STARTED ON TOP of a stored legacy blob, with the outcome read off
// the settings panel a user would open.
//
// Why this exists when 21-07 already covers the conversion with 34 backend tests: every one of those
// hands a hand-written blob to `OptionsMigration.Scan`/`Convert` or to the initialize seam directly,
// and none of them starts a host. So none can answer the question a user actually has — "do my
// settings survive the upgrade?" — because the answer depends on three things those tiers replace
// with a double:
//
//   1. that the host runs `InitializeAsync` (and therefore the conversion) before it serves the panel;
//   2. that the elevated library read returns real rows through Cove's own authorization filters,
//      which exist only under Npgsql and so are absent from the SQLite L1 tier (21-07's D11);
//   3. that the panel then RENDERS the converted ids as entity names rather than as numbers, empty
//      fields, or the host's "Loading tag..." placeholder.
//
// This spec is the only place all three are real at once.
//
// What it is NOT: a test against data a real installation accumulated. The blob below is written by
// this test, so it is realistic by construction rather than by history — six migrated fields in their
// name-keyed form, both groups carrying the empty-array shape a real install always emitted, and
// three unrelated fields whose survival is the preservation proof. The residue that leaves is stated
// in 21-12-SUMMARY.md.
import { test as base, expect, RENAMER_EXTENSION } from '../lib/renamer-fixtures.mjs';
import { startHarness } from '@cove-extensions/e2e/harness';
import { RenamerSettingsPage } from '../lib/pages/renamer-settings-page.mjs';

const RENAMER_ID = 'com.alextomas955.renamer';

const test = base.extend({
  // Its own Cove instance, for a reason stronger than data isolation: this test RESTARTS the host.
  // A restart re-binds the published port and invalidates every token minted before it, so running
  // it against the per-worker instance would pull the base URL out from under every sibling test
  // sharing that worker.
  migrationHarness: [
    async ({}, use) => {
      const instance = await startHarness();
      instance.owner = await instance.bootstrapOwner();
      // The extension initializes here with NO stored blob, which is exactly what makes the seed
      // below reachable: the conversion returns before stamping when there is nothing to convert, so
      // the schema stamp is still unset when this test writes its legacy blob.
      await instance.installExtension(RENAMER_EXTENSION);
      await use(instance);
      await instance.stop();
    },
    { scope: 'test' },
  ],
});

/**
 * The root element of a titled `GroupCard` — heading → title box → header row → card root, the hop
 * count read off `primitives.tsx` rather than matched on a class, which would silently follow a
 * restyle onto the wrong element instead of failing. Same shape as `rename-ui-coverage.spec.mjs`'s.
 */
function groupCard(page, title) {
  return page.getByRole('heading', { name: title, exact: true }).locator('xpath=../../..');
}

/** The root `<section>` of a `ToggleHeaderCard`, whose header nests its heading exactly as deeply. */
function toggleCard(page, title) {
  return page.getByRole('heading', { name: title, exact: true }).locator('xpath=../../..');
}

/**
 * One `Field` within a scope. Two selector fields share the Performers card and two share the Tags
 * card, so the card alone cannot separate a whitelist chip from a blacklist chip; `Field` renders a
 * `<label>` whose first span is the field name, which is the narrowest scope that can.
 */
function field(scope, label) {
  return scope.locator('label').filter({ hasText: label }).first();
}

function apiFor(baseUrl) {
  async function callApi(method, path, body) {
    const res = await fetch(`${baseUrl}${path}`, {
      method,
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    let json;
    try {
      json = text ? JSON.parse(text) : undefined;
    } catch {
      json = undefined;
    }
    return { status: res.status, ok: res.ok, json, text };
  }
  return {
    get: (p) => callApi('GET', p),
    post: (p, b) => callApi('POST', p, b),
    put: (p, b) => callApi('PUT', p, b),
  };
}

test('a legacy blob stored before the host starts converts at initialize, and the panel renders the surviving rules as entity names', async ({
  page,
  migrationHarness,
}) => {
  // Two container lifecycles (a boot for the isolated instance, then a full restart on top of it)
  // plus a browser-driven panel run do not fit the project's 180s default.
  test.setTimeout(360_000);

  const errors = [];
  page.on('pageerror', (err) => errors.push(err.message));

  const stamp = Date.now();
  const seedApi = apiFor(migrationHarness.baseUrl);

  // ── The library the stored NAMES will be resolved against ───────────────────────────────────────
  const names = {
    tagKeep: `Qzmig Keep Tag ${stamp}`,
    // Two tags differing ONLY by letter case. Created in this order deliberately: the converter keeps
    // the LOWEST id so the choice is decided by the data rather than by the order rows came back in,
    // and Postgres hands out ascending ids, so `caseFirst` is the predictable survivor.
    caseFirst: `qzmig case tag ${stamp}`,
    caseSecond: `QZMIG CASE TAG ${stamp}`,
    tagRoute: `Qzmig Route Tag ${stamp}`,
    tagExclude: `Qzmig Exclude Tag ${stamp}`,
    performerKeep: `Qzmig Keep Performer ${stamp}`,
    performerBlock: `Qzmig Block Performer ${stamp}`,
  };
  // Named by the blob, deliberately never created — a rule pointing at something the library no
  // longer has, which every real upgrade carries at least one of.
  const vanishedTag = `Qzmig Vanished Tag ${stamp}`;

  const ids = {};
  for (const key of ['tagKeep', 'caseFirst', 'caseSecond', 'tagRoute', 'tagExclude']) {
    const created = await seedApi.post('/api/tags', { name: names[key] });
    expect(
      created.ok,
      `POST /api/tags "${names[key]}" answered ${created.status}: ${created.text} — a 409 here means this host folds tag names by case, which would make the case-collapse assertion below unreachable rather than merely failing`,
    ).toBe(true);
    ids[key] = created.json.id;
  }
  for (const key of ['performerKeep', 'performerBlock']) {
    const created = await seedApi.post('/api/performers', { name: names[key] });
    expect(created.ok, `POST /api/performers "${names[key]}" answered ${created.status}: ${created.text}`).toBe(true);
    ids[key] = created.json.id;
  }
  expect(
    ids.caseFirst < ids.caseSecond,
    `the case-variant tags were created as ${ids.caseFirst} and ${ids.caseSecond} — not ascending, so the collapse below has no predictable survivor`,
  ).toBe(true);

  // ── The blob a pre-migration install left behind ────────────────────────────────────────────────
  // All six migrated fields in their name-keyed form, both groups carrying BOTH legacy keys including
  // the empty array a real install always emitted (the panel serialized its whole defaults object),
  // and three fields the converter does not model at all, so preservation is proven rather than
  // assumed. The template names $performers and $tags because the panel renders those two token cards
  // only for tokens the template uses — which makes it an unrelated-field check and a precondition at
  // once.
  const legacyBlob = {
    FilenameTemplate: '$title - $performers [$tags]',
    AllowedRoots: ['/data'],
    PathDestinations: [{ Pattern: '/data/legacy', Dest: '/data/archive', IsRegex: false }],
    Performers: { Whitelist: [names.performerKeep], Blacklist: [names.performerBlock] },
    Tags: { Whitelist: [names.tagKeep, names.caseFirst, names.caseSecond], Blacklist: [] },
    ExcludeTags: [names.tagExclude, vanishedTag],
    TagDestinations: { [names.tagRoute]: '/data/routed' },
  };
  // Single JSON.stringify: the [FromBody] string binder wants exactly one JSON string literal.
  const seeded = await seedApi.put(
    `/api/extensions/${RENAMER_ID}/data/options`,
    JSON.stringify(legacyBlob),
  );
  expect(seeded.ok, `seeding the legacy options blob answered ${seeded.status}: ${seeded.text}`).toBe(true);

  // ── Start the host over on top of it ────────────────────────────────────────────────────────────
  // The conversion runs at InitializeAsync and nowhere else, so there is no way to reach it while the
  // host stays up. Everything after this reads the RESTARTED instance's base URL: a restart can
  // re-bind the published port.
  await migrationHarness.restart();
  const baseUrl = migrationHarness.baseUrl;
  const settings = new RenamerSettingsPage(page, baseUrl);

  // The panel reads the stored blob once, on mount, so a page that mounted before the conversion
  // landed would show the pre-conversion state forever. Reload until the conversion's own visible
  // outcome is on the page — its save refusal gone — rather than sleeping for a guessed interval.
  // This is also the first assertion that fails when the conversion never runs at all.
  await expect(async () => {
    await settings.goto();
    await expect(settings.filenameTemplateInput).toBeVisible({ timeout: 15_000 });
    await expect(
      page.getByText('still stored by name', { exact: false }),
      'the panel still refuses to save because the stored rules are name-keyed — the initialize-time conversion did not run, or ran and refused to write',
    ).toHaveCount(0);
  }).toPass({ timeout: 120_000 });

  // ── The surviving rules render as entity NAMES ──────────────────────────────────────────────────
  const tagsCard = groupCard(page, 'Tags');
  const tagWhitelist = field(tagsCard, 'Whitelist');
  await expect(
    tagWhitelist.getByRole('button', { name: `Remove ${names.tagKeep}`, exact: true }),
    `the tag whitelist rule stored as the name "${names.tagKeep}" is not on the panel as a chip carrying that name`,
  ).toBeVisible({ timeout: 15_000 });
  await expect(
    tagWhitelist.getByRole('button', { name: `Remove ${names.caseFirst}`, exact: true }),
    'the surviving half of the case-variant pair must be the LOWEST id, whose name is the first-created spelling',
  ).toBeVisible();
  await expect(
    tagWhitelist.getByRole('button', { name: `Remove ${names.caseSecond}`, exact: true }),
    'both case variants survived as separate chips — the two stored names resolve to one id, so this rule now covers one tag where it covered two, and that narrowing is what the changelog discloses',
  ).toHaveCount(0);
  await expect(
    tagWhitelist.getByRole('button', { name: /^Remove / }),
    'three stored names must land as exactly two chips: the keep tag, plus one survivor of the case-variant pair',
  ).toHaveCount(2);
  await expect(
    field(tagsCard, 'Blacklist').getByRole('button', { name: /^Remove / }),
    'the empty legacy Blacklist a real install always emitted must convert to an empty id list — not to a chip, and not by stranding the whole conversion on a half it had nothing to resolve',
  ).toHaveCount(0);

  const performersCard = groupCard(page, 'Performers');
  await expect(
    field(performersCard, 'Whitelist').getByRole('button', { name: `Remove ${names.performerKeep}`, exact: true }),
    'the performer whitelist rule did not survive as a named chip',
  ).toBeVisible();
  await expect(
    field(performersCard, 'Blacklist').getByRole('button', { name: `Remove ${names.performerBlock}`, exact: true }),
    'the performer blacklist rule did not survive as a named chip — the two groups convert independently, so a whitelist that landed says nothing about a blacklist that did not',
  ).toBeVisible();

  // ── The unresolvable name is gone ───────────────────────────────────────────────────────────────
  await page.getByRole('button', { name: /^Excludes/ }).click();
  const excludeTagCard = groupCard(page, 'Exclude by tag');
  await expect(
    excludeTagCard.getByRole('button', { name: `Remove ${names.tagExclude}`, exact: true }),
    'the exclusion that DID resolve is missing, so nothing below distinguishes "the vanished name was dropped" from "the whole field was emptied"',
  ).toBeVisible({ timeout: 15_000 });
  await expect(
    excludeTagCard.getByRole('button', { name: /^Remove / }),
    'the exclusion list holds more than the one rule that could resolve — a name matching nothing in the library must be dropped, because it could never have matched anything',
  ).toHaveCount(1);
  const excludeText = await excludeTagCard.innerText();
  expect(excludeText, `the dropped name "${vanishedTag}" is still on the panel`).not.toContain(vanishedTag);
  expect(
    excludeText,
    'a chip is stuck on the host loading placeholder, which is what an id resolving to no entity looks like — the conversion wrote an id the library does not have',
  ).not.toContain('Loading tag...');

  // ── The name-keyed destination map re-keyed to ids, and reads back as a name ────────────────────
  const tagDestinations = toggleCard(page, 'Per-tag destinations');
  await expect(
    tagDestinations.getByRole('button', { name: `Remove ${ids.tagRoute}`, exact: true }),
    'the committed per-tag destination row is gone — its map key did not survive re-keying from the tag name to that tag\'s id',
  ).toHaveCount(1);
  await expect(
    tagDestinations.getByText(names.tagRoute, { exact: true }),
    'the destination row shows its opaque id rather than the tag name the host resolves it to',
  ).toBeVisible();
  await expect(
    tagDestinations.getByRole('textbox').first(),
    'the destination the rule routed to did not survive the re-key',
  ).toHaveValue('/data/routed');

  // ── Fields the conversion does not model are untouched ──────────────────────────────────────────
  await expect(
    settings.filenameTemplateInput,
    'the filename template changed across a conversion that has no business touching it — this is the field a typed converter would have reset to its default, and the whole reason the conversion works on raw JSON',
  ).toHaveValue(legacyBlob.FilenameTemplate);
  const advancedRouting = toggleCard(page, 'Advanced routing & safety');
  await expect(
    advancedRouting.getByRole('button', { name: 'Remove /data', exact: true }),
    'the stored allowed root did not survive the conversion',
  ).toHaveCount(1);
  await expect(
    field(advancedRouting, 'Source path').getByRole('textbox'),
    'the stored source-path rule did not survive the conversion',
  ).toHaveValue('/data/legacy');
  await expect(
    field(advancedRouting, 'Destination root').getByRole('textbox'),
    'the source-path rule survived but its destination did not, so the row is preserved in name only',
  ).toHaveValue('/data/archive');

  expect(errors, `the settings surface raised page errors: ${errors.join('; ')}`).toEqual([]);
});
