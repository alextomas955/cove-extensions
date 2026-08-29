// What each Whisparr generation accepts when a caller registers to receive a webhook: the settings
// it offers, what a second registration under an existing name does, whether a secret can travel out
// of band, and which triggers exist.
//
// Absence is the finding here, so every field the ledger names is recorded present OR absent. A list
// of the fields that exist does not say that one is missing, and the missing one is the whole point:
// it is what makes the confidentiality of a callback secret a per-generation question.
//
// The delivery of a header is asserted from the listener's own capture and never from the
// registration's echo. A field a schema declares and a save accepts, but the build then does not
// send, would answer the echo exactly as a working one does.
const SCHEMA_PATH = "/api/v3/notification/schema";
const NOTIFICATION_PATH = "/api/v3/notification";

// The fields the ledger asks about by name. Recorded present or absent on both generations, so the
// one that exists on only one of them is a row in the table rather than a gap in it.
const LEDGER_FIELDS = ["url", "method", "username", "password", "headers"];

// Obviously synthetic, and they authorise nothing. A capture is written to a record that outlives
// the run, so nothing resembling a credential may enter the request in the first place.
const SYNTHETIC_HEADER_NAME = "X-Cove-Probe-Secret";
const SYNTHETIC_HEADER_VALUE = "row04-not-a-real-secret";

const CAPTURE_TIMEOUT_MS = 60_000;

const triggerFlagNames = (webhook) =>
  Object.entries(webhook)
    .filter(([key, value]) => typeof value === "boolean" && !key.startsWith("supports"))
    .map(([key]) => key);

/**
 * Every trigger enabled except the ones a running instance raises by itself.
 *
 * At least one must be on or the connection is disabled and the save sends nothing, which would
 * leave the header question unanswerable. Health and update triggers stay off because an instance
 * fires those on its own schedule, and a delivery this row did not cause is one it would have to
 * tell apart from the one it did.
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
      `row-04-notification-semantics: GET ${SCHEMA_PATH} on ${generation} answered ${response.status} ${response.contentType} and declared no Webhook entry.`,
    );
  }
  return { status: response.status, webhook };
}

/** A validation response's shape and the fields a caller would branch on — never the whole body. */
function refusalShape(response) {
  const first = Array.isArray(response.json) ? response.json[0] : null;
  return {
    shape: Array.isArray(response.json)
      ? `array[${response.json.length}]`
      : typeof response.json === "object" && response.json !== null
        ? "object"
        : "non-json",
    entryKeys: first === null ? null : Object.keys(first),
    propertyName: first?.propertyName ?? null,
    errorMessage: first?.errorMessage ?? null,
    errorCode: first?.errorCode ?? null,
  };
}

const listLength = async (api) => (await api.get(NOTIFICATION_PATH)).json?.length ?? null;

export const row = {
  id: "row-04-notification-semantics",
  label: "How a callback is registered on each generation, and the capability one of them lacks",
  requires: {
    // Whisparr and the listener both join the Cove instance's own network, so a row asking for
    // either asks for cove too.
    cove: true,
    whisparr: ["v3", "v2"],
    seedHistory: false,
    support: ["webhook-listener"],
    network: false,
    live: false,
  },
  async run(ctx) {
    const listener = ctx.support["webhook-listener"];
    const generations = {};

    for (const generation of ["v3", "v2"]) {
      const api = ctx.whisparr.apiFor(generation);
      const { status: schemaStatus, webhook } = await readWebhookSchema(api, generation);
      const declared = new Map(webhook.fields.map((field) => [field.name, field]));
      const carriesHeaders = declared.has("headers");
      const name = `cove-probe-row04-${generation}`;
      const at = (leaf) => listener.url(`/row04/${generation}/${leaf}`);

      const settings = Object.fromEntries(
        LEDGER_FIELDS.map((field) => [
          field,
          declared.has(field)
            ? {
                present: true,
                type: declared.get(field).type,
                privacy: declared.get(field).privacy,
                advanced: declared.get(field).advanced,
              }
            : { present: false, type: null, privacy: null, advanced: null },
        ]),
      );

      const triggers = enabledTriggers(webhook);
      const registration = {
        ...triggers,
        name,
        implementation: webhook.implementation,
        implementationName: webhook.implementationName,
        configContract: webhook.configContract,
        tags: [],
        fields: [
          { name: "url", value: at("create") },
          { name: "method", value: 1 },
          ...(carriesHeaders
            ? [
                {
                  name: "headers",
                  value: [{ key: SYNTHETIC_HEADER_NAME, value: SYNTHETIC_HEADER_VALUE }],
                },
              ]
            : []),
        ],
      };

      const lengthBefore = await listLength(api);
      const created = await api.post(NOTIFICATION_PATH, registration);
      if (created.status !== 201) {
        throw new Error(
          `row-04-notification-semantics: POST ${NOTIFICATION_PATH} on ${generation} answered ${created.status} ${created.contentType}: ${created.text.slice(0, 400)}.`,
        );
      }
      const lengthAfterCreate = await listLength(api);
      const [capture] = await listener.waitForCaptures(1, {
        timeoutMs: CAPTURE_TIMEOUT_MS,
        match: (delivery) => delivery.path === `/row04/${generation}/create`,
      });

      const duplicate = await api.post(NOTIFICATION_PATH, {
        ...registration,
        fields: [
          { name: "url", value: at("duplicate") },
          { name: "method", value: 1 },
        ],
      });
      const lengthAfterDuplicate = await listLength(api);

      const listed = await api.get(NOTIFICATION_PATH);
      const mine = listed.json?.find?.((entry) => entry.name === name);
      if (mine === undefined) {
        throw new Error(
          `row-04-notification-semantics: GET ${NOTIFICATION_PATH} on ${generation} answered ${listed.status} and carried no entry named "${name}", so the find-then-PUT half cannot be measured.`,
        );
      }
      const updated = await api.put(`${NOTIFICATION_PATH}/${mine.id}`, {
        ...mine,
        fields: mine.fields.map((field) =>
          field.name === "url" ? { ...field, value: at("put") } : field,
        ),
      });
      await listener.waitForCaptures(1, {
        timeoutMs: CAPTURE_TIMEOUT_MS,
        match: (delivery) => delivery.path === `/row04/${generation}/put`,
      });

      // The PUT's own delivery has arrived by here, so a delivery the refused duplicate had made
      // would have arrived too. That is what bounds this absence.
      const everything = await listener.captures();
      const deliveredHeader = Object.entries(capture.headers).find(
        ([header]) => header.toLowerCase() === SYNTHETIC_HEADER_NAME.toLowerCase(),
      );

      generations[generation] = {
        schemaStatus,
        settings,
        triggerFlags: triggerFlagNames(webhook),
        idempotency: {
          create: { status: created.status, url: at("create") },
          duplicateSameNameDifferentUrl: {
            status: duplicate.status,
            contentType: duplicate.contentType,
            ...refusalShape(duplicate),
          },
          listLength: {
            before: lengthBefore,
            afterCreate: lengthAfterCreate,
            afterRefusedDuplicate: lengthAfterDuplicate,
            unchangedByRefusal: lengthAfterCreate === lengthAfterDuplicate,
          },
          findThenPut: { status: updated.status, foundBy: "name", url: at("put") },
          refusedDuplicateDelivered: everything.some(
            (delivery) => delivery.path === `/row04/${generation}/duplicate`,
          ),
          pattern:
            "GET the list, find the product's own registration by name, PUT it; POST only when absent. Name uniqueness is enforced by the server, so an extension does not reimplement it.",
        },
        outOfBandSecret: carriesHeaders
          ? {
              field: "headers",
              sent: { header: SYNTHETIC_HEADER_NAME, value: SYNTHETIC_HEADER_VALUE },
              // Read off the inbound request, never off the registration's echo.
              deliveredOnInboundRequest: deliveredHeader !== undefined,
              deliveredValue: deliveredHeader?.[1] ?? null,
              valueMatchesWhatWasSet: deliveredHeader?.[1] === SYNTHETIC_HEADER_VALUE,
              capturedAt: capture.ts,
            }
          : {
              field: null,
              absent: "headers",
              alternatives: [
                "the URL query string, which intermediaries do log",
                "username/password, which the connection sends as Basic auth",
              ],
              consequence:
                "A callback secret cannot travel on this generation in a way an intermediary does not log.",
            },
      };
    }

    const onlyOn = (mine, theirs) => mine.filter((flag) => !theirs.includes(flag));

    return {
      method: {
        verb: "GET",
        path: `${SCHEMA_PATH}, then ${NOTIFICATION_PATH} under POST, POST again and PUT`,
        inputs: { syntheticHeader: SYNTHETIC_HEADER_NAME },
      },
      verdict: "semantics-recorded",
      observed: {
        v3: generations.v3,
        v2: generations.v2,
        triggerFlagAsymmetry: {
          note: "An extension must set the right set per generation. One set shared across both silently under-subscribes on whichever generation carries a trigger the other does not.",
          onlyOnV3: onlyOn(generations.v3.triggerFlags, generations.v2.triggerFlags),
          onlyOnV2: onlyOn(generations.v2.triggerFlags, generations.v3.triggerFlags),
          sharedCount: generations.v3.triggerFlags.filter((flag) =>
            generations.v2.triggerFlags.includes(flag),
          ).length,
        },
        gapCandidate: {
          id: "GAP-1",
          observed: {
            date: new Date().toISOString().slice(0, 10),
            builds: {
              v3: ctx.builds.whisparr.v3.version,
              v2: ctx.builds.whisparr.v2.version,
            },
          },
          axis: "generation",
          surface: `the Webhook connection's "headers" settings field, as declared by ${SCHEMA_PATH}`,
          expected:
            "HOOK-3: the callback secret travels in a way an intermediary does not log, on whichever generation an instance runs.",
          observedBehaviour: `v3 declares "headers" (${generations.v3.settings.headers.type}, advanced) and delivers the header on the inbound request; v2 declares no such field, leaving only the URL query string or Basic auth.`,
          blastRadius:
            "HOOK-3 acceptance on v2, and any later test that would assert one confidentiality property across both generations.",
          acceptanceAdjustment:
            "On v2, assert that the secret arrives and that the product states the reduced confidentiality; assert the header's delivery only on v3. The bound is that v2's registration carries the secret in the URL or as Basic auth and the extension says so, not that the assertion is dropped.",
          recheckTrigger:
            "any bump of the v2 reference in lib/whisparr-images.mjs; the row is re-answered by re-running it, and a v2 schema that grows a headers field closes the gap.",
          axisNote:
            "generation, not build: this is permanent for v2, so it becomes a capability that is simply not registered on that generation rather than a limitation a later image may lift.",
        },
      },
    };
  },
};
