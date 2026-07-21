// Offline correctness spec (node:test) — Cove settings that write Whisparr's config, proven bidirectionally
// against Whisparr's OWN config API (never the extension's return code). The four file-affecting toggles
// live on Whisparr's naming (RenameMovies, ReplaceIllegalCharacters) and media-management
// (AutoRenameFolders, DeleteEmptyFolders) config singletons.
//
// Load-bearing assertions:
//   1. Cove→Whisparr: POST /file-settings flips the four toggles → Whisparr's config/naming +
//      config/mediamanagement reflect the new values.
//   2. The read-modify-write preserves UNKNOWN config fields (the config is a whole-object replace, so a
//      partial body would wipe fields it omits — assert a sentinel field survives).
//   3. Whisparr→Cove: flipping a field directly in Whisparr → GET /file-settings reflects it.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { startWhisparrSyncHarness, EXTENSION_ID } from "../lib/setup.mjs";

let ctx;

async function whisparr(method, path, body) {
  const res = await fetch(`${ctx.whisparr.baseUrlFromHost}${path}`, {
    method,
    headers: { "X-Api-Key": ctx.whisparr.apiKey, ...(body ? { "Content-Type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  return { status: res.status, json: text ? JSON.parse(text) : undefined };
}

before(async () => {
  ctx = await startWhisparrSyncHarness({ version: "v3" });
}, { timeout: 600_000 });

after(async () => {
  await ctx?.stop();
}, { timeout: 120_000 });

test("Cove file-settings write Whisparr's naming + media-management config (RMW preserves unknown fields)", async () => {
  const { api } = ctx;

  // Baseline the real Whisparr config + capture a sentinel field the extension never touches, to prove
  // the whole-object RMW does not wipe unrelated fields.
  const naming0 = (await whisparr("GET", "/api/v3/config/naming")).json;
  const media0 = (await whisparr("GET", "/api/v3/config/mediamanagement")).json;
  assert.ok(naming0 && media0, "Whisparr naming + media-management config are readable");
  const namingSentinel = naming0.standardMovieFormat; // an unrelated naming field
  const mediaSentinelPresent = "recycleBin" in media0; // an unrelated media-management field

  // 1) Cove→Whisparr: flip all four toggles on.
  const set = await api.post(`/api/extensions/${EXTENSION_ID}/file-settings`, {
    RenameMovies: true,
    ReplaceIllegalCharacters: true,
    AutoRenameFolders: true,
    DeleteEmptyFolders: true,
  });
  assert.ok(set.status < 500, `file-settings write did not error (status ${set.status}, body: ${set.text})`);

  const naming1 = (await whisparr("GET", "/api/v3/config/naming")).json;
  const media1 = (await whisparr("GET", "/api/v3/config/mediamanagement")).json;
  assert.equal(naming1.renameMovies, true, "renameMovies reflected in Whisparr");
  assert.equal(naming1.replaceIllegalCharacters, true, "replaceIllegalCharacters reflected in Whisparr");
  // Whisparr maps auto-rename-folders onto renameFolders on some builds; accept either name.
  assert.equal(media1.autoRenameFolders ?? media1.renameFolders, true, "autoRenameFolders reflected in Whisparr");
  assert.equal(media1.deleteEmptyFolders, true, "deleteEmptyFolders reflected in Whisparr");

  // 2) RMW preserved the unrelated fields (a partial body would have wiped them).
  assert.equal(naming1.standardMovieFormat, namingSentinel, "the RMW preserved the unrelated naming field");
  assert.equal("recycleBin" in media1, mediaSentinelPresent, "the RMW preserved the unrelated media field");

  // 3) Whisparr→Cove: flip renameMovies off directly in Whisparr, then the extension's read reflects it.
  await whisparr("PUT", "/api/v3/config/naming", { ...naming1, renameMovies: false });
  const reflected = await pollUntil(
    async () => (await api.get(`/api/extensions/${EXTENSION_ID}/file-settings`)).json,
    (fs) => fs?.renameMovies === false,
    { timeoutMs: 30_000, label: "the extension reflects the Whisparr-side change" },
  );
  assert.equal(reflected.renameMovies, false, "Whisparr→Cove: renameMovies change reflected");
  // The other three stayed on — the reverse read is a true read, not a reset.
  assert.equal(reflected.replaceIllegalCharacters, true, "unrelated toggles unchanged by the reverse read");

  // Restore a clean config.
  await api.post(`/api/extensions/${EXTENSION_ID}/file-settings`, {
    RenameMovies: false,
    ReplaceIllegalCharacters: false,
    AutoRenameFolders: false,
    DeleteEmptyFolders: false,
  });
});
