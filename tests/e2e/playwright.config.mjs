import { defineConfig, devices } from "@playwright/test";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { checkRelativePath } from "../../scripts/catalog-paths.mjs";

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(__dirname, "..", "..");

// The catalog is this monorepo's single declaration of which extensions exist, so a suite is
// registered by declaring it there rather than here, where the same fact would then live twice and be
// free to disagree with itself. Two things the derivation below cannot show. There is exactly one
// shared @playwright/test install for the whole monorepo: a second install breaks Playwright's
// module singleton the moment a test file imports a fixture module across the two, so an extension
// gets a project here rather than an install of its own (see tests/e2e/README.md). And this
// harness's own `tests/` directory is deliberately not a catalog entry — `template.spec.mjs` there
// is a copyable starting point rather than a live suite, and it resolves extension paths
// self-relatively, so it addresses a real extension only once it has been copied into one.
const catalog = JSON.parse(
  readFileSync(join(repoRoot, "extensions", "catalog.json"), "utf8").replace(/^\uFEFF/, ""),
);
const catalogEntries = Array.isArray(catalog.extensions) ? catalog.extensions : [];
const e2eProjects = catalogEntries
  .filter((entry) => entry.e2ePath && entry.e2eProject)
  .map((entry) => {
    // Validated before the join, not after: an absolute or `..`-bearing value silently relocates
    // testDir outside the checkout, where the run either collects specs nobody reviewed or collects
    // nothing at all — and collecting nothing still passes the guard below, because the entry itself
    // resolved. Same contract, same check, as every other reader of a catalog path field.
    const reason = checkRelativePath(`catalog entry ${entry.e2eProject}'s e2ePath`, entry.e2ePath);
    if (reason) throw new Error(`Refusing to derive a Playwright project: ${reason}`);
    return { name: entry.e2eProject, testDir: join(repoRoot, entry.e2ePath, "tests") };
  });

// A runner configured with no projects collects nothing and still exits 0, so a mis-shaped catalog
// would read as a green E2E tier that inspected nothing.
if (e2eProjects.length === 0) {
  throw new Error(
    `No E2E project resolved: examined ${catalogEntries.length} catalog entries, of which 0 declared both e2ePath and e2eProject.`,
  );
}

export default defineConfig({
  // Safe because every test's data is isolated: worker-shared-harness test files (the default —
  // see fixtures.mjs) seed their own uniquely-named data per test (timestamp + random suffix), so
  // concurrent tests in different workers never collide even though a worker's own tests run one
  // at a time against ITS instance. Files that mutate shared extension state itself (install/
  // enable/disable/uninstall — see extension-lifecycle.spec.mjs) opt out of the shared harness
  // entirely via their own `scope: 'test'` fixture, so parallel workers never race on those
  // mutations either.
  //
  // Workers capped, not left at Playwright's CPU-based default: each worker brings up its own
  // Docker Compose network (one per Cove+Postgres pair) plus a Chromium instance. Locally, 4 is
  // comfortably within Docker Desktop's default address-pool on a typical dev machine — confirmed
  // directly (a 13-worker run failed 3 tests with "all predefined address pools have been fully
  // subnetted" on a machine that already had several unrelated projects' networks allocated). In
  // CI, each worker's fixed cost (a full Compose stack + a real browser, not just a browser context
  // against one shared server) is high relative to a standard GitHub-hosted runner's 4 vCPU/16GB —
  // running all 4 concurrently there oversubscribes the runner, so CI gets fewer, not the same
  // count as local. Override with `--workers=N` if a given machine/runner can sustain more (or
  // fewer) than its default.
  fullyParallel: true,
  // A committed `.only` silently shrinks the suite to the focused test and still exits 0, which reads
  // as a green run over work nothing checked. Keyed on CI so a local focused run stays possible.
  forbidOnly: !!process.env.CI,
  workers: process.env.CI ? 2 : 4,
  retries: process.env.CI ? 2 : 0,

  // Retries exist so an infrastructure hiccup does not fail a run, not so a flaky test can hide behind
  // one: without this a rescued flake leaves the job green and its evidence discarded. The retry still
  // runs, so the report distinguishes flaky from broken.
  failOnFlakyTests: true,
  timeout: 180_000,
  reporter: [["list"]],
  use: {
    trace: process.env.CI ? "on-first-retry" : "retain-on-failure",
    screenshot: "only-on-failure",
    ...devices["Desktop Chrome"],
  },
  projects: e2eProjects,
});
