// The two pre-seeds a Whisparr fixture needs before anything can be asked of it: the configuration
// file v2 must already hold when it starts, because that generation takes its API key from nowhere
// else, and the import history neither generation offers any way to create.
//
// Delivered as content into the create→start window rather than through a bind mount, for the same
// reason nothing else in this harness bind-mounts: the host's Docker file-sharing configuration
// never enters into it, so this behaves the same on any contributor's machine and any CI runner.
import { join } from "node:path";
import { attemptUntil } from "./poll.mjs";

// The database each generation serves its own API from. The seeder takes this as an argument rather
// than choosing for itself, so the one database it may open is always named by its caller.
const DATABASES = { v3: "/config/whisparr3.db", v2: "/config/whisparr2.db" };

// A committed file copied in, never a heredoc and never a string assembled in shell: a
// heredoc-written script carries CRLF into every path it handles, and the failure then blames the
// path.
const SEEDER_SOURCE = join(import.meta.dirname, "whisparr-seed-history.py");
const SEEDER_TARGET = "/tmp/whisparr-seed-history.py";

// Enough rows to span every event type the seeder writes, which is what makes the integer-to-name
// rendering observable rather than assumed.
const DEFAULT_HISTORY_ROWS = 3;

// A write made from outside the app is not read-your-writes through its API: the reader answers an
// empty page for a short while after the rows and their parent are both committed and visible to
// SQL. Asserting on the first read is therefore a coin toss, and the losing side looks exactly like
// the orphan failure below.
const READ_BACK_TIMEOUT_MS = 30_000;

/**
 * The minimal document the app accepts. It expands this to its full form on its first write, so
 * nothing here anticipates what it will add.
 *
 * `AuthenticationMethod` governs the UI session, NOT the API key: both generations refuse an
 * unauthenticated API read even under `None`.
 *
 * @param {{apiKey: string, port: number}} options
 * @returns {string}
 */
export function buildConfigXml({ apiKey, port }) {
  if (!apiKey) {
    throw new Error(
      "buildConfigXml: no apiKey given; a config carrying none leaves the app minting its own, which no caller can present.",
    );
  }
  if (!port) {
    throw new Error("buildConfigXml: no port given.");
  }
  return `<Config>
  <ApiKey>${apiKey}</ApiKey>
  <AuthenticationMethod>None</AuthenticationMethod>
  <AuthenticationRequired>DisabledForLocalAddresses</AuthenticationRequired>
  <Port>${port}</Port>
  <BindAddress>*</BindAddress>
  <LogLevel>info</LogLevel>
  <AnalyticsEnabled>False</AnalyticsEnabled>
</Config>
`;
}

/**
 * Writes `count` rows of import history into one generation's own database, then proves they are
 * there by reading them back through that instance's history API. Returns the written row count,
 * the observed integer-to-name rendering of every event type written, and the records themselves.
 *
 * The read-back is the whole point. The history reader inner-joins to the parent library row, so a
 * history row whose parent is missing is dropped: the table holds the rows, the API answers an
 * empty page, and nothing anywhere reports an error. A seeder that returns on its own say-so
 * therefore reports success on a seed that produced nothing.
 *
 * `api` must already present the instance's API key; both generations answer 401 without one.
 *
 * @param {{container: import("testcontainers").StartedTestContainer, api: {get: Function},
 *          generation: "v3"|"v2", count?: number}} options
 */
export async function seedHistory({ container, api, generation, count = DEFAULT_HISTORY_ROWS }) {
  const database = DATABASES[generation];
  if (database === undefined) {
    throw new Error(
      `seedHistory: no database is declared for generation "${generation}"; declared generations are ${Object.keys(DATABASES).join(", ")}.`,
    );
  }

  await container.copyFilesToContainer([{ source: SEEDER_SOURCE, target: SEEDER_TARGET }]);
  // A copied file arrives root-owned. The chown is the only step run as root: the seeder itself runs
  // as the container's default user, so the WAL and shm siblings it touches stay owned by the app.
  await container.exec(["chown", "1000:1000", SEEDER_TARGET], { user: "root" });
  const written = await runSeeder(container, generation, count, database);

  const {
    settled,
    value: readBack,
    note,
  } = await attemptUntil(
    async (_signal, record) => {
      const response = await api.get(`/api/v3/history?page=1&pageSize=${count + 2}`);
      record(`${response.status} with totalRecords ${response.json?.totalRecords ?? "absent"}`);
      return response.status === 200 && response.json?.totalRecords === count
        ? { value: response }
        : null;
    },
    { timeoutMs: READ_BACK_TIMEOUT_MS, intervalMs: 500, label: "seedHistory" },
  );
  if (!settled) {
    throw new Error(
      `seedHistory: ${generation} wrote ${count} row(s) into ${database}, and GET /api/v3/history still answered ${note} after ${READ_BACK_TIMEOUT_MS}ms. ` +
        "The likeliest cause is an orphaned seed: the reader inner-joins each history row to its parent library row and silently drops any whose parent is missing, so the table keeps the rows while the API reports none of them.",
    );
  }

  return { count, eventTypeNames: renderedEventTypes(written, readBack.json.records), readBack };
}

async function runSeeder(container, generation, count, database) {
  const result = await container.exec([
    "python3",
    SEEDER_TARGET,
    "--generation",
    generation,
    "--count",
    String(count),
    "--db",
    database,
  ]);
  if (result.exitCode !== 0) {
    throw new Error(
      `seedHistory: the seeder exited ${result.exitCode} against ${generation} (${database}): ${result.output}`,
    );
  }
  return JSON.parse(result.output).rows;
}

/**
 * The integer-to-name rendering the instance itself produced, correlated by source title.
 *
 * Derived by observation because the stored integers are sparse: the name at a given position in
 * the published enum belongs to a different integer, so a map read off that document is wrong
 * without ever failing.
 */
function renderedEventTypes(written, records) {
  const byTitle = new Map(records.map((record) => [record.sourceTitle, record.eventType]));
  return Object.fromEntries(
    written
      .filter((row) => byTitle.has(row.sourceTitle))
      .map((row) => [row.eventType, byTitle.get(row.sourceTitle)]),
  );
}
