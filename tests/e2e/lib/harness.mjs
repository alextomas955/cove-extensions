// Brings up an isolated Cove instance, installs an extension into it, and tears it down.
//
// Testcontainers' Ryuk sidecar reaps containers, networks and volumes even when the test process is
// killed rather than exiting gracefully, and it owns port resolution and health-check waiting.
import { randomUUID } from "node:crypto";
import { join } from "node:path";
import { DockerComposeEnvironment, Wait } from "testcontainers";
import { installViaContainerCopy } from "./install-extension.mjs";
import { createApiClient } from "./apiClient.mjs";
import { attemptUntil } from "./poll.mjs";
import {
  compareSemver,
  parseSemver,
  readCoveImageReference,
  readExtensionFloors,
} from "../../../scripts/cove-versions.mjs";

// import.meta.dirname, never a filesystem path read off a module URL's path component: on Windows
// that yields a leading-slash form which resolves to a doubled drive prefix.
const COMPOSE_DIR = join(import.meta.dirname, "..", "docker");
const COMPOSE_FILE = "docker-compose.yml";

// A shared runner cold-starts containers more slowly than a dev machine's Docker Desktop.
const DEFAULT_STARTUP_TIMEOUT_MS = process.env.CI ? 240_000 : 180_000;

/**
 * The Cove image an instance boots. The compose file holds no default, so this is the only thing
 * that decides.
 *
 * A host below an extension's floor does not error: Cove's version gate silently refuses to load
 * the extension, so its routes 404 and every browser spec fails against a Settings page that never
 * gains the extension's tab.
 *
 * @param {string} [image] - an explicit complete reference, which wins over both environment forms.
 * @returns {string} a complete image reference.
 */
export function resolveCoveImage(image) {
  if (image) return image;
  if (process.env.COVE_E2E_IMAGE) return process.env.COVE_E2E_IMAGE;
  // registry and repository, never the `repository` field alone: that one is the host-less path, and a
  // reference missing its registry host resolves to Docker Hub - a real image, from a registry nobody
  // named.
  const { registry, repository } = readCoveImageReference();
  return `${registry}/${repository}:${process.env.COVE_E2E_TAG || highestDeclaredFloor()}`;
}

/**
 * The highest `minCoveVersion` declared by a catalog entry that has an e2e suite. One instance
 * serves whichever extensions a run installs into it. An entry without a suite would otherwise
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
 * Brings up an isolated Cove instance. Every instance gets a random compose project name and a
 * random host port, so parallel test runs never collide.
 *
 * `env` reaches any `${VAR:-default}` substitution in docker-compose.yml - e.g.
 * `{ COVE_E2E_AUTH_ENABLED: 'true' }` for an instance that must enforce real authentication.
 *
 * `image` is a complete reference and overrides every other source.
 */
export async function startHarness({ image, env, timeoutMs = DEFAULT_STARTUP_TIMEOUT_MS } = {}) {
  let environment = new DockerComposeEnvironment(COMPOSE_DIR, COMPOSE_FILE)
    .withStartupTimeout(timeoutMs)
    // Keyed on container names (`<service>-<index>`, the same names getContainer takes below), never
    // service names: Testcontainers drops a key matching no container with only a log warning, and
    // leaves whatever it inferred from the image in force. restart() reuses the strategy named here.
    .withWaitStrategy("cove-1", Wait.forHealthCheck())
    .withWaitStrategy("db-1", Wait.forHealthCheck());

  // Names this stack's database on the shared network. Workers are separate processes, so the value
  // has to be unique across them, not only within one.
  const composeEnv = {
    COVE_E2E_IMAGE: resolveCoveImage(image),
    COVE_E2E_STACK: randomUUID().slice(0, 8),
    ...env,
  };
  environment = environment.withEnvironment(composeEnv);

  const started = await environment.up();
  const coveContainer = started.getContainer("cove-1");
  const dbContainer = started.getContainer("db-1");

  let credentials = null;

  // `anonymous` carries no token: the endpoints that mint a credential are the ones that must not
  // present a stale one.
  const api = createApiClient(
    () => handle.baseUrl,
    () => handle.token,
  );
  const anonymous = createApiClient(() => handle.baseUrl);

  const handle = {
    /** The bootstrapped owner's bearer token. Undefined until `bootstrapOwner()` runs. */
    token: undefined,

    get baseUrl() {
      return `http://${coveContainer.getHost()}:${coveContainer.getMappedPort(5073)}`;
    },
    get containerId() {
      return coveContainer.getId();
    },
    /** The raw Testcontainers container, for helpers that need copyFilesToContainer or exec. */
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
     * A copied-in extension is only discovered on a (re)start, and an extension's initialize-time
     * path does not run again while the host stays up, so this is the only way to reach either.
     *
     * `baseUrl` may change across this call: a container published on an ephemeral host port can be
     * reassigned a new one, so a caller holding a previously-read `baseUrl`, or anything built from
     * one, must re-read it.
     *
     * Returning means the host answers `/health` with 2xx. It does not mean an extension finished
     * initializing: a caller depending on initialize-time work must wait for that work's own
     * observable outcome.
     */
    async restart() {
      await coveContainer.restart();
      await waitForHostReady(handle.baseUrl, { timeoutMs });
      // An access token does not survive the restart.
      if (handle.token) {
        await handle.login();
      }
    },

    exec(command, opts) {
      return coveContainer.exec(command, opts);
    },

    /**
     * Runs a command inside the database container - the one way to ask the database itself whether
     * the host really created an extension's tables. A failed extension migration is a host log
     * line and the load continues, so an extension can be enabled with no table behind it.
     *
     * `opts` carries the statement, passed through `env` so it can hold quotes, and the credentials
     * are read from the container's own environment (`sh -c 'psql -U "$POSTGRES_USER" …'`).
     * Omitting `opts` fails silently, because psql exits 0 on an empty statement.
     */
    execDb(command, opts) {
      return dbContainer.exec(command, opts);
    },

    /**
     * Creates the first (owner) account and returns its access token. Required before any
     * browser-driven test: Cove's frontend hard-gates the whole app behind a first-run setup wizard
     * whenever no owner account exists, and its "Skip setup for now" button does nothing then.
     *
     * `COVE__Auth__Enabled=false` does not lift that gate. The auth-bypass principal serves API
     * calls, but the UI still reads `GET /api/auth/bootstrap-status`'s `ownerExists` and refuses to
     * render past the wizard until `POST /api/auth/bootstrap-owner` has run.
     */
    async bootstrapOwner({ username = "e2e-owner", password = "E2eTestPassword123!" } = {}) {
      const { response, lastError } = await postUntilSettled(
        anonymous,
        "/api/auth/bootstrap-owner",
        { username, password },
        { timeoutMs },
      );

      // The host refuses a second bootstrap with a conflict, so a 409 means an earlier attempt
      // completed on the server after its client-side bound expired.
      if (response?.status === 409) {
        credentials = { username, password };
        return await handle.login({ username, password });
      }

      if (!response?.ok) {
        // Cove answers an unhandled exception with a bare 500 and an empty body, so the thrown
        // failure says nothing about its own cause without the server's log.
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

    /** Signs the stored owner credentials in again and replaces `token`. */
    async login({ username, password } = credentials ?? {}) {
      if (!username || !password) {
        throw new Error("login: no credentials - call bootstrapOwner() first, or pass them here");
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
     * Creates a non-owner user Cove's row-level authorization filters actually apply to, and returns
     * its token without replacing the handle's own.
     *
     * `CoveContext` short-circuits every one of those filters to true for a principal holding the
     * `"*"` permission, which Cove's bootstrap grants to the owner role, and for a missing
     * principal. So neither `bootstrapOwner()`'s token nor an anonymous call can observe row-level
     * authorization; only a present, under-privileged user does.
     *
     * A permission list alone will not produce one. Cove's write permissions declare the matching
     * read as implied, so a role granted `videos.write` is expanded to hold `videos.read` and reaches
     * every video read endpoint. A content rule denying read on a kind leaves the permission in place
     * while making the per-entity SQL predicate answer false, which is the case where a caller gets
     * 200 and zero rows rather than a 403 naming itself.
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
      // Creating a role, a content rule and a user require RolesWrite/UsersWrite, which at this
      // point only the bootstrapped owner holds.
      const asOwner = async (path, body) => {
        const res = await api.post(path, body);
        if (!res.ok) {
          throw new Error(
            `createRestrictedUser: POST ${path} failed (${res.status}): ${res.text || "<empty body>"}`,
          );
        }
        // The shared client reports an unparseable body as `undefined` json. Here that is a
        // failure: the rest of this helper needs the ids.
        if (res.text && res.json === undefined) {
          throw new Error(
            `createRestrictedUser: POST ${path} answered ${res.status} with a body that is not JSON: ${res.text}`,
          );
        }
        return res.json;
      };

      const role = await asOwner("/api/roles", {
        Name: roleName,
        Description: "Restricted e2e role - no wildcard, read denied by content rule.",
        Permissions: permissions,
      });
      const roleId = requireId(role, "id", `createRestrictedUser: POST /api/roles`);

      for (const entityKind of denyReadEntityKinds) {
        // The effect/scope/appliesTo vocabulary is the host's own, lowercase there and matched
        // case-insensitively. An empty ScopeValue is normalised to `{}`, which a scope of "all"
        // wants.
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

// Wide enough to let an attempt that is merely slow finish: the host may be hashing a password
// while it also migrates its schema. A bound that fires anyway loses no work, because
// bootstrapOwner's 409 path adopts an attempt the server completed after this gave up on it.
const AUTH_ATTEMPT_TIMEOUT_MS = 15_000;

/** A status the host may answer while still coming up, as opposed to a verdict on the request. */
function isTransientStatus(status) {
  return status >= 500 || status === 429;
}

function describeAttempt(response, lastError) {
  if (!response) return lastError;
  return `HTTP ${response.status}: ${response.text || "<empty body>"}`;
}

/**
 * POSTs until the host answers something that is a verdict rather than a symptom of still starting,
 * or the deadline passes. Returns the settled response (null if none arrived) - never throws on a
 * status, so each caller raises an error naming its own operation.
 *
 * Cove seeds its built-in roles on a background task that host startup neither awaits nor covers
 * with its maintenance gate, so `/health` answers 200 while it runs. `POST /api/auth/bootstrap-owner`
 * inserts the Owner role itself when it is absent, so inside that window two writers race one unique
 * role name and the loser escapes as a bare 500. The role exists by the next attempt.
 *
 * The role's arrival is not observable from here: listing roles requires a permission, an
 * auth-enabled instance answers 401 to the anonymous caller this necessarily is, and no owner yet
 * exists to mint a token from.
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
 * Best-effort tail of a container's own log. Never throws and never hangs: every failure mode
 * degrades to a short note, so a diagnostic cannot fail the run it is trying to explain.
 */
async function tailContainerLog(container, { lines = 60, timeoutMs = 5000 } = {}) {
  try {
    const stream = await container.logs();
    const chunks = [];
    stream.on("data", (chunk) => chunks.push(chunk.toString()));
    await new Promise((resolve) => {
      // An uncleared timer and an open stream both hold the event loop open, which reads as the
      // suite hanging after its last test.
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

// Fails with the keys the host returned rather than handing a caller `undefined` to put in a URL,
// where it reads as a 404 about a missing entity instead of as a wire-shape mismatch.
function requireId(payload, field, source) {
  const value = payload?.[field];
  if (typeof value !== "number") {
    throw new TypeError(
      `${source}: response carried no numeric "${field}" (top-level keys: ${Object.keys(payload ?? {}).join(", ") || "<none>"})`,
    );
  }
  return value;
}

// The access token is `token`, not `accessToken`: the wrong field yields `Bearer undefined`, which
// the host rejects exactly as it rejects no header at all.
function readToken(response, source) {
  if (typeof response?.token !== "string" || response.token.length === 0) {
    throw new TypeError(
      `${source}: response carried no usable token (top-level keys: ${Object.keys(response ?? {}).join(", ") || "<none>"})`,
    );
  }
  return response.token;
}

/**
 * Waits until `/health` answers 2xx from the host side. Cove returns 503 there while its database
 * is not connectable and 200 once it is, so any-response-is-ready hands the caller a window in
 * which a browser page's data requests all fail.
 *
 * The restart's own wait strategy is a health check, but that probe runs inside the container and
 * says nothing about the host side, where an ephemeral published port is being re-bound at the same
 * moment. A fetch that lands in that gap rejects rather than answering a status.
 */
async function waitForHostReady(baseUrl, { timeoutMs, intervalMs = 500 }) {
  const { settled, note } = await attemptUntil(
    async (signal, note) => {
      const res = await fetch(`${baseUrl}/health`, { signal }).catch((err) => {
        note(err?.message ?? String(err));
        return null;
      });
      if (!res) return null;
      if (!res.ok) {
        note(`HTTP ${res.status}`);
        return null;
      }
      return { value: res };
    },
    {
      timeoutMs,
      intervalMs,
      attemptTimeoutMs: Math.min(intervalMs * 4, 5_000),
      label: "waitForHostReady",
    },
  );
  if (!settled) {
    throw new Error(
      `waitForHostReady: ${baseUrl}/health did not answer 2xx within ${timeoutMs}ms (last attempt: ${note})`,
    );
  }
}

// Takes the token-carrying client, because `GET /api/extensions` requires a permission: under an
// auth-enabled instance an anonymous poll answers 401 forever, and this runs before any test body.
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
