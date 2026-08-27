// The only spec that observes Cove's ROW-LEVEL authorization filters. Three host facts put it here
// rather than in a C# tier, and each one of them makes a cheaper version of this test vacuous.
//
// The filters install only under Npgsql: `CoveContext.OnModelCreating` configures them inside its
// provider branch, so a SQLite-backed test has no filters to observe at all. The suite's containers
// run auth off by default, so every request there resolves to a bypass principal whatever it carries
// — hence the auth-on instance below, which is instance-global and therefore per-test rather than
// worker-shared.
//
// Which principal discriminates, and why this role holds a write permission and still sees nothing,
// are stated in full on the harness's `createRestrictedUser`. What follows from them here is that
// this spec's FIRST assertions are that the principal it drives is not the bypass one.
//
// `jobs.read` is in the set for one reason only: the host gates its own job-status endpoint on it,
// so without it the restricted user cannot poll the job it just enqueued (measured — the poll answers
// 403 naming that key). It grants no entity read of any kind and so cannot weaken anything below.
// Nothing else was needed; in particular no extensions permission, because an extension endpoint
// that declares no host authorization metadata stays reachable and does its own in-handler check.
import { test as base, expect, createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { seedVideo } from "@cove-extensions/e2e/seed-media";
import { RENAMER_EXTENSION } from "../lib/renamer-fixtures.mjs";
import { pollRenamerJob } from "../lib/poll-renamer-job.mjs";

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
        routeBase: `/api/extensions/${extensionId}`,
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

test("the whole-library endpoints refuse a caller without the permission, and answer the owner", async ({
  authz,
}) => {
  const { owner, harness, routeBase } = authz;

  // WHAT THIS DOES AND DOES NOT GATE, measured rather than assumed.
  //
  // `RequireCovePermission` is evaluated against the caller's PERMISSIONS: the host returns true as
  // soon as `principal.Has(permission)` does. It does not consult content rules. So it refuses a caller
  // that lacks the permission outright - which is what is asserted here - and it does NOT refuse one
  // that holds the permission while a role content rule denies it rows. The `restricted` fixture user
  // is the second kind, and it still gets 202 from scan-library; that is the host's model, not a gap in
  // this declaration.
  //
  // Refusing THAT caller would need the content-rule check Cove applies to its own routes with
  // [RequiresUnscopedEntityAccess], which is an MVC action filter and never reaches a minimal-API
  // endpoint. `IAuthorizationService` offers no unrestricted-access query either, so an extension could
  // only reach it by reading RoleContentRules itself - a host table outside the extension surface.
  const noPermission = await harness.createRestrictedUser({
    username: "e2e-nopermission",
    roleName: "e2e-nopermission",
    permissions: [],
  });
  const withoutPermission = createApiClient(() => harness.baseUrl, noPermission.token);

  const wholeLibrary = [
    { method: "post", path: "scan-library", body: {} },
    { method: "get", path: "last-scan" },
    { method: "post", path: "scan-rows", body: {} },
    { method: "post", path: "renamer-library", body: {} },
    { method: "post", path: "undo", body: {} },
  ];

  for (const call of wholeLibrary) {
    const refused =
      call.method === "get"
        ? await withoutPermission.get(`${routeBase}/${call.path}`)
        : await withoutPermission.post(`${routeBase}/${call.path}`, call.body);
    expect(
      refused.status,
      `${call.method.toUpperCase()} ${call.path} answered ${refused.status} to a caller holding no ` +
        `renamer permission at all: ${refused.text}`,
    ).toBe(403);
  }

  // Driven as the owner too: the refusals above would also pass against an endpoint broken for
  // everyone. `renamer-library` is left out - this is about the boundary, not about moving every file.
  const asOwner = await owner.post(`${routeBase}/scan-library`, {});
  expect(
    asOwner.status,
    `POST scan-library as the owner answered ${asOwner.status}: ${asOwner.text}`,
  ).toBe(202);
  const jobId = asOwner.json?.jobId;
  expect(typeof jobId, `the enqueue carried no jobId: ${asOwner.text}`).toBe("string");
  const job = await pollRenamerJob(owner, routeBase, jobId, { timeoutMs: 120_000 });
  expect(
    job?.status?.toLowerCase(),
    `the scan job did not complete (status ${job?.status}, error ${job?.error})`,
  ).toBe("completed");

  const summary = await owner.get(`${routeBase}/last-scan`);
  expect(
    summary.status,
    `GET last-scan as the owner answered ${summary.status}: ${summary.text}`,
  ).toBe(200);
});

test("a scoped account still reaches what is its own: its rules, and a job it started", async ({
  authz,
}) => {
  const { restricted, routeBase } = authz;

  // These endpoints disclose no library content - which of the caller's own rules are broken, the
  // configured library roots, the progress of a job it started - so demanding an unrestricted grant
  // for them would lock out a scoped account for nothing. They keep the authenticated-caller
  // declaration and their own in-handler check.
  const ownState = await restricted.get(`${routeBase}/orphaned-rules`);
  expect(
    ownState.status,
    `GET orphaned-rules as the restricted user answered ${ownState.status}: ${ownState.text}`,
  ).toBe(200);

  const roots = await restricted.get(`${routeBase}/library-paths`);
  expect(
    roots.status,
    `GET library-paths as the restricted user answered ${roots.status}: ${roots.text}`,
  ).toBe(200);

  // Job status, which is the reason the extension serves its own: Cove gates ITS job route on an
  // unrestricted grant, so without this route a scoped caller could start work it could never watch.
  //
  // Asked with an id no job answers to, deliberately. The two refusals are what separate the door from
  // the handler: 403 would mean the caller never got past authorization, while 404 is the handler's own
  // own-jobs check answering - so 404 proves reachability without needing a job to exist, and without a
  // conditional that could pass by skipping.
  const status = await restricted.get(`${routeBase}/job-status/no-such-job`);
  expect(
    status.status,
    `GET job-status as the restricted user answered ${status.status}; 403 would mean the door refused ` +
      `it, and a scoped account must be able to watch a run it started: ${status.text}`,
  ).toBe(404);
});
