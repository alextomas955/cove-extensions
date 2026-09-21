// The steps more than one spec takes to get a run into position.
//
// None of these asserts anything about this product. They open a page, read a secret back, list what
// Cove holds, restart the worker: the arrangement a spec needs before its own assertions begin. A
// copy of each in every spec that needs it drifts, and a drifted copy is a spec doing something
// slightly different from the one beside it for no reason anybody chose.
import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";

import { createApiClient, expect } from "@cove-extensions/e2e";

import {
  CALLBACK_STATUS_ROUTE,
  CAPTURED_DELIVERY,
  DATA_ROUTE,
  DISABLE_ROUTE,
  ENABLE_ROUTE,
  OPTIONS_KEY,
  SECRET_HEADER,
  SECRET_QUERY_PARAMETER,
  USER_AGENT,
} from "./contract.mjs";

/**
 * A client presenting what a delivering instance presents and nothing more: this generation's agent,
 * the shared secret where one is given, and no Cove credential - Whisparr holds none.
 *
 * The address is read through a getter rather than captured, for the reason createApiClient
 * documents: a restart re-mints the token and can republish the container on a different host port.
 *
 * @param {() => string} baseUrl
 * @param {"v2"|"v3"} generation
 * @param {{secret?: string, headers?: Record<string, string>}} presenting
 */
export function whisparrCaller(baseUrl, generation, { secret, headers = {} } = {}) {
  const agent = USER_AGENT[generation];
  if (agent === undefined) {
    throw new Error(
      `whisparrCaller: no agent is transcribed for "${generation}"; transcribed are ${Object.keys(USER_AGENT).join(", ")}.`,
    );
  }
  return createApiClient(baseUrl, undefined, {
    headers: {
      "User-Agent": agent,
      ...(secret === undefined ? {} : { [SECRET_HEADER]: secret }),
      ...headers,
    },
  });
}

/**
 * How long one navigation is given to render what the caller named, and how many are tried.
 *
 * The product of the two stays well inside the 180s per-test timeout in playwright.config.mjs. At
 * one minute an attempt it equalled that timeout exactly, so a page that never rendered was killed
 * by the runner one instant before the throw below, and the diagnostic naming the label, the
 * attempts and the final URL never printed.
 */
const PAGE_BUDGET_MS = 30_000;
const PAGE_ATTEMPTS = 3;

/**
 * Opens `path`, re-navigating while nothing the caller named has rendered.
 *
 * The host paints its own error boundary in place of a page whose lazily-imported chunk failed to
 * fetch, on the correct URL and indefinitely. Only a fresh navigation recovers it, and the retry is
 * bounded so a permanent failure is not turned into a hung test.
 *
 * @param {object} present - a locator for something the loaded page must draw
 * @param {string} label - what the caller was opening, named in the failure
 */
export async function visit(
  page,
  baseUrl,
  path,
  present,
  label,
  { attempts = PAGE_ATTEMPTS, budgetMs = PAGE_BUDGET_MS } = {},
) {
  // Held rather than discarded: a locator that matches nothing and a chunk that never fetched both
  // time out here, and without the last failure the two are one message.
  let lastFailure = null;
  for (let attempt = 1; attempt <= attempts; attempt++) {
    await page.goto(`${baseUrl}${path}`);
    const rendered = await present
      .waitFor({ state: "visible", timeout: budgetMs })
      .then(() => true)
      .catch((failure) => {
        lastFailure = failure;
        return false;
      });
    if (rendered) return;
  }
  throw new Error(
    `${label}: nothing rendered at ${baseUrl}${path} across ${String(attempts)} navigation(s) of ${String(budgetMs)}ms each; the page is now at ${page.url()}. The last attempt failed with: ${String(lastFailure?.message ?? lastFailure)}`,
  );
}

/** This installation's own callback secret, read out of the address the page offers. */
export async function callbackSecret(api) {
  const status = await api.get(CALLBACK_STATUS_ROUTE);
  expect(
    status.status,
    `GET ${CALLBACK_STATUS_ROUTE} answered: ${String(status.text).slice(0, 300)}`,
  ).toBe(200);

  const secret = new URL(status.json.copyableAddress).searchParams.get(SECRET_QUERY_PARAMETER);
  expect(secret, `the copyable address carried no ${SECRET_QUERY_PARAMETER}`).toBeTruthy();
  return secret;
}

/** Every video Cove holds. */
export async function videosIn(api) {
  const listed = await api.get("/api/videos?perPage=200");
  expect(listed.status, `GET /api/videos answered: ${String(listed.text).slice(0, 300)}`).toBe(200);
  return listed.json?.items ?? [];
}

/** The file path of every video Cove holds. */
export async function videoPathsIn(api) {
  const videos = await videosIn(api);
  return videos.flatMap((video) => (video.files ?? []).map((file) => file.path).filter(Boolean));
}

/**
 * One video as Cove holds it, with its files and its identity rows.
 *
 * Each read carries its own query so it gets its own output-cache entry: the host caches this route
 * briefly, and two reads a moment apart would otherwise be one answer.
 */
export async function videoDetail(api, id) {
  const held = await api.get(`/api/videos/${String(id)}?_=${randomUUID()}`);
  expect(held.status, `GET /api/videos/${String(id)} answered: ${held.text.slice(0, 300)}`).toBe(
    200,
  );
  return held.json;
}

/**
 * The extension's stored options blob, parsed, or null while the route is not answering.
 *
 * Re-enabling the extension republishes its endpoints a moment after the request that asked for it
 * returns, so a read taken in that window is a state to poll through rather than a failure.
 */
export async function readOptions(api) {
  const data = await api.get(DATA_ROUTE);
  return data.status === 200 ? JSON.parse(data.json?.[OPTIONS_KEY] ?? "{}") : null;
}

/** The stored options blob, insisting the route answers. */
export async function storedOptions(api) {
  const options = await readOptions(api);
  expect(
    options,
    `GET ${DATA_ROUTE} did not answer with the extension's stored data`,
  ).not.toBeNull();
  return options;
}

// The member the backend serializes its refusal aggregate under. PascalCase, matching the C# record,
// and pinned by the backend's own test.
const REFUSALS = "ImportRefusals";

/** The refusal aggregate the extension has stored, read through Cove's own bulk data route. */
export async function importRefusals(api) {
  return (await storedOptions(api))[REFUSALS] ?? [];
}

/** One root's stored refusal line, or undefined while that root has none. */
export const refusalLineFor = (refusals, root) => refusals.find((entry) => entry.Root === root);

/** Rewrites the stored options blob with `change` applied. */
export async function writeOptions(api, change) {
  const written = await api.put(`${DATA_ROUTE}/${OPTIONS_KEY}`, JSON.stringify(change));
  expect(written.status, `PUT the options key answered: ${written.text.slice(0, 300)}`).toBe(200);
}

/**
 * Stops and starts the extension, which is how a spec makes the background worker run again without
 * waiting out its interval.
 */
export async function restartWorker(api) {
  const disabled = await api.post(DISABLE_ROUTE);
  expect(
    disabled.status,
    `POST ${DISABLE_ROUTE} answered: ${String(disabled.text).slice(0, 300)}`,
  ).toBe(200);
  const enabled = await api.post(ENABLE_ROUTE);
  expect(
    enabled.status,
    `POST ${ENABLE_ROUTE} answered: ${String(enabled.text).slice(0, 300)}`,
  ).toBe(200);
}

/** Where each version carries the file it delivered, and where it carries that scene's identity. */
const DELIVERY_SHAPE = {
  v2: { file: "episodeFile", identity: (body) => body.episodes[0], member: "tvdbId" },
  v3: { file: "movieFile", identity: (body) => body.movie, member: "stashId" },
};

/**
 * The delivery a real instance of `version` sent, with only what the caller names rewritten.
 *
 * Every other member stays exactly what Whisparr delivers: a body assembled by hand would exercise a
 * shape nothing sends, which is what the capture exists to prevent.
 *
 * `remoteId` replaces the identity the capture carries; `identified: false` removes it, which is how
 * a spec reaches the candidate that names no scene.
 */
export function deliveryNaming(version, { path, size, remoteId, identified = true }) {
  const shape = DELIVERY_SHAPE[version];
  if (shape === undefined) {
    throw new Error(
      `deliveryNaming: no capture is declared for "${version}"; declared are ${Object.keys(DELIVERY_SHAPE).join(", ")}.`,
    );
  }

  const body = JSON.parse(readFileSync(CAPTURED_DELIVERY[version], "utf8"));
  body[shape.file].path = path;
  body[shape.file].size = size;

  if (!identified) {
    delete shape.identity(body)[shape.member];
  } else if (remoteId !== undefined) {
    shape.identity(body)[shape.member] = remoteId;
  }
  return body;
}

/** The identifier the captured delivery for `version` names, where that version carries it. */
export function deliveredRemoteId(version) {
  const shape = DELIVERY_SHAPE[version];
  const body = JSON.parse(readFileSync(CAPTURED_DELIVERY[version], "utf8"));
  return String(shape.identity(body)[shape.member]);
}
