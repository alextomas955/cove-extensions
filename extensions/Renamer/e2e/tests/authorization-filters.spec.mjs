// The only spec that observes Cove's ROW-LEVEL authorization filters. Three host facts put it here
// rather than in a C# tier, and each one of them makes a cheaper version of this test vacuous.
//
// The filters install only under Npgsql: `CoveContext.OnModelCreating` configures them inside its
// provider branch, so a SQLite-backed test has no filters to observe at all. The suite's containers
// run auth off by default, so every request there resolves to a bypass principal whatever it carries
// — hence the auth-on instance below, which is instance-global and therefore per-test rather than
// worker-shared. And the trap: the filters short-circuit to true for a principal holding the
// wildcard permission, which Cove's bootstrap grants the owner role. An auth-on spec driven with
// `bootstrapOwner()`'s token proves nothing about row-level authorization, so this one is driven as a
// restricted user and its FIRST assertions are that the principal it drives is not the bypass one.
//
// WHY THE ROLE HOLDS A WRITE PERMISSION AND STILL SEES NOTHING. Cove's write permissions declare the
// matching read as implied, so `videos.write` expands to carry `videos.read`: the restricted user
// reaches every video read endpoint and gets 200. What empties the result is a content rule denying
// read on the kind — the permission stays, and the per-entity SQL predicate answers false. That is
// deliberately the interesting shape, because it is the one where a wrong principal is answered 200
// with zero rows instead of a 403 that names itself.
//
// `jobs.read` is in the set for one reason only: the host gates its own job-status endpoint on it,
// so without it the restricted user cannot poll the job it just enqueued (measured — the poll answers
// 403 naming that key). It grants no entity read of any kind and so cannot weaken anything below.
// Nothing else was needed; in particular no extensions permission, because an extension endpoint
// that declares no host authorization metadata stays reachable and does its own in-handler check.
import { test as base, expect } from "@playwright/test";
import { createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { seedVideo } from "@cove-extensions/e2e/seed-media";
import { RENAMER_EXTENSION } from "../lib/renamer-fixtures.mjs";

// Small and fixed. Every assertion below compares two live responses to each other, so nothing here
// depends on this number — it exists only so the library is provably non-empty for the owner, which
// is what makes the restricted user's empty result mean something.
const SEEDED_VIDEOS = 3;

const RESTRICTED_PERMISSIONS = ["videos.write", "jobs.read"];

const test = base.extend({
  authz: [
    async ({}, use) => {
      const harness = await startHarness({ env: { COVE_E2E_AUTH_ENABLED: "true" } });
      await harness.bootstrapOwner();
      // The install reads the id out of the manifest, the one place it is defined, and restarts the
      // container — which re-mints the owner token. Both the seeding and the restricted user below
      // therefore happen after it, never before.
      const { id: extensionId } = await harness.installExtension(RENAMER_EXTENSION);

      for (let i = 0; i < SEEDED_VIDEOS; i++) {
        await seedVideo({
          container: harness.container,
          baseUrl: harness.baseUrl,
          token: harness.token,
        });
      }

      const restricted = await harness.createRestrictedUser({
        permissions: RESTRICTED_PERMISSIONS,
        denyReadEntityKinds: ["video"],
      });

      await use({
        harness,
        extensionId,
        owner: createApiClient(() => harness.baseUrl, harness.token),
        restricted: createApiClient(() => harness.baseUrl, restricted.token),
      });
      await harness.stop();
    },
    { scope: "test" },
  ],
});

/** Reads a video page as whoever `api` carries, returning the host's own total for the request. */
async function readVideoTotal(api, who) {
  const res = await api.get(`/api/videos?perPage=${SEEDED_VIDEOS * 10}`);
  expect(res.status, `GET /api/videos as ${who} answered ${res.status}: ${res.text}`).toBe(200);
  expect(
    typeof res.json?.totalCount,
    `GET /api/videos as ${who} carried no numeric totalCount: ${res.text}`,
  ).toBe("number");
  return res.json.totalCount;
}

test("Cove's row-level filters bite for a restricted principal and not for the owner", async ({
  authz,
}) => {
  const { owner, restricted } = authz;

  // The licence for every other assertion in this file: prove the principal being driven is one the
  // filters apply to. Without this pair, a spec can report green while the host does nothing.
  const me = await restricted.get("/api/auth/me");
  expect(me.status, `GET /api/auth/me as the restricted user answered ${me.status}`).toBe(200);
  const permissions = me.json?.permissions;
  expect(Array.isArray(permissions), `/api/auth/me carried no permissions array: ${me.text}`).toBe(
    true,
  );
  expect(
    permissions,
    "the restricted principal holds the wildcard permission, which bypasses every query filter — nothing below would prove anything",
  ).not.toContain("*");

  // Held, not missing. A zero-row read below has to be the filter answering false, not the endpoint
  // refusing the caller: those are different defects and only one of them is silent.
  expect(
    permissions,
    "the restricted principal does not hold videos.read, so an empty video read would be a permission refusal rather than a filter decision",
  ).toContain("videos.read");

  // The deny rule contributes no allow, so the principal carries no read GRANT to fall back on.
  expect(
    me.json?.readGrantedEntityKinds ?? [],
    "the restricted role carries a read grant for videos, so the deny rule is not the only thing deciding",
  ).not.toContain("video");

  const ownerTotal = await readVideoTotal(owner, "the owner");
  const restrictedTotal = await readVideoTotal(restricted, "the restricted user");

  expect(
    ownerTotal,
    "the owner sees no videos, so the library is empty and a restricted count of zero would prove nothing",
  ).toBeGreaterThan(0);
  expect(
    restrictedTotal,
    "the restricted principal can read video rows — Cove's row-level authorization filters did not bite",
  ).toBe(0);
  expect(
    restrictedTotal,
    "the owner and the restricted user read the same count from the same library, so the filters are not discriminating between them",
  ).not.toBe(ownerTotal);
});
