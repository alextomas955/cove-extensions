// The pre-seeds a Whisparr fixture needs before anything can be asked of it: the configuration file
// v2 must already hold when it starts, because that generation takes its API key from nowhere else;
// the import history neither generation offers any way to create; and the catalogue entities whose
// add route would otherwise be a call to the vendor's metadata service.
//
// Delivered as content into the create→start window rather than through a bind mount, for the same
// reason nothing else in this harness bind-mounts: the host's Docker file-sharing configuration
// never enters into it, so this behaves the same on any contributor's machine and any CI runner.
import { join } from "node:path";
import { attemptUntil } from "./poll.mjs";
import { APP_USER } from "./whisparr-images.mjs";

// The database each generation serves its own API from. The seeder takes this as an argument rather
// than choosing for itself, so the one database it may open is always named by its caller.
const DATABASES = { v3: "/config/whisparr3.db", v2: "/config/whisparr2.db" };

// A committed file copied in, never a heredoc and never a string assembled in shell: a
// heredoc-written script carries CRLF into every path it handles, and the failure then blames the
// path.
const SEEDER_SOURCE = join(import.meta.dirname, "whisparr-seed-history.py");
const SEEDER_TARGET = "/tmp/whisparr-seed-history.py";

/** @see SEEDER_SOURCE */
const ENTITY_SEEDER_SOURCE = join(import.meta.dirname, "whisparr-seed-entities.py");
const ENTITY_SEEDER_TARGET = "/tmp/whisparr-seed-entities.py";

// The resource each seeded kind is projected under. The id in the path is the FOREIGN one the seed
// wrote rather than the row id, which is the addressing the extension itself uses.
const ENTITY_PATHS = { studio: "/api/v3/studio", performer: "/api/v3/performer" };

// What the instance offers to add against. Its first entry fills the seed's NOT NULL profile column.
const QUALITY_PROFILE_PATH = "/api/v3/qualityprofile";

// The catalogue tables this harness seeds exist on the v3 schema alone; v2 carries its own
// Sonarr-lineage shape and is addressed as a series. Declared rather than attempted, so a caller
// asking for the wrong generation is told which one seeds entities instead of failing on a column.
const ENTITY_GENERATIONS = ["v3"];

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
 * `data` gives the rows a payload: one entry per row from the newest down, each an object of
 * STRING values written into that row's `Data` column. Without it every row carries an empty
 * object, which is enough to prove a reader imported nothing and cannot prove it imported
 * anything — a reported path lives in `Data`.
 *
 * `eventTypes` names the stored EventType integers to cycle over the rows, and defaults to the
 * pair the seeder declares. The rows descend a minute apart, so a caller that needs a row of one
 * kind to be the NEWEST row names that kind alone.
 *
 * `expectedTotal` is what the read-back waits for the instance to report, and defaults to `count`.
 * A second seed against an instance that already has a past must pass the running total, or the
 * read-back waits for a number the instance passed before this call was made.
 *
 * @param {{container: import("testcontainers").StartedTestContainer, api: {get: Function},
 *          generation: "v3"|"v2", count?: number, data?: (Record<string,string>|null)[],
 *          eventTypes?: number[], expectedTotal?: number}} options
 */
export async function seedHistory({
  container,
  api,
  generation,
  count = DEFAULT_HISTORY_ROWS,
  data,
  eventTypes,
  expectedTotal = count,
}) {
  const database = DATABASES[generation];
  if (database === undefined) {
    throw new Error(
      `seedHistory: no database is declared for generation "${generation}"; declared generations are ${Object.keys(DATABASES).join(", ")}.`,
    );
  }

  await container.copyFilesToContainer([{ source: SEEDER_SOURCE, target: SEEDER_TARGET }]);
  // A copied file arrives root-owned, and the chown is the only step here that needs root. The
  // seeder itself is run AS the app's own user so the write-ahead and shared-memory siblings it
  // touches keep belonging to the process that has to go on using them.
  await container.exec(["chown", APP_USER, SEEDER_TARGET], { user: "root" });
  const written = await runSeeder(container, generation, count, database, data, eventTypes);

  const {
    settled,
    value: readBack,
    note,
  } = await attemptUntil(
    async (_signal, record) => {
      const response = await api.get(`/api/v3/history?page=1&pageSize=${expectedTotal + 2}`);
      record(`${response.status} with totalRecords ${response.json?.totalRecords ?? "absent"}`);
      return response.status === 200 && response.json?.totalRecords === expectedTotal
        ? { value: response }
        : null;
    },
    { timeoutMs: READ_BACK_TIMEOUT_MS, intervalMs: 500, label: "seedHistory" },
  );
  if (!settled) {
    throw new Error(
      `seedHistory: ${generation} wrote ${count} row(s) into ${database}, and GET /api/v3/history still answered ${note} after ${READ_BACK_TIMEOUT_MS}ms while waiting for totalRecords ${expectedTotal}. ` +
        "The likeliest cause is an orphaned seed: the reader inner-joins each history row to its parent library row and silently drops any whose parent is missing, so the table keeps the rows while the API reports none of them.",
    );
  }

  return {
    count,
    totalRecords: expectedTotal,
    eventTypeNames: renderedEventTypes(written, readBack.json.records),
    readBack,
  };
}

/**
 * Writes one studio or performer into a generation's own database, and answers with it as that
 * instance's API then projects it.
 *
 * The datastore rather than the add route, and that is the whole reason this exists: an add resolves
 * its foreign id against the vendor's metadata service, so it is a call to a third party this
 * harness does not control. The pinned v3 build relays that service's own failure for an id it
 * cannot resolve, which would make an entity's mere existence depend on someone else's uptime.
 *
 * The read-back is not decoration. A row the app will not project is indistinguishable from one that
 * was never written, and a spec asserting against an entity that is not there fails naming its own
 * assertion rather than the seed.
 *
 * `qualityProfileId` defaults to the first profile the instance offers, because the column is NOT
 * NULL and the instance owns the value. It is read from the instance rather than assumed to be 1.
 *
 * @param {{container: import("testcontainers").StartedTestContainer, api: {get: Function},
 *          generation: "v3", kind: "studio"|"performer", foreignId: string, title: string,
 *          rootFolderPath: string, qualityProfileId?: number, monitored?: boolean}} options
 * @returns {Promise<object>} the entity as the instance projects it, its row id included
 */
export async function seedEntity({
  container,
  api,
  generation,
  kind,
  foreignId,
  title,
  rootFolderPath,
  qualityProfileId,
  monitored = false,
}) {
  if (!ENTITY_GENERATIONS.includes(generation)) {
    throw new Error(
      `seedEntity: no catalogue seed is wired for generation "${generation}"; it is wired for ${ENTITY_GENERATIONS.join(", ")}.`,
    );
  }
  const entityPath = ENTITY_PATHS[kind];
  if (entityPath === undefined) {
    throw new Error(
      `seedEntity: no resource is declared for kind "${kind}"; declared kinds are ${Object.keys(ENTITY_PATHS).join(", ")}.`,
    );
  }
  if (!rootFolderPath) {
    throw new Error(
      "seedEntity: no rootFolderPath given; the column is NOT NULL and a registered root is the only value the instance accepts.",
    );
  }

  const profileId = qualityProfileId ?? (await firstQualityProfileId(api, generation));

  await container.copyFilesToContainer([
    { source: ENTITY_SEEDER_SOURCE, target: ENTITY_SEEDER_TARGET },
  ]);
  // A copied file arrives root-owned, and the chown is the only step here needing root. The seeder
  // runs AS the app's own user so the write-ahead and shared-memory siblings it touches keep
  // belonging to the process that has to go on using them.
  await container.exec(["chown", APP_USER, ENTITY_SEEDER_TARGET], { user: "root" });

  const written = await container.exec(
    [
      "python3",
      ENTITY_SEEDER_TARGET,
      "--db",
      DATABASES[generation],
      "--kind",
      kind,
      "--foreign-id",
      foreignId,
      "--title",
      title,
      "--quality-profile-id",
      String(profileId),
      "--root-folder-path",
      rootFolderPath,
      "--monitored",
      monitored ? "true" : "false",
    ],
    { user: APP_USER },
  );
  if (written.exitCode !== 0) {
    throw new Error(
      `seedEntity: the seeder exited ${written.exitCode} writing a ${kind} "${foreignId}" into ${DATABASES[generation]} on ${generation}: ${written.output}`,
    );
  }

  const projected = await api.get(`${entityPath}/${encodeURIComponent(foreignId)}`);
  if (projected.status !== 200) {
    throw new Error(
      `seedEntity: ${generation} answered GET ${entityPath}/${foreignId} with ${projected.status} after the seed reported ${written.output.trim()}. ` +
        "The row is in the table and the app will not project it, so nothing asserted against this entity would be about the extension.",
    );
  }
  return projected.json;
}

/**
 * The first quality profile the instance offers.
 *
 * Polled rather than read once: a freshly started instance answers this route before it has finished
 * writing its own defaults, so a single read races that write and reports an instance with none.
 */
async function firstQualityProfileId(api, generation) {
  const { settled, value, note } = await attemptUntil(
    async (_signal, record) => {
      const profiles = await api.get(QUALITY_PROFILE_PATH);
      record(`${profiles.status} with ${profiles.json?.length ?? "no"} profile(s)`);
      const id = profiles.json?.[0]?.id;
      return typeof id === "number" ? { value: id } : null;
    },
    { timeoutMs: READ_BACK_TIMEOUT_MS, intervalMs: 500, label: "firstQualityProfileId" },
  );
  if (!settled) {
    throw new Error(
      `seedEntity: GET ${QUALITY_PROFILE_PATH} on ${generation} last answered ${note} after ${READ_BACK_TIMEOUT_MS}ms, so no entity can be seeded: the column is NOT NULL and the instance owns the value.`,
    );
  }
  return value;
}

async function runSeeder(container, generation, count, database, data, eventTypes) {
  const result = await container.exec(
    [
      "python3",
      SEEDER_TARGET,
      "--generation",
      generation,
      "--count",
      String(count),
      "--db",
      database,
      ...(data ? ["--data", JSON.stringify(data)] : []),
      ...(eventTypes ? ["--event-types", JSON.stringify(eventTypes)] : []),
    ],
    { user: APP_USER },
  );
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
