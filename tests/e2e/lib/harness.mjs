// Core harness lifecycle: bring up an isolated Cove instance, wait for it to be ready, install an
// extension into it, and tear it down. This is the one entry point extension authors need —
// everything else (compose file, install mechanics, staging) is an implementation detail behind it.
//
// Built on Testcontainers (https://node.testcontainers.org/), not a hand-rolled `docker compose`
// child_process wrapper. Testcontainers' Ryuk sidecar guarantees container/network/volume cleanup
// even if the test process is killed (not just on a graceful exit) — a hand-rolled wrapper only
// cleans up in the success path, leaking containers on a killed run. It also owns port resolution
// and health-check waiting, removing two hand-written polling loops this file used to have.
import { join } from "node:path";
import { DockerComposeEnvironment, Wait } from "testcontainers";
import { installViaContainerCopy } from "./install-extension.mjs";
// The repository the image lives in and the floor each extension declares both already have exactly
// one reader, and a second parse of either here would be free to disagree with the one CI resolves
// against.
import {
  compareSemver,
  parseSemver,
  readCoveImageReference,
  readExtensionFloors,
} from "../../../scripts/fetch-cove-assemblies.mjs";

// import.meta.dirname, never a filesystem path read off a module URL's path component: on Windows
// that yields a leading-slash form which resolves to a doubled drive prefix.
const COMPOSE_DIR = join(import.meta.dirname, "..", "docker");
const COMPOSE_FILE = "docker-compose.yml";

// Shared-runner container cold-start is measurably slower than a dedicated dev machine's Docker
// Desktop — widen the default startup budget in CI rather than tuning it tight against local timing.
const DEFAULT_STARTUP_TIMEOUT_MS = process.env.CI ? 240_000 : 180_000;

/**
 * The Cove image an instance boots, resolved from the most specific input available.
 *
 * <remarks>
 * In order: a reference the caller states outright (a locally built host, say); a complete reference in
 * `COVE_E2E_IMAGE`; a version in `COVE_E2E_TAG`, placed on the repository the build properties declare;
 * and failing all three the highest floor the catalog's extensions declare in their own manifests. The
 * compose file holds no default of its own, so this is the only thing that decides.
 *
 * The tag-only form is what a CI leg has to give: a version leg resolves a VERSION, and the repository
 * is declared once in the build properties — which is why the fetcher and the build's version guard
 * take a tag too, rather than each being handed a whole reference to get wrong.
 *
 * Every path is derived, never written down. A version literal here would be a second declaration of a
 * number that already lives in a manifest, free to fall behind it — and a host BELOW an extension's
 * floor does not error: its version gate silently refuses to LOAD the extension, so the routes 404 and
 * every browser spec fails against a Settings page that never gains the extension's tab. The floor
 * taken is the HIGHEST declared, because one instance serves whichever extensions a run installs into
 * it and the lowest would boot a host beneath somebody's floor. Nothing here names an extension: a
 * second one arrives through the catalog and needs no edit in this file.
 *
 * Throws rather than falling back when a floor cannot be read or is not strict semver. An image nobody
 * chose is how a suite passes against the wrong host.
 * </remarks>
 * @param {string} [image] - an explicit complete reference, which wins over both environment forms.
 * @returns {string} a complete image reference, e.g. `ghcr.io/yourcove/cove-app:1.1.0`.
 */
export function resolveCoveImage(image) {
  if (image) return image;
  if (process.env.COVE_E2E_IMAGE) return process.env.COVE_E2E_IMAGE;
  // registry AND repository, never the `repository` field alone: that one is the host-less path, and a
  // reference missing its registry host resolves to Docker Hub — a real image, from a registry nobody
  // named.
  const { registry, repository } = readCoveImageReference();
  return `${registry}/${repository}:${process.env.COVE_E2E_TAG || highestDeclaredFloor()}`;
}

/** The highest `minCoveVersion` any catalog entry declares, reached through that entry's own manifest. */
function highestDeclaredFloor() {
  let highest = null;
  for (const { entry, floor, manifestPath } of readExtensionFloors()) {
    const parsed = parseSemver(floor);
    if (parsed === null) {
      throw new Error(
        `${manifestPath} declares minCoveVersion '${floor}' for '${entry.id ?? entry.name}', which is not a strict X.Y.Z semver version, so no Cove image can be resolved from it.`,
      );
    }
    if (highest === null || compareSemver(parsed, highest) > 0) highest = parsed;
  }
  return highest.tag;
}

/**
 * Brings up an isolated Cove instance and returns a handle with baseUrl + install/teardown methods.
 * Every instance gets a random project name (Testcontainers) and a random host port so parallel
 * test runs never collide.
 *
 * `env` is passed to the compose invocation, so it reaches any `${VAR:-default}` substitution in
 * docker-compose.yml — e.g. `{ COVE_E2E_AUTH_ENABLED: 'true' }` for an instance that must enforce
 * real authentication.
 *
 * `image` is a complete reference and overrides every other source; see `resolveCoveImage` for what
 * decides when it is absent.
 */
export async function startHarness({ image, env, timeoutMs = DEFAULT_STARTUP_TIMEOUT_MS } = {}) {
  let environment = new DockerComposeEnvironment(COMPOSE_DIR, COMPOSE_FILE)
    .withStartupTimeout(timeoutMs)
    // Keyed on CONTAINER names (`<service>-<index>`, the same names getContainer takes below), not
    // service names: Testcontainers drops a key that matches no container with only a log warning,
    // leaving whatever it infers from the image in force. It infers a health-check strategy here
    // anyway, so stating it is what makes that a decision rather than a coincidence — and the
    // strategy chosen now is also the one restart() reuses, where the difference is load-bearing.
    .withWaitStrategy("cove-1", Wait.forHealthCheck())
    .withWaitStrategy("db-1", Wait.forHealthCheck());

  const composeEnv = { COVE_E2E_IMAGE: resolveCoveImage(image), ...env };
  environment = environment.withEnvironment(composeEnv);

  const started = await environment.up();
  let coveContainer = started.getContainer("cove-1");
  // Resolved eagerly, like the Cove container above: a service name that no longer matches fails
  // here, at startup, rather than part-way through whatever assertion first reached for it.
  const dbContainer = started.getContainer("db-1");

  // Remembered by bootstrapOwner so the handle can re-authenticate itself after a restart without
  // the caller having to hold on to the credentials.
  let credentials = null;

  const handle = {
    /**
     * The bootstrapped owner's bearer token, set by `bootstrapOwner()`. Undefined until then, which
     * is why every consumer applies it as a conditional header rather than an unconditional one.
     */
    token: undefined,

    get baseUrl() {
      return `http://${coveContainer.getHost()}:${coveContainer.getMappedPort(5073)}`;
    },
    get containerId() {
      return coveContainer.getId();
    },
    /** The raw Testcontainers StartedGenericContainer, for helpers (e.g. seedVideo) that need copyFilesToContainer/exec directly. */
    get container() {
      return coveContainer;
    },

    async installExtension({ repoRoot, publishDir, manifestPath }) {
      const result = await installViaContainerCopy({
        container: coveContainer,
        repoRoot,
        publishDir,
        manifestPath,
      });
      await handle.restart();
      await waitForExtensionEnabled(handle.baseUrl, result.id, { timeoutMs, token: handle.token });
      return result;
    },

    /**
     * Restarts the Cove container and returns once the host can reach it again with a usable token.
     *
     * `installExtension` needs this because a copied-in extension is only discovered on a (re)start,
     * and a test needs it to reach anything an extension does at INITIALIZE time — a one-time
     * startup conversion of stored settings, say, whose precondition has to be written into the
     * running instance and the host then started over on top of it. There is no other way in: an
     * initialize-time code path does not run again while the host stays up.
     *
     * `baseUrl` MAY CHANGE across this call. A container published on an ephemeral host port can be
     * reassigned a new one on restart, so a caller holding a previously-read `baseUrl` — or anything
     * built from one — must re-read it after this resolves. `restart()` refreshes the same
     * StartedGenericContainer's port-binding state in place, so the getter above is correct
     * immediately afterwards.
     *
     * Returning does NOT mean an extension has finished initializing: this waits on the host being
     * reachable, which is a weaker condition. A caller that depends on initialize-time work having
     * landed must wait for that work's own observable outcome.
     */
    async restart() {
      await coveContainer.restart();
      await waitForHostReachable(handle.baseUrl, { timeoutMs });
      // An access token does NOT survive the restart: measured against 1.1.0, a token that answered
      // 200 before it answers 401 after, while a fresh login on the restarted instance answers 200.
      // So the token is re-minted here, or every later call in an auth-enabled instance fails as an
      // authentication error.
      if (handle.token) {
        await handle.login();
      }
    },

    /**
     * Runs a command inside the Cove container (e.g. to inspect /data2 for the cross-device test).
     *
     * Takes Testcontainers' own exec options alongside the argv, for the same reason `execDb` below
     * does: passing a value through `env` is what lets a command carry quotes with no escaping rule
     * to get wrong, and `user` is what reaches a path the container's own user may not. Dropping the
     * options does not fail loudly — the command still runs and still exits 0, just without what the
     * caller meant to supply — so the caller reads a successful run of a command that did nothing.
     */
    exec(command, opts) {
      return coveContainer.exec(command, opts);
    },

    /**
     * Runs a command inside the DATABASE container — the one way to ask the database itself whether
     * the host really created an extension's tables, rather than inferring it from the extension
     * having loaded. Nothing behavioural needs this: a failed extension migration is a host log line
     * and the load continues, so an extension can be enabled with no table behind it.
     *
     * Takes an argv array plus Testcontainers' own exec options, and `opts` is not optional dressing:
     * passing the statement through `env` is what lets it carry quotes with no escaping rule to get
     * wrong, and reading the credentials from the container's own environment
     * (`sh -c 'psql -U "$POSTGRES_USER" …'`) keeps the compose file the one place they are written.
     * Dropping `opts` here does not fail loudly — psql exits 0 on an empty statement — so a caller
     * would see a successful query that returned nothing.
     */
    execDb(command, opts) {
      return dbContainer.exec(command, opts);
    },

    /**
     * Creates the first (owner) account and returns its access token. REQUIRED before any
     * browser-driven test: Cove's frontend (App.tsx's `showSetupWizard`) hard-gates the ENTIRE
     * app behind a first-run setup wizard whenever no owner account exists, with no way to
     * dismiss it — confirmed directly (a "Skip setup for now" click does nothing while
     * `ownerMissing` is true). This is unrelated to `COVE__Auth__Enabled=false`: the auth-bypass
     * principal used for API calls exists independently, but the UI itself still checks
     * `GET /api/auth/bootstrap-status`'s `ownerExists` field and refuses to render past the
     * wizard until an owner is created via `POST /api/auth/bootstrap-owner`. Every extension's
     * browser-driven E2E test needs this, so it lives here rather than being copy-pasted per test.
     */
    async bootstrapOwner({ username = "e2e-owner", password = "E2eTestPassword123!" } = {}) {
      const res = await fetch(`${handle.baseUrl}/api/auth/bootstrap-owner`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username, password }),
      });
      if (!res.ok) {
        const body = await res.text().catch(() => "<unreadable body>");
        throw new Error(
          `bootstrapOwner: POST /api/auth/bootstrap-owner failed (${res.status}): ${body}`,
        );
      }
      credentials = { username, password };
      const payload = await res.json();
      handle.token = readToken(payload, "bootstrapOwner");
      return payload;
    },

    /**
     * Signs the stored owner credentials in again and replaces `token`. Needed after a container
     * restart, which invalidates every token minted before it.
     */
    async login({ username, password } = credentials ?? {}) {
      if (!username || !password) {
        throw new Error("login: no credentials — call bootstrapOwner() first, or pass them here");
      }
      const res = await fetch(`${handle.baseUrl}/api/auth/login`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username, password }),
      });
      if (!res.ok) {
        const body = await res.text().catch(() => "<unreadable body>");
        throw new Error(`login: POST /api/auth/login failed (${res.status}): ${body}`);
      }
      credentials = { username, password };
      const payload = await res.json();
      handle.token = readToken(payload, "login");
      return payload;
    },

    /**
     * Creates a NON-OWNER user Cove's row-level authorization filters actually apply to, and returns
     * its token WITHOUT replacing the handle's own.
     *
     * Why this exists at all: `CoveContext` short-circuits every one of those filters to true for a
     * principal holding the `"*"` permission, and Cove's bootstrap grants exactly that to the owner
     * role. So a spec driven with `bootstrapOwner()`'s token cannot observe row-level authorization —
     * every assertion it makes about which rows a principal sees passes whatever the filters do. The
     * same clause treats a MISSING principal as bypassed too, so "send no credential" proves the safe
     * case rather than the dangerous one. What discriminates is a present, under-privileged user.
     *
     * Why a permission list is not enough on its own, and the deny rule is what does the work: Cove's
     * write permissions declare the matching read as implied, so a role granted `videos.write` is
     * expanded to hold `videos.read` and reaches every video read endpoint. A CONTENT RULE denying
     * read on a kind is the mechanism that leaves the permission in place while making the per-entity
     * SQL predicate answer false — which is the shape worth testing, because it is the one where a
     * caller gets 200 and zero rows rather than a 403 that names itself.
     *
     * The handle's `token` deliberately stays the owner's: the caller still needs it to seed the
     * fixture and to read the same data back as somebody the filters do not apply to, which is the
     * comparison that gives a zero-row assertion any meaning.
     *
     * @param {object} opts
     * @param {string[]} opts.permissions - Host permission keys granted to the role, verbatim; this
     *   helper never adds to them.
     * @param {string[]} opts.denyReadEntityKinds - Cove entity kinds (its own lowercase vocabulary,
     *   e.g. `video`) to deny read on for the whole role.
     * @returns {Promise<{token: string, userId: number, roleId: number, roleName: string,
     *   username: string, password: string}>}
     */
    async createRestrictedUser({
      username = "e2e-restricted",
      password = "E2eRestrictedPassword123!",
      roleName = "e2e-restricted",
      permissions = [],
      denyReadEntityKinds = [],
    } = {}) {
      // Every call below is made as the OWNER: creating a role, a content rule and a user require
      // RolesWrite/UsersWrite, which at this point only the bootstrapped owner holds.
      const asOwner = async (path, body) => {
        const res = await fetch(`${handle.baseUrl}${path}`, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            ...(handle.token ? { Authorization: `Bearer ${handle.token}` } : {}),
          },
          body: JSON.stringify(body),
        });
        const text = await res.text().catch(() => "<unreadable body>");
        if (!res.ok) {
          throw new Error(
            `createRestrictedUser: POST ${path} failed (${res.status}): ${text || "<empty body>"}`,
          );
        }
        try {
          return text ? JSON.parse(text) : undefined;
        } catch {
          throw new Error(
            `createRestrictedUser: POST ${path} answered ${res.status} with a body that is not JSON: ${text}`,
          );
        }
      };

      const role = await asOwner("/api/roles", {
        Name: roleName,
        Description: "Restricted e2e role — no wildcard, read denied by content rule.",
        Permissions: permissions,
      });
      const roleId = requireId(role, "id", `createRestrictedUser: POST /api/roles`);

      for (const entityKind of denyReadEntityKinds) {
        // The vocabulary is the host's own (ContentRuleService's valid effect/scope/appliesTo sets);
        // it is lowercase there and matched case-insensitively, so it is written that way here rather
        // than in an invented uppercase form. An empty ScopeValue is normalised to `{}` by the host,
        // which is what a scope of "all" wants.
        await asOwner("/api/content-rules", {
          RoleId: roleId,
          EntityKind: entityKind,
          Effect: "deny",
          ScopeKind: "all",
          ScopeValue: "",
          AppliesTo: "read",
        });
      }

      const user = await asOwner("/api/users", {
        Username: username,
        Password: password,
        Roles: [roleName],
      });
      const userId = requireId(user, "id", `createRestrictedUser: POST /api/users`);

      const loginPayload = await asOwner("/api/auth/login", { username, password });
      const token = readToken(loginPayload, "createRestrictedUser login");

      return { token, userId, roleId, roleName, username, password };
    },

    async stop() {
      await started.down({ removeVolumes: true });
    },
  };

  // The access token is `token` — NOT `accessToken`. Reading the wrong field yields
  // `Bearer undefined`, which the host rejects identically to sending no header at all, so a spec
  // would go red for a broken fixture rather than for the behavior it means to prove. Asserted here,
  // at the one place any login response's field name is spelled, so that failure names itself.
  //
  // Returns the token instead of storing it: the handle's `token` is the owner's by contract, and a
  // helper that mints a second, deliberately less privileged one must not be able to overwrite it —
  // silently swapping the owner's credential for a restricted one turns every later fixture call into
  // a permission failure a long way from its cause.
  function readToken(response, source) {
    if (typeof response?.token !== "string" || response.token.length === 0) {
      throw new Error(
        `${source}: response carried no usable token (top-level keys: ${Object.keys(response ?? {}).join(", ") || "<none>"})`,
      );
    }
    return response.token;
  }

  return handle;
}

// Reads an id the host minted, failing with the keys it actually returned rather than handing a
// caller `undefined` to put in a URL — where it reads as a 404 about a missing entity instead of as
// a wire-shape mismatch.
function requireId(payload, field, source) {
  const value = payload?.[field];
  if (typeof value !== "number") {
    throw new Error(
      `${source}: response carried no numeric "${field}" (top-level keys: ${Object.keys(payload ?? {}).join(", ") || "<none>"})`,
    );
  }
  return value;
}

// The restart's own wait strategy is a health check, but that probe runs INSIDE the container and
// says nothing about the host side, where an ephemeral published port is being re-bound at the same
// moment. A fetch that lands in that gap REJECTS rather than answering a status, and every call
// after the restart is a bare fetch — the first of them inside a per-test fixture, so a single
// rejection there fails every test in the suite while naming neither the restart nor the gap.
//
// Any status counts as reachable. The container's health check already gated the app being up, so
// the only open question here is whether the host can reach it at all — answering that without
// assuming which statuses /health may return keeps this independent of whether the instance
// enforces authentication.
//
// Each attempt carries its own abort signal, because the deadline below is only consulted BETWEEN
// attempts and Node's fetch has no default timeout of its own. Docker's userland port proxy accepts
// the TCP connection while the app inside is still starting, so an attempt that lands in the gap
// connects and then waits for a response that never comes — with no per-attempt bound that single
// call never settles, the loop never re-tests its deadline, and the run hangs until the outer job
// timeout kills it without naming the restart.
//
// `timeoutMs` is REQUIRED, and stated by the caller rather than defaulted here: how long a restart may
// take is a property of the instance under test, not of this helper, so no one value is right for every
// call site. Absent, it produced a NaN deadline the `while` never entered — zero attempts, then a
// failure blaming `undefinedms` and a "last error" of never having tried. A gate that inspects nothing
// must say so, not report a timeout it never ran.
async function waitForHostReachable(baseUrl, { timeoutMs, intervalMs = 500 }) {
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    throw new Error(`waitForHostReachable: timeoutMs must be a positive number, got ${timeoutMs}`);
  }
  const attemptTimeoutMs = Math.min(intervalMs * 4, 5_000);
  const deadline = Date.now() + timeoutMs;
  let lastError = "never attempted";
  while (Date.now() < deadline) {
    const res = await fetch(`${baseUrl}/health`, {
      signal: AbortSignal.timeout(attemptTimeoutMs),
    }).catch((err) => {
      lastError = err?.message ?? String(err);
      return null;
    });
    if (res) return;
    await new Promise((r) => setTimeout(r, intervalMs));
  }
  throw new Error(
    `waitForHostReachable: ${baseUrl}/health did not answer from the host within ${timeoutMs}ms (last error: ${lastError})`,
  );
}

// `GET /api/extensions` carries a permission requirement, so under an auth-enabled instance this
// poll answers 401 forever without a token — and it runs inside installExtension(), before any test
// body. Hence the token parameter: an auth-on suite cannot reach its first assertion without it.
//
// Per-attempt abort bound and required `timeoutMs` for the same reasons spelled out above
// waitForHostReachable, and this poll runs immediately after the very restart that function was
// bounded for — the container's port proxy is accepting connections the app is not yet answering.
// The bound covers reading the body too, not just the headers: an abort landing mid-read rejects,
// and a rejection escaping the loop would fail the suite blaming the abort rather than the wait.
async function waitForExtensionEnabled(
  baseUrl,
  extensionId,
  { timeoutMs, intervalMs = 1000, token },
) {
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    throw new Error(
      `waitForExtensionEnabled: timeoutMs must be a positive number, got ${timeoutMs}`,
    );
  }
  const attemptTimeoutMs = Math.min(intervalMs * 4, 5_000);
  const deadline = Date.now() + timeoutMs;
  let lastPoll = "never attempted";
  while (Date.now() < deadline) {
    const match = await fetch(`${baseUrl}/api/extensions`, {
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
      signal: AbortSignal.timeout(attemptTimeoutMs),
    })
      .then(async (res) => {
        if (!res.ok) {
          lastPoll = `HTTP ${res.status}`;
          return null;
        }
        const found = (await res.json()).find((e) => e.id === extensionId) ?? null;
        lastPoll = found ? `present, enabled=${found.enabled}` : "not present in the list";
        return found;
      })
      .catch((err) => {
        lastPoll = err?.message ?? String(err);
        return null;
      });
    if (match?.enabled) return match;
    await new Promise((r) => setTimeout(r, intervalMs));
  }
  throw new Error(
    `waitForExtensionEnabled: extension "${extensionId}" was not found/enabled within ${timeoutMs}ms at ${baseUrl}/api/extensions (last poll: ${lastPoll})`,
  );
}
