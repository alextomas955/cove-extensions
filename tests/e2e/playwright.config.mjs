import { defineConfig, devices } from "@playwright/test";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { checkRelativePath, readJson } from "../../scripts/repo-files.mjs";

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(__dirname, "..", "..");

// A suite is registered in the catalog, not here. There is exactly one shared @playwright/test
// install for the whole monorepo: a second install breaks Playwright's module singleton the moment
// a test file imports a fixture module across the two, so an extension gets a project here rather
// than an install of its own (see tests/e2e/README.md).
//
// This harness's own `tests/` directory is deliberately not a catalog entry. `template.spec.mjs`
// there resolves extension paths self-relatively, so it addresses a real extension only once it has
// been copied into one.
const catalog = readJson(join(repoRoot, "extensions", "catalog.json"));
const catalogEntries = Array.isArray(catalog.extensions) ? catalog.extensions : [];
const e2eProjects = catalogEntries
  .filter((entry) => entry.e2ePath && entry.e2eProject)
  .map((entry) => {
    // Validated before the join: an absolute or `..`-bearing value relocates testDir outside the
    // checkout, where the run collects either specs nobody reviewed or nothing at all - and
    // collecting nothing still passes the guard below, because the entry itself resolved.
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
  // Safe because every test's data is isolated: a test on the worker-shared harness seeds its own
  // uniquely-named data, and a file that mutates shared extension state (install, enable, disable,
  // uninstall) takes an instance of its own through `isolatedHarnessFixtures`.
  fullyParallel: true,
  // A committed `.only` silently shrinks the suite to the focused test and still exits 0, which reads
  // as a green run over work nothing checked. Keyed on CI so a local focused run stays possible.
  forbidOnly: !!process.env.CI,
  // A worker holds a Cove container and a database of its own, and a spec taking an isolated
  // harness starts a second pair alongside its worker's, so the peak is twice the worker count. The
  // database keeps its data directory on tmpfs, so that peak is paid in RAM rather than on disk.
  //
  // A shared runner has to hold the build as well, and when it runs short the kernel kills a
  // container rather than failing a test. That arrives as a spec timing out against a host serving
  // nothing, which reads as a UI defect.
  workers: process.env.CI ? 2 : 6,
  retries: process.env.CI ? 2 : 0,

  // Retries exist so an infrastructure hiccup does not fail a run, not so a flaky test can hide
  // behind one. The retry still runs, so the report distinguishes flaky from broken.
  failOnFlakyTests: true,
  timeout: 180_000,

  // Playwright's own default here is 5s, which is shorter than a cold container takes to fetch and
  // render one of the host's lazily-imported route chunks: an assertion following a navigation to a
  // route this run has not visited loses on the first attempt and wins on the retry. The per-test
  // budget stays the ceiling.
  expect: { timeout: 20_000 },
  reporter: process.env.CI ? [["list"], ["github"], ["html", { open: "never" }]] : [["list"]],
  use: {
    trace: process.env.CI ? "on-first-retry" : "retain-on-failure",
    screenshot: "only-on-failure",
    ...devices["Desktop Chrome"],
  },
  globalSetup: "./lib/global-setup.mjs",
  projects: e2eProjects,
});
