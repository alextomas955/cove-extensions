// The full hermetic acquire→import pipeline (node:test): a Cove-owned scene is added + searched, the
// grab flows through a REAL download client, and the completed file is imported back — proven against
// Whisparr's + qBit's OWN APIs, not the extension's return code.
//
// Stack (pipeline mode): Cove + SkyHook stub + Whisparr + qBittorrent 4.6.7 + a Torznab fake indexer,
// all on one Docker network; Whisparr + qBit share a dedicated /data volume so Whisparr imports the
// completed download by hardlink. The fake indexer serves a webseed torrent of a valid fixture MP4, so
// the download completes with no peers/tracker/DHT.
//
// Load-bearing assertions:
//   1. Loop-safety: the add itself never grabs (only the explicit grab does).
//   2. The interactive grab flows a REAL release → qBit reaches 100% (webseed download, no peers).
//   3. Whisparr imports the completed download (downloadFolderImported) → the movie gains a file.
//   4. Whisparr's On-Import webhook round-trips to Cove — the extension's import log records the ingest.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { startWhisparrSyncHarness, EXTENSION_ID } from "../lib/setup.mjs";
import { registerWebhookNotification } from "../lib/whisparr-webhook.mjs";

// Whisparr posts the webhook from INSIDE its own container, so it must reach Cove by the shared-network alias.
const COVE_WEBHOOK_URL = `http://cove:5073/api/extensions/${EXTENSION_ID}/webhook`;

let ctx;

async function whisparrGet(path) {
  const res = await fetch(`${ctx.whisparr.baseUrlFromHost}${path}`, {
    headers: { "X-Api-Key": ctx.whisparr.apiKey },
  });
  const text = await res.text();
  return { status: res.status, json: text ? JSON.parse(text) : undefined };
}

async function qbitTorrents() {
  const res = await fetch(`${ctx.qbit.urlFromHost}/api/v2/torrents/info`);
  return res.ok ? res.json() : [];
}

before(async () => {
  ctx = await startWhisparrSyncHarness({ version: "v3", pipeline: true });
}, { timeout: 900_000 });

after(async () => {
  await ctx?.stop();
}, { timeout: 180_000 });

test("a Cove-owned scene, searched, grabs a real release that downloads to 100% and imports back", async () => {
  const { api, remoteIds } = ctx;
  assert.ok(remoteIds.sceneVideoId, "a seeded video carries the per-scene StashDB id");
  assert.ok(remoteIds.sceneRemoteId, "the allowlist yielded a per-scene StashDB id");
  assert.ok(ctx.provisioned?.indexerId, "the Torznab indexer was provisioned");
  assert.ok(ctx.provisioned?.downloadClientId, "the qBittorrent download client was provisioned");

  // Add the scene (monitored, NON-grabbing) → a real Whisparr movie row.
  const add = await api.post(`/api/extensions/${EXTENSION_ID}/scene-add`, { CoveId: remoteIds.sceneVideoId });
  assert.ok(add.status < 500, `scene-add did not error (status ${add.status}, body: ${add.text})`);

  const movie = await pollUntil(
    async () => {
      const { json } = await whisparrGet("/api/v3/movie");
      return (Array.isArray(json) ? json : []).find((m) => m.foreignId === remoteIds.sceneRemoteId);
    },
    (m) => Boolean(m),
    { timeoutMs: 60_000, label: "the added scene is a Whisparr movie" },
  );

  // Loop-safety: the ADD alone must not have grabbed anything.
  assert.equal((await qbitTorrents()).length, 0, "the non-grabbing add left the download client empty");

  // Register the On-Import webhook (real minted secret, via the shared-network cove alias) BEFORE the
  // grab, so Whisparr's post-import notification round-trips to Cove and the extension logs the ingest.
  const urlRes = await api.get(`/api/extensions/${EXTENSION_ID}/webhook-url`);
  const token = new URL(urlRes.json.url).searchParams.get("token");
  assert.ok(token, "a webhook secret was minted");
  await registerWebhookNotification({ whisparr: ctx.whisparr, version: "v3", coveWebhookUrl: COVE_WEBHOOK_URL, token });

  // Interactive grab: list the indexer releases for the scene, then grab one. This is the user-driven
  // path that forces the grab (automatic MoviesSearch applies match/quality gates that a hermetic fake
  // release cannot satisfy; the interactive pick is exactly how a user grabs a specific release).
  const releases = await pollUntil(
    async () => (await api.post(`/api/extensions/${EXTENSION_ID}/scene-releases-list`, { CoveId: remoteIds.sceneVideoId })).json,
    (r) => Array.isArray(r?.releases) && r.releases.length > 0,
    { timeoutMs: 60_000, label: "the fake indexer returns a release for the scene" },
  );
  const pick = releases.releases[0];
  const grab = await api.post(`/api/extensions/${EXTENSION_ID}/scene-grab-release`, {
    CoveId: remoteIds.sceneVideoId,
    Guid: pick.guid,
    IndexerId: pick.indexerId,
  });
  assert.ok(grab.json?.grabbed, `scene-grab-release grabbed the release (status ${grab.status}, body: ${grab.text})`);

  // 1) qBit downloads the webseed torrent to 100% (no peers).
  await pollUntil(
    async () => (await qbitTorrents())[0],
    (t) => t && t.progress === 1,
    { timeoutMs: 120_000, label: "qBit completes the webseed download (progress=1)" },
  );

  // 2) Whisparr imports the completed download → the movie gains a file. Whisparr Eros's /movie/{id}
  // does not populate `hasFile`; the imported movie file is proven by movieFileId (+ sizeOnDisk).
  try {
    await pollUntil(
      async () => (await whisparrGet(`/api/v3/movie/${movie.id}`)).json,
      (m) => (m?.movieFileId ?? 0) > 0 && (m?.sizeOnDisk ?? 0) > 0,
      { timeoutMs: 300_000, label: "Whisparr imports the completed download (movieFileId)" },
    );
  } catch (err) {
    // Surface the exact import blocker (queue status messages) before failing — the stack tears down after.
    const queue = (await whisparrGet("/api/v3/queue?pageSize=20")).json;
    const rows = (queue?.records ?? queue ?? []).map((r) => ({
      status: r.status, state: r.trackedDownloadState, tds: r.trackedDownloadStatus,
      msgs: (r.statusMessages ?? []).flatMap((m) => [m.title, ...(m.messages ?? [])]),
    }));
    console.error("IMPORT DIAGNOSTIC queue:", JSON.stringify(rows, null, 2));
    const logs = await ctx.whisparr.container.logs().catch(() => null);
    if (logs) {
      const text = await new Promise((res) => { let b = ""; logs.on("data", (d) => (b += d)); logs.on("end", () => res(b)); setTimeout(() => res(b), 3000); });
      console.error("WHISPARR LOG tail:\n" + text.split("\n").filter((l) => /import|sample|reject|manual|not.*found|runtime|movie/i.test(l)).slice(-15).join("\n"));
    }
    throw err;
  }

  // Whisparr writes the history row asynchronously AFTER it sets movieFileId, so the poll above clearing
  // is not a guarantee this row exists yet — reading once here raced it and failed intermittently with
  // only "grabbed" recorded. Poll for the event rather than sampling the log at one instant.
  const history = await pollUntil(
    async () => (await whisparrGet(`/api/v3/history?pageSize=20&movieId=${movie.id}`)).json,
    (h) => (h?.records ?? []).some((r) => r.eventType === "downloadFolderImported"),
    { timeoutMs: 60_000, intervalMs: 1000, label: "Whisparr records the downloadFolderImported history event" },
  );
  const events = (history?.records ?? []).map((r) => r.eventType);
  assert.ok(events.includes("downloadFolderImported"), `history shows an import (${events.join(",")})`);

  // 4) Whisparr's On-Import fired the webhook → Cove received it and OPENED the file. /import-log carries
  // two bounded aggregates rather than a per-attempt journal (the per-entry list went when the store was
  // capped — nothing rendered it and a per-import row grows with the library), so the ingest is proven by
  // the import dependency reaching "imported" with no unresolved path mismatch. That is the load-bearing
  // claim for this whole pipeline: not that a notification arrived, but that Cove could read what landed.
  const log = await pollUntil(
    async () => (await api.get(`/api/extensions/${EXTENSION_ID}/import-log`)).json,
    (l) => l?.pipelineHealth?.some((d) => d.dependency === "import" && d.outcome === "imported"),
    { timeoutMs: 120_000, intervalMs: 1000, label: "the import dependency reaching the imported outcome" },
  );

  const importHealth = log.pipelineHealth.find((d) => d.dependency === "import");
  assert.equal(importHealth.outcome, "imported", "Cove ingested the grabbed file at the path Whisparr imported it to");
  assert.equal(importHealth.consecutiveFailures, 0, "the import leg records no failure");
  assert.equal(log.syncHealth.pathMismatch, 0, "no unresolved path mismatch across the acquire→import chain");
  assert.ok(log.lastEventTicks > 0, "the inbound On-Import webhook was recorded");
});
