// The addresses and spellings this extension answers to, written down once.
//
// WHY ONE PLACE. These are strings the specs USE, not values they claim. What pins them is the
// backend suite: a test emits the wire document from the shipped registrations and fails when the
// committed copy differs, so a renamed route is already caught, by name, before any spec runs.
// Repeating each address in every spec that calls it pins nothing a second time and drifts.
//
// WHAT DOES NOT BELONG HERE. A value a spec ASSERTS. Read from the same symbol the product reads,
// an assertion says a string equals itself. Those stay written out where they are asserted - the
// wire spellings of a refusal, a state, a capability set.
//
// WHAT ALSO DOES NOT BELONG HERE. Anything of Whisparr's own. That contract is the vendor's, and
// re-verifying it means re-capturing a payload rather than re-typing a constant. The captured
// deliveries below are paths to those recordings, not transcriptions of them.
import { join } from "node:path";

/** This extension's own id, as its manifest declares it. */
export const EXTENSION_ID = "com.alextomas955.whisparrsync";

/** One of this extension's own routes, addressed the way the host mounts them. */
export const extensionRoute = (path) => `/api/extensions/${EXTENSION_ID}/${path}`;

export const SETTINGS_ROUTE = extensionRoute("settings");
export const DATA_ROUTE = extensionRoute("data");
export const CALLBACK_ROUTE = extensionRoute("callback");
export const CALLBACK_STATUS_ROUTE = extensionRoute("callback/status");
export const CALLBACK_REGISTER_ROUTE = extensionRoute("callback/register");
export const HOST_CONFIGURATION_ROUTE = extensionRoute("host-configuration");
export const CONNECTION_TEST_ROUTE = extensionRoute("connection/test");

// The host's own, not this extension's: stopping and starting the extension is how a spec makes the
// background worker run again without waiting out an interval.
export const DISABLE_ROUTE = extensionRoute("disable");
export const ENABLE_ROUTE = extensionRoute("enable");

/** The key this extension's one stored blob travels under on the host's bulk data route. */
export const OPTIONS_KEY = "options";

/** Where the settings tab renders, which is a page address rather than a route. */
export const SETTINGS_PAGE_PATH = "/settings/whisparr-sync";

/** Where an inbound delivery carries its shared secret, in each position that is accepted. */
export const SECRET_HEADER = "X-Cove-Whisparr-Sync-Secret";
export const SECRET_QUERY_PARAMETER = "s";

/**
 * The agent each pinned build sends, which is what this product decides a version from.
 *
 * A spec sending another agent exercises a different branch, so these are the two that mean
 * anything. They are transcribed from the pinned images, and the connection spec is what asserts the
 * versions behind them.
 */
export const USER_AGENT = {
  v2: "Whisparr/2.2.0.231 (alpine 3.23.5)",
  v3: "Whisparr/3.6.2.1727 (alpine 3.23.6)",
};

/**
 * The source each version identifies an entity against.
 *
 * Transcribed by hand rather than read from the product: the library and this product have to agree
 * on them, and a value read from the product would agree with whatever the product says.
 */
export const STASHDB_ENDPOINT = "https://stashdb.org/graphql";
export const THEPORNDB_ENDPOINT = "https://theporndb.net/graphql";

/**
 * The floor this product clamps the backstop interval to, so a pass follows a restart without a
 * long wait. A stored value below it is read as it, and the worker wakes on it.
 *
 * The product's own floor is thirty seconds. That is a wake period paid per pass, and the backstop
 * specs wait through three or four, so this suite asks the extension for a shorter one through
 * `WHISPARRSYNC_BACKSTOP_FLOOR_SECONDS`. The variable is honoured downward only, so naming a larger
 * number here would silently leave the thirty standing rather than raise it.
 *
 * Transcribed from `WhisparrSyncOptions.ShortenedFloorSeconds`, and passed to the Cove container by
 * `startHarness`. Both readers take it from here, so the interval a spec stores and the floor the
 * container clamps to cannot disagree.
 */
export const BACKSTOP_INTERVAL_FLOOR_SECONDS = 2;

/** The variable the extension reads that floor from. */
export const BACKSTOP_FLOOR_VARIABLE = "WHISPARRSYNC_BACKSTOP_FLOOR_SECONDS";

/** The library root a seeded Whisparr entity is registered under, where a spec needs no volume. */
export const WHISPARR_ROOT = "/whisparr-media";

/** The Cove library root the compose file declares. */
export const COVE_ROOT = "/data";

const FIXTURES = join(
  import.meta.dirname,
  "..",
  "..",
  "src",
  "WhisparrSync.Tests",
  "TestSupport",
  "Fixtures",
);

/**
 * The delivery each pinned build really sent, captured beside the backend tests.
 *
 * A path rather than a body: a spec reads the recording and rewrites only what it must, so every
 * other member stays exactly what Whisparr delivers.
 */
export const CAPTURED_DELIVERY = {
  v2: join(FIXTURES, "whisparr-v2-2.2.0.231-webhook-import.json"),
  v3: join(FIXTURES, "whisparr-v3-3.3.8.1097-webhook-import.json"),
};

/**
 * How long an absence is watched for before it is reported as one.
 *
 * One value and one reasoning, two readers. A command the instance has not issued yet is
 * indistinguishable from one it will never issue, and a row a menu has not rendered yet is
 * indistinguishable from one it never will. An absence read immediately after the gesture that could
 * have produced it is bounded by whatever delay the run happened to have, which is not a window
 * anyone chose.
 */
export const SETTLE_DWELL_MS = 8_000;
