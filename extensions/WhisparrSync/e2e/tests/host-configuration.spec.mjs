// Whisparr Sync's endpoints under real authentication: the extension installs into a live Cove, the
// host reports it enabled, and each route answers a permitted caller while refusing callers that are
// not. The read tier (the host-configuration probe) and the configure tier (the connection test) are
// both here because an auth-enabled instance is instance-global and costs a boot; splitting them
// across files would pay for it twice.
//
// Runs against an AUTH-ENABLED instance. Under this suite's auth-off default the host resolves every
// request to a bypass principal holding every permission, so each refusal asserted below would pass
// unchanged against an extension whose endpoint gated nothing.
//
// Serial, over a worker-scoped instance: turning authentication on is instance-global, so the tests
// share one boot rather than each paying for its own. A failed install then skips the probe tests
// instead of reporting each one separately against an instance that has no extension.
import { test as base, expect, createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const PROBE_PATH = `/api/extensions/${EXTENSION_ID}/host-configuration`;
const CONNECTION_TEST_PATH = `/api/extensions/${EXTENSION_ID}/connection/test`;
const SETTINGS_PATH = `/api/extensions/${EXTENSION_ID}/settings`;

// A port nothing inside the Cove container listens on. Well-formed, so it passes the address check
// and a request really is attempted, and refused at once rather than left to time out.
const DEAD_ADDRESS = "http://127.0.0.1:1";

// Synthetic, and authorises nothing: no instance is reached at the address above.
const SOME_KEY = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

const test = base.extend({
  authHarness: [
    async ({}, use) => {
      // The container pair exists from startHarness() onward, so every later step belongs inside the
      // try: a bootstrap or install failure would unwind past stop() and strand a Cove instance, a
      // Postgres instance and their compose network until Ryuk reaps them.
      const authHarness = await startHarness({ env: { COVE_E2E_AUTH_ENABLED: "true" } });
      try {
        authHarness.owner = await authHarness.bootstrapOwner();
        await authHarness.installExtension(WHISPARR_SYNC_EXTENSION);
        await use(authHarness);
      } finally {
        await authHarness.stop();
      }
    },
    { scope: "worker" },
  ],
});

test.describe.configure({ mode: "serial" });

// Read through the handle rather than captured: installExtension restarts the container, which
// re-mints the token and can republish the instance on a different host port.
const ownerClient = (authHarness) =>
  createApiClient(
    () => authHarness.baseUrl,
    () => authHarness.token,
  );

test("the extension installs into a live Cove and the host reports it enabled", async ({
  authHarness,
}) => {
  const listed = await ownerClient(authHarness).get("/api/extensions");

  expect(listed.status, `reading the installed extensions failed: ${listed.text}`).toBe(200);
  expect(
    listed.json?.find((extension) => extension.id === EXTENSION_ID)?.enabled,
    `the host does not report ${EXTENSION_ID} as enabled`,
  ).toBe(true);
});

test("the probe answers a permitted caller with exactly its two camelCase scalars", async ({
  authHarness,
}) => {
  const probe = await ownerClient(authHarness).get(PROBE_PATH);

  expect(probe.status, `GET ${PROBE_PATH} as the owner answered: ${probe.text}`).toBe(200);
  expect(Object.keys(probe.json ?? {}).sort()).toEqual([
    "configurationResolved",
    "libraryRootCount",
  ]);
  expect(typeof probe.json.configurationResolved).toBe("boolean");
  expect(Number.isInteger(probe.json.libraryRootCount)).toBe(true);
});

test("the probe refuses an unauthenticated caller and discloses neither field", async ({
  authHarness,
}) => {
  const anonymous = createApiClient(() => authHarness.baseUrl);

  // The discriminating control: without it a refused probe below could equally mean the instance
  // never enforced authentication in the first place.
  const anonymousRead = await anonymous.get("/api/extensions");
  expect(
    anonymousRead.status,
    "an anonymous read was not refused, so this instance is not enforcing authentication and nothing below is evidence",
  ).toBe(401);

  const probe = await anonymous.get(PROBE_PATH);
  expect(probe.ok, `GET ${PROBE_PATH} unauthenticated answered ${probe.status}`).toBe(false);
  expect(probe.text).not.toContain("configurationResolved");
  expect(probe.text).not.toContain("libraryRootCount");
});

test("the connection test answers a caller holding the configure permission", async ({
  authHarness,
}) => {
  const answered = await ownerClient(authHarness).post(CONNECTION_TEST_PATH, {
    address: DEAD_ADDRESS,
    apiKey: SOME_KEY,
  });

  // The positive control for the refusals below: without it a 403 could equally mean the route was
  // never mounted. Nothing listens at the address, so the honest answer is that nothing answered.
  expect(
    answered.status,
    `POST ${CONNECTION_TEST_PATH} as the owner answered: ${answered.text}`,
  ).toBe(200);
  expect(answered.json?.kind).toBe("unreachable");
  // The response is a classified KIND and named values, never what answered.
  expect(Object.keys(answered.json).sort()).toEqual([
    "address",
    "branch",
    "corroborated",
    "generation",
    "kind",
    "missingSetting",
    "otherApplication",
    "version",
  ]);
});

// Refusing an anonymous caller is the host's authentication layer, which answers before this
// extension's gate is consulted at all — so on its own it is no evidence that the gate exists. A
// caller who IS authenticated and holds nothing is the one that reaches the gate.
//
// The file is serial, and this test and the one after it each write to the shared instance (a role
// and a user). They take DIFFERENT names for both, so neither collides with the other's.
test("the probe refuses an authenticated caller holding no read permission", async ({
  authHarness,
}) => {
  const restricted = await authHarness.createRestrictedUser({
    username: "e2e-holds-nothing",
    roleName: "e2e-holds-nothing",
    permissions: [],
  });
  const asRestricted = createApiClient(() => authHarness.baseUrl, restricted.token);

  // The discriminating control: the same credential must be accepted somewhere, or a refusal below
  // would only mean the token was rejected.
  const accepted = await asRestricted.get("/api/auth/me");
  expect(accepted.status, `the restricted user's own token was refused: ${accepted.text}`).toBe(
    200,
  );

  const probe = await asRestricted.get(PROBE_PATH);
  expect(
    probe.status,
    `GET ${PROBE_PATH} as a caller holding nothing answered: ${probe.text}`,
  ).toBe(403);
  expect(probe.text).not.toContain("configurationResolved");
  expect(probe.text).not.toContain("libraryRootCount");
});

// The configure tier, checked on the same instance. A caller who is authenticated and holds the READ
// tier still must not reach this route: the two arrays are separate on purpose, and a handler reading
// the wrong one would pass every test that only ever presents a caller holding nothing.
test("the connection test refuses an authenticated caller holding only the read tier", async ({
  authHarness,
}) => {
  const readOnly = await authHarness.createRestrictedUser({
    username: "e2e-read-tier",
    roleName: "e2e-read-tier",
    permissions: ["videos.read"],
  });
  const asReadOnly = createApiClient(() => authHarness.baseUrl, readOnly.token);

  // The discriminating control: the same credential reaches the READ-tier route, so a refusal below
  // is about the gate rather than about the token.
  const probe = await asReadOnly.get(PROBE_PATH);
  expect(probe.status, `GET ${PROBE_PATH} as a read-tier caller answered: ${probe.text}`).toBe(200);

  const refused = await asReadOnly.post(CONNECTION_TEST_PATH, {
    address: DEAD_ADDRESS,
    apiKey: SOME_KEY,
  });
  expect(
    refused.status,
    `POST ${CONNECTION_TEST_PATH} as a read-tier caller answered: ${refused.text}`,
  ).toBe(403);
  expect(refused.json?.code).toBe("FORBIDDEN");
  expect(refused.text).not.toContain("kind");
});

// The settings routes carry the same gate, on the same instance and with the same control. They are
// asserted here rather than beside the write-only spec because that one runs with authentication off,
// where the host resolves every request to a bypass principal holding every permission — a refusal
// asserted there would pass against an extension that gated nothing.
test("the settings routes refuse an authenticated caller holding only the read tier", async ({
  authHarness,
}) => {
  const readOnly = await authHarness.createRestrictedUser({
    username: "e2e-settings-read-tier",
    roleName: "e2e-settings-read-tier",
    permissions: ["videos.read"],
  });
  const asReadOnly = createApiClient(() => authHarness.baseUrl, readOnly.token);

  // The discriminating control: the same credential reaches the READ-tier route, so the refusals
  // below are about the gate rather than about the token.
  const probe = await asReadOnly.get(PROBE_PATH);
  expect(probe.status, `GET ${PROBE_PATH} as a read-tier caller answered: ${probe.text}`).toBe(200);

  const read = await asReadOnly.get(SETTINGS_PATH);
  expect(read.status, `GET ${SETTINGS_PATH} as a read-tier caller answered: ${read.text}`).toBe(
    403,
  );
  expect(read.json?.code).toBe("FORBIDDEN");
  expect(read.text).not.toContain("keyIsSet");

  const write = await asReadOnly.put(SETTINGS_PATH, {
    selectedGeneration: "v3",
    v3: { address: DEAD_ADDRESS, keyWrite: "replace", apiKey: SOME_KEY },
    v2: null,
  });
  expect(write.status, `PUT ${SETTINGS_PATH} as a read-tier caller answered: ${write.text}`).toBe(
    403,
  );
  expect(write.json?.code).toBe("FORBIDDEN");

  // The refused write changed nothing: the owner, who may read, still sees no key stored.
  const owner = await ownerClient(authHarness).get(SETTINGS_PATH);
  expect(owner.status, `GET ${SETTINGS_PATH} as the owner answered: ${owner.text}`).toBe(200);
  expect(owner.json?.v3?.keyIsSet, "the refused write stored a key anyway").toBe(false);
});
