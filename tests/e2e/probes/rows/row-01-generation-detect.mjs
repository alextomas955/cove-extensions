// Settles how a consumer tells the two Whisparr generations apart before it talks to either, and
// records the two refusals that establish the key is what authorised the read.
//
// `AuthenticationMethod: None` does not open the API: both generations refuse a keyless read and a
// wrong-key read alike.
//
// One generation answers an unmatched path with its single-page frontend rather than a miss, so a
// status alone establishes nothing about what answered.
import { createApiClient } from "../../lib/apiClient.mjs";

const STATUS_PATH = "/api/v3/system/status";

// Well-formed and belonging to no instance, so a refusal of it is about the key's value rather than
// its shape.
const WRONG_KEY = "ffffffffffffffffffffffffffffffff";

// The fields read off the authorised body. The ones that agree across the generations are recorded
// too, so a reader can see they were checked.
const STATUS_FIELDS = [
  "version",
  "branch",
  "packageVersion",
  "runtimeVersion",
  "runtimeName",
  "migrationVersion",
  "authentication",
  "databaseType",
  "databaseVersion",
];

// These count library contents, so only their PRESENCE is recorded: a value here grows with the
// library.
const COUNT_FIELDS = ["movieCount", "sceneCount", "performerCount", "studioCount"];

// This row starts no listener, so the header a webhook consumer sees is out of its reach.
const INBOUND_USER_AGENT = {
  observedHere: false,
  reason: "This row makes outbound calls only; nothing here receives a request from an instance.",
  observedBy: "the webhook-payload row, which captures the headers a notification arrives with",
};

const observeResponse = (response) => ({
  status: response.status,
  contentType: response.contentType,
});

/**
 * The generation a status body claims to be, from the version's major alone.
 *
 * @returns {string} a `v<major>` label, or `unknown` when the version carries no leading major.
 */
function detectGeneration(version) {
  const major = /^(\d+)\./.exec(String(version ?? ""))?.[1];
  return major === undefined ? "unknown" : `v${major}`;
}

async function probeGeneration(ctx, generation) {
  const baseUrl = () => ctx.whisparr[generation].baseUrl;
  const keyed = await ctx.whisparr.apiFor(generation).get(STATUS_PATH);
  const keyless = await createApiClient(baseUrl).get(STATUS_PATH);
  const wrongKey = await createApiClient(baseUrl, undefined, {
    headers: { "X-Api-Key": WRONG_KEY },
  }).get(STATUS_PATH);

  const body = keyed.json ?? {};
  return {
    image: ctx.builds.whisparr[generation]?.image ?? null,
    requested: generation,
    detected: detectGeneration(body.version),
    observations: {
      keyed: observeResponse(keyed),
      keyless: observeResponse(keyless),
      wrongKey: observeResponse(wrongKey),
    },
    fields: Object.fromEntries(STATUS_FIELDS.map((field) => [field, body[field] ?? null])),
    countFieldsPresent: Object.fromEntries(
      COUNT_FIELDS.map((field) => [field, Object.hasOwn(body, field)]),
    ),
    appName: body.appName ?? null,
  };
}

export const row = {
  id: "row-01-generation-detect",
  label: "Which fields of GET /api/v3/system/status tell the two Whisparr generations apart",
  requires: {
    // Whisparr containers join the Cove instance's own network, so a row asking for one asks for both.
    cove: true,
    whisparr: ["v3", "v2"],
    seedHistory: false,
    support: [],
    network: false,
    live: false,
  },
  async run(ctx) {
    const generations = {};
    for (const generation of ctx.whisparr.generations) {
      generations[generation] = await probeGeneration(ctx, generation);
    }

    const appNames = Object.values(generations).map((observed) => observed.appName);
    const discriminated = Object.values(generations).every(
      (observed) => observed.detected === observed.requested,
    );

    return {
      method: {
        verb: "GET",
        path: STATUS_PATH,
        inputs: { keys: ["seeded", "none", "wrong"] },
      },
      verdict: discriminated ? "discriminated" : "not-discriminated",
      observed: {
        discriminator:
          "the version's major, corroborated by branch and by whether the count fields are present",
        appName: {
          values: appNames,
          identicalAcrossGenerations: new Set(appNames).size === 1,
          discriminates: false,
          note: "appName carries the same value on both generations, so it must not be used to tell them apart.",
        },
        generations,
        inboundUserAgent: INBOUND_USER_AGENT,
      },
    };
  },
};
