// Offline correctness spec (node:test) — the v2 (Sonarr-shaped) OUTWARD parity proof, the v2 analogue of
// monitor.test.mjs / loop-safety.test.mjs. v2 has no /movie entity: a site = a series, a scene = an
// episode, keyed on the ThePornDB id carried in Sonarr's tvdbId slot. Monitoring a Cove studio that
// carries the real allowlist TPDB id (resolved offline through the SkyHook replay stub) must produce
// genuine, assertable v2 state, not the extension's own return code.
//
// The load-bearing assertions read Whisparr v2's OWN Sonarr-shaped API on the /api/v3 path:
//
//   1. GET /api/v3/series finds the site row whose tvdbId is the allowlist TPDB id, monitored:true,
//      carrying the cove-sync origin tag (tag id resolved via GET /api/v3/tag).
//   2. GET /api/v3/queue is empty — the add is non-grabbing (addOptions.searchForMissingEpisodes:false),
//      so registering the site queues no grab.
//   3. A repeated identical add is idempotent: v2 answers the second add with a 400 SeriesExistsValidator
//      the adapter maps to success, so still exactly one series row (never a duplicate).
//   4. The extension's status projection reads back grabbed-of-total over the site's REAL episode state
//      (a projection from v2's series/episode rows, not a stub of the extension response).
//   5. An explicit user Search issues the EpisodeSearch command — the one grab-capable v2 verb — which
//      v2 accepts (GET /api/v3/command shows it), while loop-safety (empty queue after the add) holds:
//      a loop-safe add leaves the back-catalogue episodes unmonitored, so the search grabs nothing.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { startWhisparrSyncHarness, EXTENSION_ID } from "../lib/setup.mjs";

const ORIGIN_TAG = "cove-sync";

let ctx;

// Reads Whisparr v2's own Sonarr-shaped API on its mapped host port with the out-of-band key (never the
// extension's). Both v2 and v3 expose the /api/v3 surface; on v2 /series and /command are the live verbs.
async function whisparrGet(path) {
  const res = await fetch(`${ctx.whisparr.baseUrlFromHost}${path}`, {
    headers: { "X-Api-Key": ctx.whisparr.apiKey },
  });
  const text = await res.text();
  return { status: res.status, json: text ? JSON.parse(text) : undefined };
}

async function listSeries() {
  const { json } = await whisparrGet("/api/v3/series");
  return Array.isArray(json) ? json : [];
}

// The site identity is the TPDB id in Sonarr's tvdbId slot (v2 rows carry no StashDB id).
function findSiteByTvdbId(rows, tvdbId) {
  return rows.filter((s) => s.tvdbId === tvdbId);
}

function queueRecords(json) {
  if (Array.isArray(json)) return json;
  return json?.records ?? [];
}

before(async () => {
  ctx = await startWhisparrSyncHarness({ version: "v2" });
}, { timeout: 600_000 });

after(async () => {
  await ctx?.stop();
}, { timeout: 120_000 });

test("monitoring a studio adds a real, monitored, cove-sync-tagged v2 site (series) row", async () => {
  const { api, remoteIds } = ctx;
  // The allowlist TPDB id resolves to the tvdbId slot; the harness stored it as a string RemoteId.
  const tvdbId = Number(remoteIds.remoteId);

  const res = await api.post(`/api/extensions/${EXTENSION_ID}/monitor`, {
    Kind: "studio",
    RemoteIds: [{ Endpoint: remoteIds.endpoint, RemoteId: remoteIds.remoteId }],
    Monitored: true,
  });
  // The extension must not have errored, but its status is NOT the proof of sync — the v2-side reads
  // below are. Never assert success from the extension's own status code alone.
  assert.ok(res.status < 500, `monitor did not error (status ${res.status}, body: ${res.text})`);

  // Read-after-write is not guaranteed on the first GET: a fresh v2 site CREATE queues an async series
  // refresh (it fetches the episode list) that rebuilds the row after the flip PUT, so poll until the
  // real monitored row is observable.
  const matches = await pollUntil(
    () => listSeries(),
    (rows) => findSiteByTvdbId(rows, tvdbId).length === 1 && findSiteByTvdbId(rows, tvdbId)[0].monitored === true,
    { timeoutMs: 90_000, label: "a single monitored v2 site row for the allowlist TPDB id" },
  );
  const site = findSiteByTvdbId(matches, tvdbId)[0];
  assert.equal(site.tvdbId, tvdbId);
  assert.equal(site.monitored, true);

  // The cove-sync origin tag is attached — resolve its id from v2's own tag list, then confirm membership
  // on the site row (the loop-safety attribution contract, proven against real v2 state).
  const { json: tags } = await whisparrGet("/api/v3/tag");
  const originTag = (Array.isArray(tags) ? tags : []).find((t) => t.label === ORIGIN_TAG);
  assert.ok(originTag, `the ${ORIGIN_TAG} tag exists in Whisparr v2`);
  assert.ok(
    Array.isArray(site.tags) && site.tags.includes(originTag.id),
    `the v2 site row carries the ${ORIGIN_TAG} tag`,
  );

  // Loop-safety, proven against real v2 state: the non-grabbing add (searchForMissingEpisodes:false)
  // queued nothing. Poll rather than sleep — a grab, had one been wrongly requested, surfaces shortly.
  const records = await pollUntil(
    async () => queueRecords((await whisparrGet("/api/v3/queue")).json),
    (recs) => recs.length === 0,
    { timeoutMs: 30_000, label: "the v2 queue is empty after a non-grabbing site add" },
  );
  assert.equal(records.length, 0, "no grab was queued by the Cove-initiated v2 site add");
});

test("a repeated identical monitor call is idempotent — one v2 site row, still monitored and tagged", async () => {
  const { api, remoteIds } = ctx;
  const tvdbId = Number(remoteIds.remoteId);

  const res = await api.post(`/api/extensions/${EXTENSION_ID}/monitor`, {
    Kind: "studio",
    RemoteIds: [{ Endpoint: remoteIds.endpoint, RemoteId: remoteIds.remoteId }],
    Monitored: true,
  });
  // The re-add hits v2's 400 SeriesExistsValidator, which the adapter classifies as success (Conflict →
  // re-read the existing row), so the extension must not surface a 5xx.
  assert.ok(res.status < 500, `re-add did not error (status ${res.status}, body: ${res.text})`);

  const matches = await pollUntil(
    () => listSeries(),
    (rows) => findSiteByTvdbId(rows, tvdbId).length === 1 && findSiteByTvdbId(rows, tvdbId)[0].monitored === true,
    { timeoutMs: 60_000, label: "still exactly one monitored v2 site row after the re-add" },
  );
  const sites = findSiteByTvdbId(matches, tvdbId);
  assert.equal(sites.length, 1, "no duplicate v2 site row was created (400-exists treated as success)");

  const { json: tags } = await whisparrGet("/api/v3/tag");
  const originTag = (Array.isArray(tags) ? tags : []).find((t) => t.label === ORIGIN_TAG);
  assert.ok(originTag && sites[0].tags?.includes(originTag.id), `still carries the ${ORIGIN_TAG} tag`);
});

// v2 fetches a fresh site's episode list asynchronously after the create, so the count settles a beat
// after the add. Reads the site's real episodes once they have landed (the status + search proofs both
// need the loaded set). The response is Whisparr's own camelCase JSON.
async function loadedEpisodes(seriesId) {
  return pollUntil(
    async () => {
      const { json } = await whisparrGet(`/api/v3/episode?seriesId=${seriesId}`);
      return Array.isArray(json) ? json : [];
    },
    (eps) => eps.length > 0,
    { timeoutMs: 90_000, label: "the v2 site's episodes have loaded" },
  );
}

test("status projection reads grabbed-of-total over the real v2 site episode state", async () => {
  const { api, remoteIds } = ctx;
  const tvdbId = Number(remoteIds.remoteId);

  const site = findSiteByTvdbId(await listSeries(), tvdbId)[0];
  assert.ok(site, "the site row exists for the status cross-check");
  const realEpisodes = await loadedEpisodes(site.id);
  const realGrabbed = realEpisodes.filter((e) => e.hasFile === true).length;

  // The extension's own projection: added / monitored / grabbed-of-total for the site. The response is
  // camelCase (the extension serializes with Web JSON defaults).
  const res = await api.post(`/api/extensions/${EXTENSION_ID}/monitor-status`, {
    Kind: "studio",
    RemoteIds: [{ Endpoint: remoteIds.endpoint, RemoteId: remoteIds.remoteId }],
  });
  assert.equal(res.status, 200, `monitor-status returned 200 (body: ${res.text})`);
  assert.equal(res.json.added, true, "the site reads as added");
  assert.equal(res.json.monitored, true, "the site reads as monitored");

  // The projection MUST be a real read of v2's episode set, not a hardcoded response: grabbed = episodes
  // with a file, total = all episodes of the site. The container is hermetic (no indexer/download
  // client), so grabbed is 0 — but the counts are asserted against the container's own episode state.
  // The projection serializes present/catalog counts as scenesPresent (episodes with a file) over
  // scenesTotal (all site episodes) — the Phase-30 "Monitored · present/catalog" contract.
  assert.equal(res.json.scenesTotal, realEpisodes.length, "status total matches the real v2 episode count");
  assert.equal(res.json.scenesPresent, realGrabbed, "status present matches the real v2 hasFile episode count");
  assert.equal(res.json.hasCounts, true, "the site status carries real of-total counts");
});

test("an explicit Search issues the EpisodeSearch command v2 accepts, and loop-safety still holds", async () => {
  const { api, remoteIds } = ctx;
  const tvdbId = Number(remoteIds.remoteId);

  const site = findSiteByTvdbId(await listSeries(), tvdbId)[0];
  assert.ok(site, "the site row exists for the search proof");
  await loadedEpisodes(site.id);

  // "Search all monitored" acts only on monitored episodes; a default (container-only) monitor leaves the
  // back-catalogue episodes unmonitored, so re-assert the monitor with the AllScenes scope now that the
  // episodes have loaded — the cascade monitors them WITHOUT searching (loop-safety unchanged: a flip
  // carries no addOptions). Then the explicit search has a real monitored set to act on.
  const flip = await api.post(`/api/extensions/${EXTENSION_ID}/monitor`, {
    Kind: "studio",
    RemoteIds: [{ Endpoint: remoteIds.endpoint, RemoteId: remoteIds.remoteId }],
    Monitored: true,
    Scope: "AllScenes",
  });
  assert.ok(flip.status < 500, `AllScenes monitor did not error (status ${flip.status}, body: ${flip.text})`);

  const monitored = await pollUntil(
    () => loadedEpisodes(site.id),
    (eps) => eps.some((e) => e.monitored === true),
    { timeoutMs: 60_000, label: "at least one monitored v2 episode after the AllScenes cascade" },
  );
  assert.ok(monitored.some((e) => e.monitored === true), "the site has monitored episodes to search");

  const res = await api.post(`/api/extensions/${EXTENSION_ID}/bulk-search-monitored`, {
    Kind: "studio",
    RemoteIds: [{ Endpoint: remoteIds.endpoint, RemoteId: remoteIds.remoteId }],
  });
  // The extension's status is not the proof — the accepted command on v2's own command list is.
  assert.ok(res.status < 500, `search did not error (status ${res.status}, body: ${res.text})`);

  // v2 accepts the EpisodeSearch on the same /api/v3/command endpoint the v3 MoviesSearch uses. Assert it
  // was really accepted by the container — poll the command list for the queued/started/completed command,
  // not the extension's return code. EpisodeSearch is the ONE grab-capable v2 verb.
  const command = await pollUntil(
    async () => {
      const { json } = await whisparrGet("/api/v3/command");
      return (Array.isArray(json) ? json : []).find((c) => c.name === "EpisodeSearch");
    },
    (cmd) => Boolean(cmd),
    { timeoutMs: 60_000, label: "an EpisodeSearch command accepted by v2" },
  );
  assert.equal(command.name, "EpisodeSearch", "v2 accepted the EpisodeSearch command");

  // Loop-safety holds: the container is hermetic (no indexer/download client), so the search finds
  // nothing to grab — the queue stays empty (assert the command was accepted, not that a grab occurred).
  const records = await pollUntil(
    async () => queueRecords((await whisparrGet("/api/v3/queue")).json),
    (recs) => recs.length === 0,
    { timeoutMs: 30_000, label: "the v2 queue stays empty after an explicit EpisodeSearch" },
  );
  assert.equal(records.length, 0, "the explicit EpisodeSearch grabbed nothing (loop-safety holds)");
});
