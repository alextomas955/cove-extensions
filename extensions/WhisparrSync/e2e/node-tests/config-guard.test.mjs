// Offline correctness spec (node:test) — the Whisparr SIDE of a configuration refusal. A guarded action driven
// with an incomplete stored configuration must reach Whisparr not at all, and the proof is positive rather than
// circumstantial: a stand-in primed to answer EVERY call successfully records ZERO requests. A count that merely
// did not grow proves nothing, because a request that failed upstream also leaves the count where it was.
//
// Three facts, in the order they earn each other:
//   1. The refusal is a real extension response — `application/json` carrying the refusal code and the unmet
//      option keys. An unmatched extension route falls through to the host's single-page catch-all and answers
//      200 with an HTML document, so a status-only assertion would report a route that does not exist as a pass.
//   2. Zero requests reach the primed stand-in with the address unset, and MORE THAN ZERO reach the same
//      stand-in through the same call once an address is stored. The second half is what makes the first
//      discriminating: without it, an unreachable stub would look identical to a short circuit.
//   3. Against the REAL Whisparr container: a refused action leaves the movie / studio / series counts and the
//      queue and history untouched — and the same action with a stored address creates the row, carrying the
//      FIRST quality profile the instance offers (the add derives it per add; nothing is stored), still with an
//      untouched queue and history. The add is the loop-safe verb, so "not searched" is a real assertion here
//      rather than a tautology.
//
// CONTENT SAFETY: every identity comes from the suite's own synthetic allowlist fixture; no real metadata and no
// credential is referenced. The stand-in authenticates nothing, so its key is meaningless.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { GenericContainer, Wait } from "testcontainers";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { startWhisparrSyncHarness, EXTENSION_ID } from "../lib/setup.mjs";

const STUB_ALIAS = "config-guard-stub";
const STUB_PORT = 6989;
const NO_ADDRESS = "";

let ctx;
let stub;
let firstOfferedProfileId;

/**
 * A stand-in for Whisparr's whole API that answers every call successfully and records each one. Its purpose is
 * the inverse of the usual stub's: what matters is not what it returns but that its log stays EMPTY, which is
 * only meaningful because it is primed to succeed. `GET /__requests` is its own read-out and is never logged.
 */
async function startRecordingStub({ networkName }) {
  const script = `
const http = require('http');
const seen = [];
const server = http.createServer((req, res) => {
  const path = req.url.split('?')[0];
  if (path === '/__requests') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(seen));
    return;
  }
  if (req.method === 'DELETE' && path === '/__reset') {
    seen.length = 0;
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end('[]');
    return;
  }
  let body = '';
  req.on('data', (c) => { body += c; });
  req.on('end', () => {
    seen.push({ method: req.method, path });
    // A generic success for every shape the outward spine asks for: a list read gets a one-row list, a create
    // gets a created row. The point is that nothing here is ever reached, not that it is faithful.
    const created = req.method === 'POST' || req.method === 'PUT';
    res.writeHead(created ? 201 : 200, { 'Content-Type': 'application/json' });
    res.end(created ? JSON.stringify({ id: 1, monitored: true, tags: [1] }) : JSON.stringify([{ id: 1, label: 'cove-sync', path: '/data', name: 'stub' }]));
  });
});
server.listen(${STUB_PORT}, '0.0.0.0');
`;

  const container = await new GenericContainer("node:22-alpine")
    .withNetworkMode(networkName)
    .withNetworkAliases(STUB_ALIAS)
    .withExposedPorts(STUB_PORT)
    .withCopyContentToContainer([{ content: script, target: "/stub/server.cjs" }])
    .withCommand(["node", "/stub/server.cjs"])
    .withWaitStrategy(Wait.forListeningPorts())
    .withStartupTimeout(60_000)
    .start();

  const fromHost = `http://${container.getHost()}:${container.getMappedPort(STUB_PORT)}`;
  return {
    baseUrlFromCove: `http://${STUB_ALIAS}:${STUB_PORT}`,
    async requests() {
      const res = await fetch(`${fromHost}/__requests`);
      return res.json();
    },
    async reset() {
      await fetch(`${fromHost}/__reset`, { method: "DELETE" });
    },
    async stop() {
      await container.stop();
    },
  };
}

/** Reads Whisparr's own API on its mapped host port with the out-of-band key (never the extension's). */
async function whisparrGet(path) {
  const res = await fetch(`${ctx.whisparr.baseUrlFromHost}${path}`, {
    headers: { "X-Api-Key": ctx.whisparr.apiKey },
  });
  const text = await res.text();
  let json;
  try {
    json = text ? JSON.parse(text) : undefined;
  } catch {
    json = undefined;
  }
  return { status: res.status, json };
}

function rows(json) {
  if (Array.isArray(json)) return json;
  return json?.records ?? [];
}

/**
 * A count per collection, with a generation that does not offer one recorded as `absent` rather than as zero —
 * absence and emptiness are different facts and collapsing them is how a matrix reports a pass it did not earn.
 */
async function snapshot() {
  const out = {};
  for (const name of ["movie", "studio", "series", "queue", "history"]) {
    const { status, json } = await whisparrGet(`/api/v3/${name}`);
    out[name] = status === 404 ? "absent" : rows(json).length;
  }
  return out;
}

/**
 * Writes a COMPLETE options object varying only the address. Completeness is the point: `WithSubmitted` rebuilds
 * the stored connection on every write, so a partial body loses a field.
 */
async function storeOptions({ baseUrl }) {
  const res = await ctx.api.post(`/api/extensions/${EXTENSION_ID}/options`, {
    BaseUrl: baseUrl,
    ApiKey: ctx.whisparr.apiKey,
    SelectedVersion: "v3",
    StashDbEndpoint: ctx.remoteIds.endpoint,
    TagsOnAdd: ["cove"],
    MonitorNewByDefault: true,
    AllowQualityUpgrades: false,
  });
  assert.ok(res.status < 300, `storing options succeeded (status ${res.status}, body: ${res.text})`);

  // Read the server's own predicate back rather than trusting the write — this is the fact the guard consults.
  const status = await ctx.api.get(`/api/extensions/${EXTENSION_ID}/status`);
  assert.equal(status.status, 200);
  assert.deepEqual(
    status.json.missingRequiredOptions,
    baseUrl === NO_ADDRESS ? ["baseUrl"] : [],
    "the stored configuration is in the state this case needs",
  );
}

/** The guarded action under test: monitoring a studio ON, which creates a Whisparr studio record. */
function monitorStudioOn() {
  return ctx.api.post(`/api/extensions/${EXTENSION_ID}/monitor`, {
    Kind: "studio",
    RemoteIds: [{ Endpoint: ctx.remoteIds.endpoint, RemoteId: ctx.remoteIds.remoteId }],
    Monitored: true,
  });
}

before(async () => {
  ctx = await startWhisparrSyncHarness({ version: "v3" });
  const networks = ctx.harness.container.getNetworkNames();
  stub = await startRecordingStub({ networkName: networks[0] });

  // The id the add is expected to carry is the FIRST row of the instance's own list — the same rule the resolver
  // applies — so the case never asserts against an id this Whisparr does not offer.
  const { json: profiles } = await whisparrGet("/api/v3/qualityprofile");
  firstOfferedProfileId = rows(profiles)[0]?.id;
  assert.ok(firstOfferedProfileId > 0, "the instance offers at least one quality profile");
}, { timeout: 600_000 });

after(async () => {
  await stub?.stop().catch(() => {});
  await ctx?.stop();
}, { timeout: 120_000 });

test("the refusal is a real extension response — JSON carrying the code and the unmet keys, not the host's HTML catch-all", async () => {
  await storeOptions({ baseUrl: NO_ADDRESS });

  const res = await fetch(`${ctx.baseUrl}/api/extensions/${EXTENSION_ID}/monitor`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      Kind: "studio",
      RemoteIds: [{ Endpoint: ctx.remoteIds.endpoint, RemoteId: ctx.remoteIds.remoteId }],
      Monitored: true,
    }),
  });
  const contentType = res.headers.get("content-type") ?? "";
  const text = await res.text();

  // Content type first: an unmatched route answers 200 with `text/html`, so the status alone cannot tell a
  // refusal from a route that was never registered.
  assert.match(contentType, /application\/json/, `the response is JSON, not the app shell (got "${contentType}", body starts: ${text.slice(0, 60)})`);
  assert.ok(!text.includes("<!DOCTYPE"), "the body is not the host's single-page document");

  const body = JSON.parse(text);
  assert.equal(body.code, "CONFIG_INCOMPLETE", "the body carries the refusal code");
  assert.deepEqual(body.options, ["baseUrl"], "the body names the unmet option key");
  assert.equal(res.status, 400, "and it is a 400, which is what the client decodes a code on");
});

test("a refused action reaches Whisparr not at all — a stand-in primed to succeed records zero requests, and the same call with an address reaches it", async () => {
  // ---- the refusal: zero requests against a stand-in that would have answered every one of them ----
  await storeOptions({ baseUrl: NO_ADDRESS });
  await stub.reset();

  const refused = await monitorStudioOn();
  assert.equal(refused.status, 400, `the action was refused (body: ${refused.text})`);
  assert.equal(refused.json.code, "CONFIG_INCOMPLETE");

  const afterRefusal = await stub.requests();
  assert.deepEqual(
    afterRefusal,
    [],
    `no request reached Whisparr at all (saw: ${JSON.stringify(afterRefusal)})`,
  );

  // ---- the positive control: the SAME call with a stored address does reach it, so the zero above is a short
  // circuit rather than an unreachable stand-in ----
  await storeOptions({ baseUrl: stub.baseUrlFromCove });
  await stub.reset();

  const allowed = await monitorStudioOn();
  assert.notEqual(allowed.status, 400, `the action was not refused with an address (body: ${allowed.text})`);

  const afterAllowed = await stub.requests();
  assert.ok(
    afterAllowed.length > 0,
    "the same call with a stored address does reach Whisparr — so the stand-in was reachable and the zero above was the guard",
  );
});

test("a refused action creates nothing in the real Whisparr, and the same action with an address creates the row carrying the instance's first profile without searching", async () => {
  // ---- the refusal, against the real instance: every collection, the queue and the history unchanged ----
  const before = await snapshot();
  await storeOptions({ baseUrl: NO_ADDRESS });

  const refused = await monitorStudioOn();
  assert.equal(refused.status, 400, `the action was refused (body: ${refused.text})`);

  const afterRefusal = await snapshot();
  assert.deepEqual(
    afterRefusal,
    before,
    "the refused action changed no collection, no queue entry and no history entry",
  );

  // ---- the positive case at this tier: the same action with a stored address lands, carries the profile the
  // instance offers first, and still fires no grab ----
  await storeOptions({ baseUrl: ctx.whisparr.baseUrlFromCove });

  const allowed = await monitorStudioOn();
  assert.ok(allowed.status < 500, `the add did not error (status ${allowed.status}, body: ${allowed.text})`);

  // A fresh studio create queues a refresh that rebuilds the row, so poll rather than read once.
  const matches = await pollUntil(
    async () => rows((await whisparrGet("/api/v3/studio")).json).filter((s) => s.foreignId === ctx.remoteIds.remoteId),
    (found) => found.length === 1 && found[0].monitored === true,
    { timeoutMs: 60_000, label: "the monitored studio row the stored address allowed" },
  );
  assert.equal(matches.length, 1, "exactly one studio row was created");
  assert.equal(
    matches[0].qualityProfileId,
    firstOfferedProfileId,
    "the created record carries the instance's first offered quality profile — resolved per add, never stored",
  );

  // Loop-safety: an add never grabs, so the queue and the history are where the refusal left them.
  const afterAdd = await snapshot();
  assert.equal(afterAdd.queue, before.queue, "the add queued no grab");
  assert.equal(afterAdd.history, before.history, "the add wrote no history entry");
  assert.notEqual(afterAdd.studio, before.studio, "and the add really did create a record — the counts move when it is allowed to");
});
