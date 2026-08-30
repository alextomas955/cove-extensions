// The credential table, proven against a real host on real Postgres — the one tier that can.
//
// Every other tier proves the migration STRING or the port behind it. None of them proves that the
// host ran that string against its own database, and the gap is not academic: a failed extension
// migration is a host log line and nothing more, so the extension loads, enables and answers every
// request with no table behind it. "The extension is enabled" is therefore not evidence, and neither
// is a green unit test — the entity model those tests run against comes from code, so it agrees with
// the code that produced it whatever the database holds.
//
// The answer is read from the database CATALOG rather than from the extension's own API for the same
// reason. The columns are asserted by name, so a mapping that drifts from the migration fails here
// even though both halves still compile.
//
// Then the host is restarted and the same reads are taken again, because a migration is applied once
// per receipt and the second load is the one that meets an existing table.
//
// Its own instance per test: this restarts the host, which is instance-global.
import {
  test as base,
  expect,
  createApiClient,
  isolatedHarnessFixture,
} from "@cove-extensions/e2e";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const TABLE_NAME = "whisparrsync_credentials";
const MIGRATION_NAME = "001_create_whisparrsync_credentials";

// Transcribed from WhisparrSync.Data.cs's column names, in the order the query sorts them. Written
// out by hand rather than derived, so a rename on either side has to be made in both places.
const COLUMNS = "api_key,generation,updated_at_utc_ticks";

const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),
});

/**
 * Runs one SQL statement in the database container and returns its single unaligned value.
 *
 * The connection details come from the container's own environment rather than being repeated here,
 * so the compose file stays the one place they are written. The statement travels as an environment
 * variable too, which is what lets it hold quotes without any escaping rule to get wrong.
 */
async function queryDb(harness, sql) {
  const result = await harness.execDb(
    ["sh", "-c", 'psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "$SQL"'],
    { env: { SQL: sql } },
  );
  expect(result.exitCode, `psql failed for [${sql}]: ${result.output}`).toBe(0);
  return result.output.trim();
}

/** Whether the credential table exists in the running database (0 or 1). */
function countCredentialTables(harness) {
  return queryDb(
    harness,
    "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' " +
      `AND table_name = '${TABLE_NAME}'`,
  );
}

/** The table's column names, comma-joined in name order. */
function readCredentialColumns(harness) {
  return queryDb(
    harness,
    "SELECT coalesce(string_agg(column_name, ',' ORDER BY column_name), '') " +
      "FROM information_schema.columns WHERE table_schema = 'public' " +
      `AND table_name = '${TABLE_NAME}'`,
  );
}

/** How many receipts the host recorded for the credential migration. */
function countMigrationReceipts(harness) {
  return queryDb(
    harness,
    "SELECT count(*) FROM extension_migrations " +
      `WHERE extension_id = '${EXTENSION_ID}' AND migration_name = '${MIGRATION_NAME}'`,
  );
}

/**
 * Whether the host reports the extension as enabled.
 *
 * Read through the handle rather than captured: a restart re-mints the token and can republish the
 * instance on a different host port.
 */
async function readEnabled(harness) {
  const listed = await createApiClient(
    () => harness.baseUrl,
    () => harness.token,
  ).get("/api/extensions");

  return listed.json?.find((extension) => extension.id === EXTENSION_ID)?.enabled;
}

/**
 * Waits for the host to report the extension enabled.
 *
 * A restart returns on host REACHABILITY, which is weaker than the extensions having loaded, so a
 * single read taken the moment it resolves can answer about a host that has not got there yet.
 */
function waitForEnabled(harness) {
  return pollUntil(
    () => readEnabled(harness),
    (enabled) => enabled === true,
    {
      timeoutMs: 120_000,
      label: `${EXTENSION_ID} to report enabled`,
    },
  );
}

test("the host creates the credential table on Postgres and a second load still finds it", async ({
  isolatedHarness,
}) => {
  // Two container restarts: the one installExtension performs, and the one this test takes to reach
  // the second load. The default per-test budget covers neither.
  test.setTimeout(900_000);

  // ── 1. The host applied the migration ──────────────────────────────────────────────────────────
  expect(await readEnabled(isolatedHarness), `${EXTENSION_ID} is not enabled`).toBe(true);
  expect(
    await countCredentialTables(isolatedHarness),
    `the host did not create ${TABLE_NAME} — an extension migration that fails is only a host log line, so the extension is enabled either way`,
  ).toBe("1");
  expect(
    await readCredentialColumns(isolatedHarness),
    "the table the host created does not carry the columns the model maps",
  ).toBe(COLUMNS);
  expect(
    await countMigrationReceipts(isolatedHarness),
    `the host recorded no receipt for ${MIGRATION_NAME}`,
  ).toBe("1");

  // ── 2. The second load meets a table that already exists ────────────────────────────────────────
  //
  // The receipt makes the host skip the migration, so what is proven here is that the extension
  // loads against a table it did not just create. A migration whose statements were not
  // create-if-absent would also fail this on a database restored without its receipt.
  await isolatedHarness.restart();
  await waitForEnabled(isolatedHarness);

  expect(await readEnabled(isolatedHarness), `${EXTENSION_ID} did not come back enabled`).toBe(
    true,
  );
  expect(
    await countCredentialTables(isolatedHarness),
    `${TABLE_NAME} did not survive the restart`,
  ).toBe("1");
  expect(
    await readCredentialColumns(isolatedHarness),
    "the table's columns changed across the restart",
  ).toBe(COLUMNS);
  expect(
    await countMigrationReceipts(isolatedHarness),
    "the second load re-applied a migration the host had already receipted",
  ).toBe("1");
});
