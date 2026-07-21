/**
 * Behavior contract for the pure scene-actions logic. The runner compiles sceneActionsLogic.ts and passes the
 * compiled module path in SCENE_ACTIONS_LOGIC_MODULE; importing the exact compiled artifact keeps the test
 * honest about what ships. Asserts the wire shapes byte-for-byte, the scene-control truth table, and the
 * menu-item state table (monitored vs not-monitored vs no-counts).
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.SCENE_ACTIONS_LOGIC_MODULE);
const {
  MONITOR_MENU_LABEL,
  EXCLUDE_LABEL,
  REMOVE_EXCLUSION_LABEL,
  BATCH_MENU_ITEMS,
  sceneAddBody,
  sceneSearchBody,
  sceneMonitorBody,
  bulkAddMissingBody,
  bulkSearchMonitoredBody,
  videosBatchBody,
  sceneExclusionBody,
  sceneGrabReleaseBody,
  formatReleaseSize,
  releaseSummary,
  sceneControlState,
  menuItemsState,
} = mod;

test("sceneAddBody / sceneSearchBody shape exactly { CoveId } (PascalCase, Cove id only, no url/key)", () => {
  assert.deepEqual(sceneAddBody(42), { CoveId: 42 });
  assert.deepEqual(sceneSearchBody(7), { CoveId: 7 });
  const body = sceneAddBody(1);
  assert.equal("BaseUrl" in body, false);
  assert.equal("ApiKey" in body, false);
});

test("sceneMonitorBody shapes { CoveId, Monitored } (PascalCase)", () => {
  assert.deepEqual(sceneMonitorBody(42, true), { CoveId: 42, Monitored: true });
  assert.deepEqual(sceneMonitorBody(9, false), { CoveId: 9, Monitored: false });
});

test("bulkAddMissingBody shapes { Kind, CoveEntityId } and carries NO RemoteIds", () => {
  const body = bulkAddMissingBody("studio", 15);
  assert.deepEqual(body, { Kind: "studio", CoveEntityId: 15 });
  assert.equal("RemoteIds" in body, false);
  assert.deepEqual(bulkAddMissingBody("performer", 3), { Kind: "performer", CoveEntityId: 3 });
});

test("bulkSearchMonitoredBody shapes { Kind, RemoteIds:[{Endpoint,RemoteId}] } (PascalCase wire, no url/key)", () => {
  const remoteIds = [
    { endpoint: "https://stashdb.org/graphql", remoteId: "abc" },
    { endpoint: "https://theporndb.net/graphql", remoteId: "def" },
  ];
  const body = bulkSearchMonitoredBody("studio", remoteIds);
  assert.deepEqual(body, {
    Kind: "studio",
    RemoteIds: [
      { Endpoint: "https://stashdb.org/graphql", RemoteId: "abc" },
      { Endpoint: "https://theporndb.net/graphql", RemoteId: "def" },
    ],
  });
  assert.equal("BaseUrl" in body, false);
  assert.equal("ApiKey" in body, false);
  // Absent ids → empty array (the server then reports NO_STASHDB_IDENTITY).
  assert.deepEqual(bulkSearchMonitoredBody("performer", null), { Kind: "performer", RemoteIds: [] });
  assert.deepEqual(bulkSearchMonitoredBody("performer", undefined), {
    Kind: "performer",
    RemoteIds: [],
  });
});

test("sceneControlState truth table: add/monitor/search/interactive/upgrades/exclude derivations", () => {
  // notAdded → show the add affordance, search + interactive disabled, monitor/upgrades not pressed, exclude label.
  assert.deepEqual(sceneControlState({ state: "notAdded", added: false, monitored: false }), {
    showAdd: true,
    monitorPressed: false,
    searchEnabled: false,
    interactiveAvailable: false,
    upgradesPressed: false,
    excluded: false,
    excludeLabel: EXCLUDE_LABEL,
  });
  // monitored (added) → no add affordance, search + interactive enabled, monitor + upgrades pressed.
  assert.deepEqual(sceneControlState({ state: "monitored", added: true, monitored: true }), {
    showAdd: false,
    monitorPressed: true,
    searchEnabled: true,
    interactiveAvailable: true,
    upgradesPressed: true,
    excluded: false,
    excludeLabel: EXCLUDE_LABEL,
  });
  // unmonitored but added → interactive enabled, monitor + upgrades not pressed.
  assert.deepEqual(sceneControlState({ state: "unmonitored", added: true, monitored: false }), {
    showAdd: false,
    monitorPressed: false,
    searchEnabled: true,
    interactiveAvailable: true,
    upgradesPressed: false,
    excluded: false,
    excludeLabel: EXCLUDE_LABEL,
  });
  // monitored + has a file (added, monitored) → search + interactive enabled, monitor + upgrades pressed.
  assert.deepEqual(sceneControlState({ state: "monitored", added: true, monitored: true }), {
    showAdd: false,
    monitorPressed: true,
    searchEnabled: true,
    interactiveAvailable: true,
    upgradesPressed: true,
    excluded: false,
    excludeLabel: EXCLUDE_LABEL,
  });
  // excluded (not added) → no add affordance, search/interactive disabled, exclude control reads "Remove exclusion".
  assert.deepEqual(sceneControlState({ state: "excluded", added: false, monitored: false }), {
    showAdd: false,
    monitorPressed: false,
    searchEnabled: false,
    interactiveAvailable: false,
    upgradesPressed: false,
    excluded: true,
    excludeLabel: REMOVE_EXCLUSION_LABEL,
  });
});

test("videosBatchBody shapes { Op, CoveIds } (PascalCase wire, matches /videos-batch)", () => {
  assert.deepEqual(videosBatchBody("add", [1, 2, 3]), { Op: "add", CoveIds: [1, 2, 3] });
  assert.deepEqual(videosBatchBody("searchUpgrades", []), { Op: "searchUpgrades", CoveIds: [] });
  const body = videosBatchBody("exclude", [9]);
  assert.equal("BaseUrl" in body, false);
  assert.equal("ApiKey" in body, false);
});

test("sceneExclusionBody shapes { CoveId, Exclude } (PascalCase)", () => {
  assert.deepEqual(sceneExclusionBody(42, true), { CoveId: 42, Exclude: true });
  assert.deepEqual(sceneExclusionBody(7, false), { CoveId: 7, Exclude: false });
});

test("sceneGrabReleaseBody shapes { CoveId, Guid, IndexerId }; null indexerId coalesces to 0", () => {
  assert.deepEqual(sceneGrabReleaseBody(42, "abc-guid", 5), {
    CoveId: 42,
    Guid: "abc-guid",
    IndexerId: 5,
  });
  assert.deepEqual(sceneGrabReleaseBody(1, "g", null), { CoveId: 1, Guid: "g", IndexerId: 0 });
  assert.deepEqual(sceneGrabReleaseBody(1, "g", undefined), { CoveId: 1, Guid: "g", IndexerId: 0 });
});

test("BATCH_MENU_ITEMS is the ordered design menu (Add · Search now · Search for upgrades · Exclude)", () => {
  assert.deepEqual(
    BATCH_MENU_ITEMS.map((i) => i.op),
    ["add", "search", "searchUpgrades", "exclude"],
  );
  assert.deepEqual(
    BATCH_MENU_ITEMS.map((i) => i.label),
    ["Add to Whisparr", "Search now", "Search for upgrades", "Exclude from Whisparr"],
  );
});

test("formatReleaseSize renders compact binary units; absent/zero → empty string", () => {
  assert.equal(formatReleaseSize(2.3 * 1024 ** 3), "2.3 GB");
  assert.equal(formatReleaseSize(700 * 1024 ** 2), "700 MB");
  assert.equal(formatReleaseSize(512 * 1024), "512 KB");
  assert.equal(formatReleaseSize(0), "");
  assert.equal(formatReleaseSize(null), "");
  assert.equal(formatReleaseSize(undefined), "");
});

test("releaseSummary joins present-only fragments (quality · size · indexer · seeders · age), reads quality.quality.name", () => {
  assert.equal(
    releaseSummary({
      guid: "g",
      title: "Scene",
      quality: { quality: { name: "WEB-DL 1080p" } },
      size: 2 * 1024 ** 3,
      indexer: "MyIndexer",
      seeders: 12,
      age: 3,
    }),
    "WEB-DL 1080p · 2.0 GB · MyIndexer · 12 seeders · 3d",
  );
  // Absent fields are omitted entirely (no empty fragments, no leading/trailing middot).
  assert.equal(releaseSummary({ guid: "g", indexer: "Only" }), "Only");
  assert.equal(releaseSummary({ guid: "g" }), "");
  // seeders: 0 is a present value (shows "0 seeders"), not omitted.
  assert.equal(releaseSummary({ guid: "g", seeders: 0 }), "0 seeders");
});

test("menuItemsState: monitored → checked + bulk shown; carries NO status-text field (the page line owns it)", () => {
  const state = menuItemsState({ added: true, monitored: true, scenesPresent: 12, scenesTotal: 40 });
  assert.deepEqual(state, {
    monitorLabel: "Monitor in Whisparr",
    monitorChecked: true,
    showBulk: true,
  });
  assert.equal(MONITOR_MENU_LABEL, "Monitor in Whisparr");
  // The duplicate in-menu status line was removed; the field it drove is pruned entirely.
  assert.equal("statusText" in state, false);
});

test("menuItemsState: not monitored (or null) → unchecked, bulk hidden, no status-text field", () => {
  assert.deepEqual(menuItemsState({ added: true, monitored: false, scenesPresent: 0, scenesTotal: 0 }), {
    monitorLabel: "Monitor in Whisparr",
    monitorChecked: false,
    showBulk: false,
  });
  assert.deepEqual(menuItemsState(null), {
    monitorLabel: "Monitor in Whisparr",
    monitorChecked: false,
    showBulk: false,
  });
  assert.deepEqual(menuItemsState(undefined), {
    monitorLabel: "Monitor in Whisparr",
    monitorChecked: false,
    showBulk: false,
  });
});
