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

test("the elevated detached read sees the library its own caller cannot", async ({ authz }) => {
  const { owner, restricted, routeBase } = authz;

  // The denominator, read live from the host as somebody the filters do not apply to. Nothing below
  // compares against a literal count.
  const ownerTotal = await readVideoTotal(owner, "the owner");
  expect(
    ownerTotal,
    "the seeded library is empty, so neither figure below can mean anything",
  ).toBeGreaterThan(0);

  // Everything from here is driven as the restricted user — the same principal that just read zero
  // videos in the test above.
  const enqueued = await restricted.post(`${routeBase}/scan-library`, {});
  expect(
    enqueued.status,
    `POST ${routeBase}/scan-library as the restricted user answered ${enqueued.status}: ${enqueued.text}`,
  ).toBe(202);
  const jobId = enqueued.json?.jobId;
  expect(typeof jobId, `the enqueue carried no jobId: ${enqueued.text}`).toBe("string");

  const job = await pollRenamerJob(restricted, routeBase, jobId, { timeoutMs: 120_000 });
  expect(
    job?.status?.toLowerCase(),
    `the scan job did not complete (status ${job?.status}, error ${job?.error})`,
  ).toBe("completed");

  // The job body runs detached and elevates, so it plans the whole library.
  //
  // WHAT THIS HALF DOES AND DOES NOT GATE, measured rather than reasoned. Cove dispatches an
  // exclusive job from a queue processor its job service starts at host startup, and the principal
  // lives in an AsyncLocal — so a queued body has NO ambient principal, and a missing principal
  // bypasses the filters exactly as the wildcard does. Removing the elevation from this job body was
  // therefore observed to leave the figure below unchanged: the assertion is not a gate on that one
  // line. What it does pin is the PAIR — a detached body that reaches the library beside a request
  // path that does not — so it goes red if the host ever begins propagating a caller's principal into
  // a queued body while the elevation is absent. The elevation still earns its place for every
  // dispatch that DOES propagate one (a non-exclusive job, an inline event handler); that case is
  // asserted at the seam, not here.
  const summary = await restricted.get(`${routeBase}/last-scan`);
  expect(
    summary.status,
    `GET ${routeBase}/last-scan as the restricted user answered ${summary.status}: ${summary.text}`,
  ).toBe(200);
  const elevatedEntities = summary.json?.totalEntities;
  expect(
    typeof elevatedEntities,
    `the scan aggregate carried no numeric totalEntities: ${summary.text}`,
  ).toBe("number");

  // The request path, by contrast, deliberately stays on the caller's principal. Renamer's own source
  // states the invariant both ways: every detached body elevates, and the two request-path scopes
  // (/undo and /scan-rows) do NOT, because elevating them would hand a caller rows their principal is
  // not allowed to see. So ZERO HERE IS THE CORRECT ANSWER and must never be "fixed" — this assertion
  // is what makes a future change that elevates this path fail a test instead of silently widening
  // every caller's reach.
  const rows = await restricted.post(`${routeBase}/scan-rows`, { take: SEEDED_VIDEOS * 10 });
  expect(
    rows.status,
    `POST ${routeBase}/scan-rows as the restricted user answered ${rows.status}: ${rows.text}`,
  ).toBe(200);
  expect(
    rows.json?.rows?.length,
    "the non-elevated request path returned rows to a principal whose own video read is denied — it is running elevated",
  ).toBe(0);
  expect(
    rows.json?.entitiesExamined,
    "the non-elevated request path examined entities its caller cannot read",
  ).toBe(0);

  // The proof, in one comparison on one library for one user: the detached body reports what the
  // owner reports, and the same caller's own request path reports nothing. Both figures come from
  // live responses and are compared to each other, never to a number this file supplied.
  expect(
    elevatedEntities,
    "the elevated scan did not see the whole library — the detached body is running on the caller's principal",
  ).toBe(ownerTotal);
  expect(
    elevatedEntities,
    "the elevated and non-elevated paths agree, so this test is not observing the difference it exists for",
  ).not.toBe(rows.json?.rows?.length);
});

test("a scoped user watches its own run, and cannot read another owner's job through it", async ({
  authz,
}) => {
  const { restricted, routeBase } = authz;

  // Why this route exists: from Cove 1.3.1 the host gates its own GET /api/jobs/{id} on unrestricted
  // read, so this user is refused there for a run it started itself and the panel would poll forever.
  // The host's answer is deliberately NOT asserted here - it differs by host version, and this suite
  // runs against several, so pinning it would make the spec fail on the older ones for a reason that
  // is not Renamer's.
  const enqueued = await restricted.post(`${routeBase}/scan-library`, {});
  expect(
    enqueued.status,
    `POST ${routeBase}/scan-library as the restricted user answered ${enqueued.status}: ${enqueued.text}`,
  ).toBe(202);
  const jobId = enqueued.json?.jobId;

  const job = await pollRenamerJob(restricted, routeBase, jobId, { timeoutMs: 120_000 });
  expect(job?.status?.toLowerCase(), `the run did not finish: ${JSON.stringify(job)}`).toBe(
    "completed",
  );

  // Confinement - that the route reports only jobs this extension enqueued - is asserted in
  // JobStatusEndpointTests, where a foreign job type can be arranged exactly. Reaching it from here
  // would need a second extension's job id, and a made-up id is absent either way.
});
