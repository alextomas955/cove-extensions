// One authenticated delivery becomes one real Cove library item, on every generation that delivers.
//
// One scenario, collected once per generation, each execution against an installation of its own.
// The two generations do not deliver the same document - one names the file under `episodeFile`
// where the other names it under `movieFile`, and which member is read is decided from the user
// agent the instance sends - so neither execution says anything about the other.
//
// Every assertion is on the COVE side. The callback's own status says the request was well formed
// and says nothing about whether anything was registered, and this product's whole failure mode is a
// pass that read nothing, so the status is deliberately not the subject here.
//
// The delivery body is the one a real instance of the connected generation sent, read from the
// payload capture committed beside the backend tests. Only the file path and its size are rewritten,
// to name the file this spec placed; every other member is exactly what Whisparr delivers. A body
// assembled by hand would test a shape nothing sends, which is the failure the capture exists to
// prevent.
//
// A live Whisparr is part of the fixture and not decoration: neither generation's import event names
// a root folder, so the extension reads the reporting instance's declared roots off the instance
// itself. Without one running and configured there is no tail to take, and the ingest refuses.
import { createApiClient } from "@cove-extensions/e2e";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { randomUUID } from "node:crypto";

import { expect, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import {
  CALLBACK_ROUTE,
  COVE_ROOT,
  SECRET_HEADER,
  USER_AGENT,
  WHISPARR_ROOT,
} from "../../lib/contract.mjs";
import { callbackSecret, deliveryNaming, videosIn } from "../../lib/steps.mjs";

const IMPORT_BUDGET_MS = 120_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    test.use({ generation });

    test("a delivery registers the file this extension verified on disk, under the identity it named", async ({
      isolatedCove,
      connected,
    }) => {
      const { adapter, api } = connected;
      const secret = await callbackSecret(api);

      // Unique per run, so a repeat against a reused image cannot pass on a previous run's item.
      const tail = `whisparr/${randomUUID()}.mp4`;
      const covePath = await placeVideoUnregistered({
        container: isolatedCove.container,
        destPath: `${COVE_ROOT}/${tail}`,
      });

      // The negative first. Without it the assertion after the delivery could have been true all
      // along, and this spec would pass with the whole ingest deleted.
      const before = await videosIn(api);
      expect(
        before.map((video) => video.id),
        `Cove already held ${String(before.length)} video(s) before the delivery`,
      ).toEqual([]);

      // The file's real size on disk, read off the file Cove can see. The delivery reports a size
      // and the extension refuses a candidate whose length disagrees, so a spec that reported a
      // made-up one would be testing the refusal rather than the import.
      const { output } = await isolatedCove.exec(["stat", "-c", "%s", covePath]);
      const size = Number(output.trim());
      expect(
        Number.isInteger(size) && size > 0,
        `stat reported ${output.trim()} for ${covePath}`,
      ).toBe(true);

      // Its own client, carrying no Cove credential: Whisparr holds none, and the secret plus the
      // agent are the whole of what a real delivery presents.
      const asWhisparr = createApiClient(() => isolatedCove.baseUrl, undefined, {
        headers: { [SECRET_HEADER]: secret, "User-Agent": USER_AGENT[connected.generation] },
      });
      const delivered = await asWhisparr.post(
        CALLBACK_ROUTE,
        deliveryNaming(connected.generation, { path: `${WHISPARR_ROOT}/${tail}`, size }),
      );

      // A diagnostic, not the evidence. This spec's subject is what Cove holds afterwards, and a 200
      // here would say only that the request was well formed - but a refusal reported as a poll
      // timeout would name the wrong cause, so a refusal is surfaced as one.
      expect(
        delivered.status,
        `the callback refused the delivery, so nothing below is about the ingest: ${delivered.text.slice(0, 300)}`,
      ).toBeLessThan(400);

      // What the answer must NOT do is name a path or say whether a file was found. The caller is
      // anonymous, and an answer that varied with what is on disk would make this route a probe of it.
      expect(delivered.text).not.toContain(covePath);
      expect(delivered.text).not.toContain(tail);

      const registered = await pollUntil(
        () => videosIn(api),
        (videos) => videos.length > 0,
        {
          timeoutMs: IMPORT_BUDGET_MS,
          intervalMs: 1_000,
          label: "Cove to hold the video the delivery caused",
        },
      );

      expect(registered, "the delivery produced more than one item").toHaveLength(1);

      const held = await api.get(`/api/videos/${String(registered[0].id)}`);
      expect(held.status, `GET the imported video answered: ${held.text.slice(0, 300)}`).toBe(200);
      expect(
        held.json.files?.map((file) => file.path),
        "the item Cove created is not at the path this extension verified",
      ).toEqual([covePath]);

      // The identity, which is the second member the generation decides. One carries it on the first
      // episode of the delivery and the other on its movie, so an item stamped here is evidence
      // about the connected generation's own reading, under that generation's own source.
      expect(
        held.json.remoteIds,
        "the imported item does not carry the identifier this generation's delivery named",
      ).toEqual([{ endpoint: adapter.identityEndpoint, remoteId: adapter.deliveredIdentity }]);
    });
  });
}
