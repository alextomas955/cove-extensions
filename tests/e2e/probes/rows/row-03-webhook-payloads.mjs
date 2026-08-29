// What each Whisparr generation actually sends when it calls out, taken from a delivery it really
// made rather than from a schema.
//
// Saving a Webhook connection is what fires the delivery, so no event has to be provoked — but only
// while the connection is ENABLED, and a connection is enabled by its trigger flags. Saving one with
// every flag off answers the same 201 and sends nothing at all, which is the failure this row would
// otherwise report as a generation that no longer calls out.
//
// The record also carries the casing trap beside the capture: the webhook's `eventType` and the
// history API's are two vocabularies on two surfaces, and a consumer modelling them as one enum gets
// a switch whose default branch is always taken.
import { writeCompanion } from "../lib/record.mjs";

const SCHEMA_PATH = "/api/v3/notification/schema";
const NOTIFICATION_PATH = "/api/v3/notification";
const STATUS_PATH = "/api/v3/system/status";
const HISTORY_PATH = "/api/v3/history?page=1&pageSize=10";

// Obviously synthetic, and it authorises nothing. A capture is written to a record that outlives the
// run, so nothing resembling a credential may enter the request in the first place.
const SYNTHETIC_URL_SECRET = "row03-not-a-real-secret";

const CAPTURE_TIMEOUT_MS = 60_000;

// Long enough for a representative value, short enough that no single field can carry a page of
// third-party text into the record.
const VALUE_CHARS = 64;

const USER_AGENT_FORM = /^Whisparr\/(\S+) \((.+)\)$/;

/** One level of depth: what a value IS, plus its key names or one representative value. */
function shapeOf(value) {
  if (Array.isArray(value)) {
    const first = value[0];
    return {
      type: "array",
      length: value.length,
      itemKeys:
        first !== null && typeof first === "object" && !Array.isArray(first)
          ? Object.keys(first)
          : null,
    };
  }
  if (value !== null && typeof value === "object") {
    return { type: "object", keys: Object.keys(value) };
  }
  const rendered = String(value);
  return {
    type: value === null ? "null" : typeof value,
    value: rendered.length <= VALUE_CHARS ? rendered : `<${rendered.length} chars>`,
  };
}

/**
 * The connection's own trigger-flag names, read off the running schema.
 *
 * The `supports*` twins are excluded: they state what the connection CAN subscribe to, not what a
 * registration is subscribing to, and sending one back does nothing.
 */
const triggerFlagNames = (webhook) =>
  Object.entries(webhook)
    .filter(([key, value]) => typeof value === "boolean" && !key.startsWith("supports"))
    .map(([key]) => key);

/**
 * Every trigger enabled except the ones a running instance raises by itself.
 *
 * At least one must be on or the connection is disabled and the save sends nothing. Health and
 * update triggers are left off because an instance fires those on its own schedule, and a delivery
 * this row did not cause is one it would have to tell apart from the one it did.
 */
const enabledTriggers = (webhook) =>
  Object.fromEntries(
    triggerFlagNames(webhook).map((name) => [name, !/health|applicationupdate/i.test(name)]),
  );

async function readWebhookSchema(api, generation) {
  const response = await api.get(SCHEMA_PATH);
  const webhook = response.json?.find?.((entry) => entry.implementation === "Webhook");
  if (webhook === undefined) {
    throw new Error(
      `row-03-webhook-payloads: GET ${SCHEMA_PATH} on ${generation} answered ${response.status} ${response.contentType} and declared no Webhook entry, so there is nothing to register.`,
    );
  }
  return webhook;
}

export const row = {
  id: "row-03-webhook-payloads",
  label: "What each Whisparr generation sends on a webhook, captured verbatim from a real delivery",
  requires: {
    // Whisparr and the listener both join the Cove instance's own network, so a row asking for
    // either asks for cove too.
    cove: true,
    whisparr: ["v3", "v2"],
    // The history half of the eventType comparison is read from this run's own seeded rows, so both
    // vocabularies in the record are this run's own observation.
    seedHistory: true,
    support: ["webhook-listener"],
    network: false,
    live: false,
  },
  async run(ctx) {
    const listener = ctx.support["webhook-listener"];
    const generations = {};

    for (const generation of ["v3", "v2"]) {
      const api = ctx.whisparr.apiFor(generation);
      const webhook = await readWebhookSchema(api, generation);
      const triggers = enabledTriggers(webhook);
      const path = `/row03/${generation}?s=${SYNTHETIC_URL_SECRET}`;
      const name = `cove-probe-row03-${generation}`;

      const created = await api.post(NOTIFICATION_PATH, {
        ...triggers,
        name,
        implementation: webhook.implementation,
        implementationName: webhook.implementationName,
        configContract: webhook.configContract,
        tags: [],
        fields: [
          { name: "url", value: listener.url(path) },
          { name: "method", value: 1 },
        ],
      });
      if (created.status !== 201) {
        throw new Error(
          `row-03-webhook-payloads: POST ${NOTIFICATION_PATH} on ${generation} answered ${created.status} ${created.contentType}: ${created.text.slice(0, 400)}. The save is what fires the delivery this row captures.`,
        );
      }

      const [capture] = await listener.waitForCaptures(1, {
        timeoutMs: CAPTURE_TIMEOUT_MS,
        match: (delivery) => delivery.path.startsWith(`/row03/${generation}`),
      });
      let body;
      try {
        body = JSON.parse(capture.body);
      } catch (cause) {
        throw new Error(
          `row-03-webhook-payloads: ${generation} delivered ${capture.body.length} byte(s) that did not parse as JSON.`,
          { cause },
        );
      }

      const status = await api.get(STATUS_PATH);
      const userAgent = capture.headers["User-Agent"] ?? null;
      const form = userAgent === null ? null : USER_AGENT_FORM.exec(userAgent);
      const history = await api.get(HISTORY_PATH);

      generations[generation] = {
        registration: {
          status: created.status,
          name,
          url: listener.url(path),
          syntheticUrlSecret: SYNTHETIC_URL_SECRET,
          fieldsSent: ["url", "method"],
          triggerFlagsEnabled: Object.entries(triggers)
            .filter(([, enabled]) => enabled)
            .map(([flag]) => flag),
          triggerFlagsLeftOff: Object.entries(triggers)
            .filter(([, enabled]) => !enabled)
            .map(([flag]) => flag),
        },
        delivery: {
          arrivedAt: capture.ts,
          verb: capture.verb,
          path: capture.path,
          // Verbatim, and the whole set: which headers a consumer sees before it sees a body is
          // half of what this row is for.
          headers: capture.headers,
          bodyByteLength: capture.body.length,
          topLevelKeys: Object.keys(body),
          eventType: body.eventType,
          shapes: Object.fromEntries(
            Object.entries(body).map(([key, value]) => [key, shapeOf(value)]),
          ),
          savedTo:
            ctx.outDir === undefined
              ? null
              : writeCompanion(
                  ctx.outDir,
                  `webhook-test-payload-${generation}-${ctx.builds.whisparr[generation].version}.json`,
                  capture.body,
                ),
        },
        userAgent: {
          value: userAgent,
          form: "Whisparr/<version> (<distro>)",
          matchesForm: form !== null,
          version: form?.[1] ?? null,
          distro: form?.[2] ?? null,
          statusVersion: status.json?.version ?? null,
          matchesStatusVersion: form?.[1] === status.json?.version,
        },
        historyEventTypes: [
          ...new Set((history.json?.records ?? []).map((record) => record.eventType)),
        ],
      };
    }

    return {
      method: {
        verb: "POST",
        path: `${NOTIFICATION_PATH}, then the listener's own capture`,
        inputs: { listener: listener.url("/row03/<generation>"), urlSecret: SYNTHETIC_URL_SECRET },
      },
      verdict: "payloads-captured",
      observed: {
        deliveryTrigger: {
          note: "Saving an ENABLED Webhook connection fires a Test delivery immediately; no event has to be provoked. A connection with every trigger flag off is disabled, answers the same 201, and sends nothing.",
          savedStatus: Object.fromEntries(
            Object.entries(generations).map(([generation, observed]) => [
              generation,
              observed.registration.status,
            ]),
          ),
        },
        v3: generations.v3,
        v2: generations.v2,
        eventTypeVocabularies: {
          statement:
            "Two vocabularies on two surfaces. They must not be modelled as one enum: an eventType switch whose default branch is always taken is what a shared enum produces.",
          webhook: {
            surface: "the captured delivery body",
            casing: "PascalCase",
            v3: generations.v3.delivery.eventType,
            v2: generations.v2.delivery.eventType,
          },
          historyApi: {
            surface: HISTORY_PATH,
            casing: "camelCase",
            v3: generations.v3.historyEventTypes,
            v2: generations.v2.historyEventTypes,
          },
        },
        inboundUserAgent: {
          note: "A second generation discriminator, and the one an inbound consumer sees before it reads a body. Row 1 defers to this capture for it.",
          v3: generations.v3.userAgent.value,
          v2: generations.v2.userAgent.value,
        },
      },
    };
  },
};
