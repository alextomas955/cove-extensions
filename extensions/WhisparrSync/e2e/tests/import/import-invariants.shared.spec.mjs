// The shared secret, proven against a live instance in each position it is accepted in, and against
// each way of presenting none.
//
// One scenario, collected once per generation, each execution against an installation of its own.
// The secret is checked before the body is parsed at all, so the check is the same lines on either
// generation - but what a rejection has to be measured against is the ingest that would otherwise
// have run, and that is this generation's own.
//
// Neither generation signs a callback, so this secret is the whole of the inbound authentication.
// What is asserted is therefore the COVE side of each delivery - whether an item exists afterwards
// and where the extension recorded the secret as having travelled - rather than the status the route
// answered. A route that answered 401 while still importing would satisfy a status assertion and
// none of the claim.
//
// The instance this runs against has its own authentication off, which is the deployment the inbound
// handler's remark is about. That is measured here and not assumed, and it is what makes each
// rejection below this product's own: nothing in front of the route refused anything.
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { randomUUID } from "node:crypto";

import { expect, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import {
  CALLBACK_ROUTE,
  CALLBACK_STATUS_ROUTE,
  COVE_ROOT,
  EXTENSION_ID,
  SECRET_HEADER,
  SECRET_QUERY_PARAMETER,
  WHISPARR_ROOT,
} from "../../lib/contract.mjs";
import { deliveryNaming, videoPathsIn, whisparrCaller } from "../../lib/steps.mjs";

// The wire spellings of where a delivery carried its secret, transcribed from the enum the server
// declares them on.
const OUT_OF_BAND = "outOfBand";
const IN_ADDRESS = "address";

const REJECTION_SETTLE_MS = 15_000;
const IMPORT_BUDGET_MS = 120_000;

/** The callback as the page reads it: this installation's secret, and where the last event carried it. */
async function callbackStatus(api) {
  const status = await api.get(`${CALLBACK_STATUS_ROUTE}?_=${randomUUID()}`);
  expect(status.status, `GET ${CALLBACK_STATUS_ROUTE} answered: ${status.text.slice(0, 300)}`).toBe(
    200,
  );
  return status.json;
}

test.describe.configure({ timeout: SPEC_BUDGET_MS });

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    test.use({ generation });

    test("the shared secret is the whole of the inbound authentication, in each position it is accepted", async ({
      isolatedCove,
      connected,
    }) => {
      const { api } = connected;

      const copyable = (await callbackStatus(api)).copyableAddress;
      const shared = new URL(copyable).searchParams.get(SECRET_QUERY_PARAMETER);
      expect(shared, `the copyable address carried no ${SECRET_QUERY_PARAMETER}`).toBeTruthy();

      // The discriminating control, and the reason each rejection below is this product's own: this
      // instance answers an unauthenticated in-network caller, so nothing in front of the route
      // refused anything. It is the deployment the inbound handler's own remark describes, measured
      // here rather than assumed.
      const anonymous = whisparrCaller(() => isolatedCove.baseUrl, connected.generation);
      const anonymousRead = await anonymous.get("/api/extensions");
      expect(
        anonymousRead.ok,
        "this instance refused an unauthenticated read, so a rejection below could be the request pipeline's answer rather than this product's",
      ).toBe(true);
      expect(
        anonymousRead.text,
        "the unauthenticated read named no extension, so it does not show what the caller below could reach",
      ).toContain(EXTENSION_ID);

      /** Places one file under the Cove root and answers with what a delivery would report for it. */
      async function place() {
        const tail = `whisparr/${randomUUID()}.mp4`;
        const covePath = await placeVideoUnregistered({
          container: isolatedCove.container,
          destPath: `${COVE_ROOT}/${tail}`,
        });
        const { output } = await isolatedCove.exec(["stat", "-c", "%s", covePath]);
        const size = Number(output.trim());
        expect(Number.isInteger(size) && size > 0, `stat reported ${output.trim()}`).toBe(true);
        return { reportedPath: `${WHISPARR_ROOT}/${tail}`, covePath, size };
      }

      /** Posts one delivery presenting `headers` and `query`, as a caller on the network would. */
      async function deliver(file, { headers = {}, query = "" } = {}) {
        const caller = whisparrCaller(() => isolatedCove.baseUrl, connected.generation, {
          headers,
        });
        await caller.post(
          `${CALLBACK_ROUTE}${query}`,
          deliveryNaming(connected.generation, {
            path: file.reportedPath,
            size: file.size,
            remoteId: randomUUID(),
          }),
        );
      }

      // ---- 1. no secret at all ----
      const refused = await place();
      expect(
        await videoPathsIn(api),
        "Cove already held a video before the delivery presenting no secret",
      ).toEqual([]);

      await deliver(refused);

      // Long enough that an ingest which HAD run would have registered the file, so the emptiness is a
      // fact about the refusal rather than about how soon the read was taken.
      await new Promise((settle) => setTimeout(settle, REJECTION_SETTLE_MS));
      expect(
        await videoPathsIn(api),
        "a delivery presenting no secret registered the file it named",
      ).toEqual([]);

      // ---- 2. a wrong secret ----
      expect(
        await videoPathsIn(api),
        "Cove already held a video before the delivery presenting a wrong secret",
      ).toEqual([]);

      await deliver(refused, { headers: { [SECRET_HEADER]: `not-${shared}` } });

      await new Promise((settle) => setTimeout(settle, REJECTION_SETTLE_MS));
      expect(
        await videoPathsIn(api),
        "a delivery presenting a wrong secret registered the file it named",
      ).toEqual([]);

      // Neither rejected delivery reached the point where the position is recorded.
      expect(
        (await callbackStatus(api)).lastEventSecretPosition ?? null,
        "a rejected delivery was recorded as having delivered",
      ).toBeNull();

      // ---- 3. the correct secret, out of band ----
      const outOfBand = await place();
      await deliver(outOfBand, { headers: { [SECRET_HEADER]: shared } });

      const afterOutOfBand = await pollUntil(
        () => videoPathsIn(api),
        (paths) => paths.length > 0,
        {
          timeoutMs: IMPORT_BUDGET_MS,
          intervalMs: 1_000,
          label: "Cove to hold the video the out-of-band delivery caused",
        },
      );
      expect(
        afterOutOfBand,
        "the accepted delivery registered something other than the file it named",
      ).toEqual([outOfBand.covePath]);
      expect(
        (await callbackStatus(api)).lastEventSecretPosition,
        "the accepted delivery was not recorded as having carried its secret out of band",
      ).toBe(OUT_OF_BAND);

      // The file the two rejected deliveries named is still not in the library. Without this, the
      // emptiness above could have been a delivery that arrived late.
      expect(
        afterOutOfBand,
        "the file a rejected delivery named was registered after all",
      ).not.toContain(refused.covePath);

      // ---- 4. the correct secret, in the address ----
      const inAddress = await place();
      await deliver(inAddress, {
        query: `?${SECRET_QUERY_PARAMETER}=${encodeURIComponent(shared)}`,
      });

      const afterAddress = await pollUntil(
        () => videoPathsIn(api),
        (paths) => paths.length > 1,
        {
          timeoutMs: IMPORT_BUDGET_MS,
          intervalMs: 1_000,
          label: "Cove to hold the video the in-address delivery caused",
        },
      );
      expect(
        afterAddress.sort(),
        "the delivery carrying its secret in the address did not register the file it named",
      ).toEqual([outOfBand.covePath, inAddress.covePath].sort());
      expect(
        (await callbackStatus(api)).lastEventSecretPosition,
        "the accepted delivery was not recorded as having carried its secret in the address",
      ).toBe(IN_ADDRESS);

      expect(
        afterAddress,
        "the file a rejected delivery named was registered after all",
      ).not.toContain(refused.covePath);
    });
  });
}
