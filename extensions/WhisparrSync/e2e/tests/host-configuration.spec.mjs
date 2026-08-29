// Whisparr Sync's first end-to-end path: the extension installs into a live Cove, the host reports
// it enabled, and its one read-gated probe answers a permitted caller while refusing callers that
// are not.
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

// Refusing an anonymous caller is the host's authentication layer, which answers before this
// extension's gate is consulted at all — so on its own it is no evidence that the gate exists. A
// caller who IS authenticated and holds nothing is the one that reaches the gate.
//
// Last in the file, and the file is serial: this is the only test here that writes to the shared
// instance (a role and a user), so nothing after it has to know they appeared.
test("the probe refuses an authenticated caller holding no read permission", async ({
  authHarness,
}) => {
  const restricted = await authHarness.createRestrictedUser({ permissions: [] });
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
