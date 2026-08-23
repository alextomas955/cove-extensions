// Core harness lifecycle: bring up an isolated Cove instance, wait for it to be ready, install an
// extension into it, and tear it down. This is the one entry point extension authors need —
// everything else (compose file, install mechanics, staging) is an implementation detail behind it.
//
// Built on Testcontainers (https://node.testcontainers.org/), not a hand-rolled `docker compose`
// child_process wrapper. Testcontainers' Ryuk sidecar guarantees container/network/volume cleanup
// even if the test process is killed (not just on a graceful exit) — a hand-rolled wrapper only
// cleans up in the success path, leaking containers on a killed run. It also owns port resolution
// and health-check waiting, so this file hand-rolls no polling loop of its own.
import { join } from "node:path";
import { DockerComposeEnvironment, Wait } from "testcontainers";
import { installViaContainerCopy } from "./install-extension.mjs";
import { createApiClient } from "./apiClient.mjs";
import { attemptUntil } from "./poll.mjs";
// The repository the image lives in and the floor each extension declares both already have exactly
// one reader, and a second parse of either here would be free to disagree with the one CI resolves
// against.
import {
  compareSemver,
  parseSemver,
  readCoveImageReference,
  readExtensionFloors,
} from "../../../scripts/fetch-cove-assemblies.mjs";

// Re-exported rather than wrapped: a spec gates on a host capability, and the version logic it needs
// already lives with the other semver helpers.
export { imageAtLeastVersion } from "../../../scripts/fetch-cove-assemblies.mjs";

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
 * In order: a reference the caller states outright (a locally built host, say); a complete reference
 * in `COVE_E2E_IMAGE`; a version in `COVE_E2E_TAG`, placed on the repository the build properties
 * declare; and failing all three the highest floor the catalog's extensions declare in their own
 * manifests. The compose file holds no default, so this is the only thing that decides.
 *
 * A CI leg can only supply the tag-only form, because a version leg resolves a VERSION while the
 * repository is declared once in the build properties.
 *
 * A host BELOW an extension's floor does not error: its version gate silently refuses to LOAD the
 * extension, so the routes 404 and every browser spec fails against a Settings page that never gains
 * the extension's tab. The floor taken is the HIGHEST declared, since one instance serves whichever
 * extensions a run installs into it.
 *
 * Throws rather than falling back when a floor cannot be read or is not strict semver.
 *
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

/**
 * The highest `minCoveVersion` declared by a catalog entry that has an e2e suite.
 *
 * Narrowed to those entries because only they can be installed into an instance this harness boots.
 * A catalog entry with no suite reaching this decision would let an extension nothing here installs
 * raise the host every suite runs against, or fail the whole tier over a manifest no spec reads.
 */
function highestDeclaredFloor() {
  let highest = null;
  const withSuite = (entry) => Boolean(entry.e2ePath && entry.e2eProject);
  for (const { entry, floor, manifestPath } of readExtensionFloors(withSuite)) {
    const parsed = parseSemver(floor);
    if (parsed === null) {
      throw new Error(
        `${manifestPath} declares minCoveVersion '${floor}' for '${entry.id ?? entry.name}', which is not a strict X.Y.Z semver version, so no Cove image can be resolved from it.`,
      );
    }
    if (highest === null || compareSemver(parsed, highest) > 0) highest = parsed;
  }
  if (highest === null) {
    throw new Error(
      "No catalog entry declares both e2ePath and e2eProject, so no Cove image can be resolved from a floor. Name one in COVE_E2E_IMAGE, or register the suite in extensions/catalog.json.",
    );
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
  const coveContainer = started.getContainer("cove-1");
  // Resolved eagerly, like the Cove container above: a service name that no longer matches fails
  // here, at startup, rather than part-way through whatever assertion first reached for it.
  const dbContainer = started.getContainer("db-1");

  // Remembered by bootstrapOwner so the handle can re-authenticate itself after a restart without
  // the caller having to hold on to the credentials.
  let credentials = null;

  // Both read the address through the handle, because a restart can republish the container on a
  // different host port. `api` also carries whatever token the handle currently holds; `anonymous`
  // deliberately carries none, since the endpoints that MINT a credential are the ones that must not
  // present a stale one.
  const api = createApiClient(
    () => handle.baseUrl,
    () => handle.token,
  );
  const anonymous = createApiClient(() => handle.baseUrl);

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
      await waitForExtensionEnabled(api, result.id, { timeoutMs });
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
      // An access token does not survive the restart, so it is re-minted here; otherwise every later
      // call against an auth-enabled instance fails as an authentication error. `login` retries a
      // transient of its own, which matters here: reachability is a weaker condition than readiness,
      // so this call can land while the host is still answering its maintenance status.
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
      const { response, lastError } = await postUntilSettled(
        anonymous,
        "/api/auth/bootstrap-owner",
        { username, password },
        { timeoutMs },
      );

      // The host refuses a second bootstrap with a conflict, so reaching one means an owner already
      // exists under these credentials — an attempt that completed on the server after its
      // client-side bound expired. Signing in finishes what this call promised rather than failing
      // over work that already succeeded.
      if (response?.status === 409) {
        credentials = { username, password };
        return await handle.login({ username, password });
      }

      if (!response?.ok) {
        // Cove answers an unhandled exception with a bare 500 and an empty body, so the failure as
        // thrown says nothing about its own cause. Carry the server's own log.
        throw new Error(
          [
            `bootstrapOwner: POST /api/auth/bootstrap-owner did not succeed within ${timeoutMs}ms (last: ${describeAttempt(response, lastError)})`,
            "--- cove container log (tail) ---",
            await tailContainerLog(coveContainer),
          ].join("\n"),
        );
      }

      credentials = { username, password };
      handle.token = readToken(response.json, "bootstrapOwner");
      return response.json;
    },

    /**
     * Signs the stored owner credentials in again and replaces `token`. Needed after a container
     * restart, which invalidates every token minted before it.
     */
    async login({ username, password } = credentials ?? {}) {
      if (!username || !password) {
        throw new Error("login: no credentials — call bootstrapOwner() first, or pass them here");
      }
      const { response, lastError } = await postUntilSettled(
        anonymous,
        "/api/auth/login",
        { username, password },
        { timeoutMs },
      );
      if (!response?.ok) {
        throw new Error(
          `login: POST /api/auth/login did not succeed within ${timeoutMs}ms (last: ${describeAttempt(response, lastError)})`,
        );
      }
      credentials = { username, password };
      handle.token = readToken(response.json, "login");
      return response.json;
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
        const res = await api.post(path, body);
        if (!res.ok) {
          throw new Error(
            `createRestrictedUser: POST ${path} failed (${res.status}): ${res.text || "<empty body>"}`,
          );
        }
        // The shared client reports an unparseable body as `undefined` json, which every other
        // caller treats as "nothing returned". Here a missing object is a failure: the ids read out
        // of it are what the rest of this helper is built on.
        if (res.text && res.json === undefined) {
          throw new Error(
            `createRestrictedUser: POST ${path} answered ${res.status} with a body that is not JSON: ${res.text}`,
          );
        }
        return res.json;
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

  return handle;
}

// Both auth endpoints this harness posts sit behind the host's strict authentication rate limiter,
// keyed by client address, so a sub-second retry would spend that budget on the wait itself and turn
// the outcome into a refusal indistinguishable from the failure being waited out.
const AUTH_RETRY_INTERVAL_MS = 2_000;

// Generous, because an attempt that is merely slow (the host hashing a password while it also
// migrates its schema) is one to let finish. A bound that fires anyway loses no work: the 409 path in
// bootstrapOwner adopts an attempt the server completed after this gave up on it.
const AUTH_ATTEMPT_TIMEOUT_MS = 15_000;

/** A status the host may answer while still coming up, as opposed to a verdict on the request. */
function isTransientStatus(status) {
  return status >= 500 || status === 429;
}

/** Renders whichever of the two outcomes actually happened, for an error message. */
function describeAttempt(response, lastError) {
  if (!response) return lastError;
  return `HTTP ${response.status}: ${response.text || "<empty body>"}`;
}

/**
 * POSTs until the host answers something that is a verdict rather than a symptom of still starting,
 * or the deadline passes. Returns the settled response (null if none arrived) — never throws on a
 * status, so each caller raises an error naming its own operation.
 *
 * Cove seeds its built-in roles on a background task that host startup neither awaits nor covers
 * with its maintenance gate, so `/health` answers 200 while it runs. `POST /api/auth/bootstrap-owner`
 * inserts the Owner role itself when it is absent, so inside that window two writers race one unique
 * role name and the loser escapes as a bare 500. The role exists by the next attempt.
 *
 * Retrying rather than first waiting for the role to appear, because that is not observable from
 * here: listing roles requires a permission, an auth-enabled instance answers 401 to the anonymous
 * caller this necessarily is, and no owner yet exists to mint a token from.
 */
async function postUntilSettled(
  api,
  path,
  body,
  { timeoutMs, intervalMs = AUTH_RETRY_INTERVAL_MS },
) {
  const { value, note } = await attemptUntil(
    async (signal, note) => {
      const res = await api.post(path, body, { signal }).catch((err) => {
        note(err?.message ?? String(err));
        return null;
      });
      if (!res) return null;
      if (!isTransientStatus(res.status)) return { value: res };
      note(describeAttempt(res));
      return null;
    },
    {
      timeoutMs,
      intervalMs,
      attemptTimeoutMs: AUTH_ATTEMPT_TIMEOUT_MS,
      label: "postUntilSettled",
    },
  );
  return { response: value ?? null, lastError: note };
}

/**
 * Best-effort tail of a container's own log. Never throws and never hangs: a diagnostic that can fail
 * the run it is trying to explain is worse than no diagnostic, so every failure mode degrades to a
 * short note.
 */
async function tailContainerLog(container, { lines = 60, timeoutMs = 5000 } = {}) {
  try {
    const stream = await container.logs();
    const chunks = [];
    stream.on("data", (chunk) => chunks.push(chunk.toString()));
    await new Promise((resolve) => {
      // Clear the timer and drop the stream on every exit path: an uncleared timer and an open
      // stream both hold the event loop open, which reads as the suite hanging after its last test.
      const timer = setTimeout(() => done(), timeoutMs);
      function done() {
        clearTimeout(timer);
        stream.destroy();
        resolve();
      }
      stream.once("end", done);
      stream.once("error", done);
    });
    const text = chunks.join("");
    return text ? text.split("\n").slice(-lines).join("\n") : "<container produced no log output>";
  } catch (err) {
    return `<container log unavailable: ${err.message}>`;
  }
}

// Reads an id the host minted, failing with the keys it actually returned rather than handing a
// caller `undefined` to put in a URL — where it reads as a 404 about a missing entity instead of as
// a wire-shape mismatch.
function requireId(payload, field, source) {
  const value = payload?.[field];
  if (typeof value !== "number") {
    throw new TypeError(
      `${source}: response carried no numeric "${field}" (top-level keys: ${Object.keys(payload ?? {}).join(", ") || "<none>"})`,
    );
  }
  return value;
}

// The access token is `token`, NOT `accessToken`: the wrong field yields `Bearer undefined`, which
// the host rejects exactly as it rejects no header at all. Returns the token rather than storing it,
// so a helper minting a deliberately under-privileged one cannot overwrite the owner's.
function readToken(response, source) {
  if (typeof response?.token !== "string" || response.token.length === 0) {
    throw new TypeError(
      `${source}: response carried no usable token (top-level keys: ${Object.keys(response ?? {}).join(", ") || "<none>"})`,
    );
  }
  return response.token;
}

// The restart's own wait strategy is a health check, but that probe runs INSIDE the container and
// says nothing about the host side, where an ephemeral published port is being re-bound at the same
// moment. A fetch that lands in that gap REJECTS rather than answering a status, and every call
// after the restart is a bare fetch — the first of them inside a per-test fixture, so a single
// rejection there fails every test in the suite while naming neither the restart nor the gap.
//
// Any status counts as reachable. The container's health check already gated the app being up, so
// the only open question here is whether the host can reach it at all, and not assuming which
// statuses /health may return keeps this independent of whether the instance enforces
// authentication.
async function waitForHostReachable(baseUrl, { timeoutMs, intervalMs = 500 }) {
  const { settled, note } = await attemptUntil(
    async (signal, note) => {
      const res = await fetch(`${baseUrl}/health`, { signal }).catch((err) => {
        note(err?.message ?? String(err));
        return null;
      });
      return res ? { value: res } : null;
    },
    {
      timeoutMs,
      intervalMs,
      attemptTimeoutMs: Math.min(intervalMs * 4, 5_000),
      label: "waitForHostReachable",
    },
  );
  if (!settled) {
    throw new Error(
      `waitForHostReachable: ${baseUrl}/health did not answer from the host within ${timeoutMs}ms (last error: ${note})`,
    );
  }
}

// Takes the token-carrying client, because `GET /api/extensions` requires a permission: under an
// auth-enabled instance an anonymous poll answers 401 forever, and this runs inside
// installExtension(), before any test body, so such a suite could never reach its first assertion.
//
// The per-attempt bound covers reading the body too, not just the headers: an abort landing mid-read
// rejects, and a rejection escaping the loop would blame the abort rather than the wait.
async function waitForExtensionEnabled(api, extensionId, { timeoutMs, intervalMs = 1000 }) {
  const { settled, value, note } = await attemptUntil(
    async (signal, note) => {
      const found = await api
        .get("/api/extensions", { signal })
        .then((res) => {
          if (!res.ok) {
            note(`HTTP ${res.status}`);
            return null;
          }
          if (!Array.isArray(res.json)) {
            note("GET /api/extensions did not return an array");
            return null;
          }
          const match = res.json.find((e) => e.id === extensionId) ?? null;
          note(match ? `present, enabled=${match.enabled}` : "not present in the list");
          return match;
        })
        .catch((err) => {
          note(err?.message ?? String(err));
          return null;
        });
      return found?.enabled ? { value: found } : null;
    },
    {
      timeoutMs,
      intervalMs,
      attemptTimeoutMs: Math.min(intervalMs * 4, 5_000),
      label: "waitForExtensionEnabled",
    },
  );
  if (!settled) {
    throw new Error(
      `waitForExtensionEnabled: extension "${extensionId}" was not found/enabled within ${timeoutMs}ms at ${api.baseUrl}/api/extensions (last poll: ${note})`,
    );
  }
  return value;
}
