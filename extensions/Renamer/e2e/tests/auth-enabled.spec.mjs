// Covers the harness against an instance that actually enforces authentication.
//
// Every other spec in this suite runs with `COVE__Auth__Enabled=false`, where the host resolves each
// request to a bypass principal holding every permission. That leaves them blind to one class of
// harness defect: a bootstrap or poll that reaches a permission-gated route without a credential
// still succeeds there, and fails only once a deployment turns authentication on.
//
// Turning it on is instance-global, so this file provisions its own instance per test rather than
// sharing the worker's.
import { test as base, expect, createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { RENAMER_EXTENSION } from "../lib/renamer-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";

const test = base.extend({
  authHarness: [
    async ({}, use) => {
      const authHarness = await startHarness({ env: { COVE_E2E_AUTH_ENABLED: "true" } });
      try {
        await use(authHarness);
      } finally {
        await authHarness.stop();
      }
    },
    { scope: "test" },
  ],
});

test("an auth-enabled instance can be bootstrapped and then driven with the owner's token", async ({
  authHarness,
}) => {
  // The bootstrap itself is the subject. On an auth-enabled instance there is no credential yet and
  // no owner to mint one from, so any step inside it that consults a permission-gated route cannot
  // succeed.
  authHarness.owner = await authHarness.bootstrapOwner();
  expect(authHarness.token, "bootstrapOwner returned without minting a token").toBeTruthy();

  const anonymous = createApiClient(() => authHarness.baseUrl);
  const asOwner = createApiClient(
    () => authHarness.baseUrl,
    () => authHarness.token,
  );

  // The discriminating control: without it, everything below would pass unchanged against the
  // auth-off default, where every route answers a bypass principal.
  const anonymousRead = await anonymous.get("/api/extensions");
  expect(
    anonymousRead.status,
    "an anonymous read was not refused, so this instance is not enforcing authentication and nothing below is evidence",
  ).toBe(401);

  const ownerRead = await asOwner.get("/api/extensions");
  expect(ownerRead.status, `the owner's own token was refused: ${ownerRead.text}`).toBe(200);
});

test("an extension installs into an auth-enabled instance and survives the restart", async ({
  authHarness,
}) => {
  authHarness.owner = await authHarness.bootstrapOwner();

  // installExtension restarts the container, which invalidates the token minted above and can
  // republish the instance on a different host port. Both are re-established by the harness, so a
  // client that reads them through the handle keeps working and one that captured either does not.
  const installed = await authHarness.installExtension(RENAMER_EXTENSION);
  expect(installed.id).toBe(EXTENSION_ID);

  const asOwner = createApiClient(
    () => authHarness.baseUrl,
    () => authHarness.token,
  );
  const listed = await asOwner.get("/api/extensions");
  expect(listed.status, `reading extensions after the restart failed: ${listed.text}`).toBe(200);
  expect(
    listed.json?.find((extension) => extension.id === EXTENSION_ID)?.enabled,
    "the extension is not enabled on the restarted auth-enabled instance",
  ).toBe(true);
});
