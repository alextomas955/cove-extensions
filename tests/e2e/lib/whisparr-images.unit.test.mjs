// Pins the shape of the declared image references. It touches no network and no container: what it
// guards is the DECLARATION, so a floating tag or a typo goes red here rather than at whichever
// container boot first pulls the wrong generation.
import { test } from "node:test";
import assert from "node:assert/strict";
import { WHISPARR_IMAGES, whisparrImage } from "./whisparr-images.mjs";

// Transcribed by hand from the reference form the registry publishes, never composed from the module
// under test: an expectation derived from its own subject agrees with it forever and reports nothing.
const RELEASE_REFERENCE = /^ghcr\.io\/hotio\/whisparr:v[23]-\d+\.\d+\.\d+-release\.\d+$/;

const FLOATING_TAGS = [":latest", ":v2", ":v3"];

test("each generation declares a pinned release-channel reference", () => {
  for (const generation of ["v3", "v2"]) {
    assert.match(whisparrImage(generation), RELEASE_REFERENCE);
  }
});

test("an unknown generation throws rather than answering undefined", () => {
  assert.throws(() => whisparrImage("v4"), /unknown generation/);
});

test("no declared reference carries a floating tag", () => {
  for (const reference of Object.values(WHISPARR_IMAGES)) {
    for (const floating of FLOATING_TAGS) {
      assert.ok(!reference.endsWith(floating), `${reference} ends in the floating tag ${floating}`);
    }
  }
});

test("both references name the same repository", () => {
  const repositories = new Set(
    Object.values(WHISPARR_IMAGES).map((reference) =>
      reference.slice(0, reference.lastIndexOf(":")),
    ),
  );
  assert.equal(
    repositories.size,
    1,
    `expected one repository, got ${[...repositories].join(", ")}`,
  );
});
