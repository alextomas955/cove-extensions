// Behavior coverage for the catalog-driven wire-type generator. Every case drives a fixture
// extensions/catalog.json in a temp dir and injects the document-to-types step, so this file needs
// nothing installed — which is the property the catalog-validation job depends on: that job installs
// no dependencies at all and runs every scripts/*.test.mjs.
//
// The fixture entry's names deliberately differ from any real extension's, so a passing case proves
// the resolution came from the catalog rather than from a name baked into the generator.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { generateWireTypes } from "./generate-wire-types.mjs";

const ID = "com.example.fixture";
const NAME = "Fixture";
const EXT_PATH = "extensions/" + NAME;
const UI_PATH = EXT_PATH + "/src/" + NAME + ".Ui";
const DOC_PATH = EXT_PATH + "/wire/openapi.json";

function fixtureRoot({ entry = {}, writeDocument = true } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "generate-wire-types-"));
  const catalogEntry = {
    name: NAME,
    id: ID,
    path: EXT_PATH,
    uiPath: UI_PATH,
    wireDocumentPath: DOC_PATH,
    ...entry,
  };
  fs.mkdirSync(path.join(root, "extensions"), { recursive: true });
  fs.writeFileSync(
    path.join(root, "extensions", "catalog.json"),
    JSON.stringify({ schemaVersion: 1, extensions: [catalogEntry] }, null, 2),
  );
  if (writeDocument) {
    fs.mkdirSync(path.join(root, EXT_PATH, "wire"), { recursive: true });
    fs.writeFileSync(path.join(root, DOC_PATH), JSON.stringify({ openapi: "3.1.1", paths: {} }));
  }
  return root;
}

function recordingGenerate() {
  const calls = [];
  return {
    calls,
    generate: (step) => {
      calls.push(step);
      return Promise.resolve();
    },
  };
}

function silent() {
  const lines = [];
  return { lines, log: (line) => lines.push(line) };
}

test("a declaring entry is generated from, with the document as input and the UI's wire module as output", async () => {
  const root = fixtureRoot();
  try {
    const { calls, generate } = recordingGenerate();
    const { lines, log } = silent();

    const result = await generateWireTypes({ root, generate, log });

    assert.equal(result.ok, true);
    assert.deepEqual(result.failures, []);
    assert.equal(calls.length, 1);

    // The ARGUMENTS, not merely that it was called: a test that counts calls passes just as happily
    // when the generator is pointed at the wrong document or writes to the wrong path, which is
    // exactly the wiring mistake this injection seam could otherwise introduce.
    assert.equal(calls[0].documentPath, path.join(root, DOC_PATH));
    assert.equal(calls[0].outputPath, path.join(root, UI_PATH, "src", "wire", "api.ts"));
    assert.deepEqual(calls[0].flags, [
      "--root-types",
      "--root-types-no-schema-prefix",
      "--root-types-keep-casing",
    ]);
    assert.equal(calls[0].entry.id, ID);

    assert.match(lines.join("\n"), /Examined 1 catalog entry; generated from 1 of 1/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("a catalog whose entries declare no wireDocumentPath says so rather than exiting quietly", async () => {
  const root = fixtureRoot({ entry: { wireDocumentPath: undefined } });
  try {
    const { calls, generate } = recordingGenerate();
    const { lines, log } = silent();

    const result = await generateWireTypes({ root, generate, log });

    assert.equal(result.ok, true);
    assert.equal(calls.length, 0);
    assert.match(lines.join("\n"), /no entry declares both wireDocumentPath and uiPath/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("an entry declaring a UI but no wireDocumentPath is not generated from", async () => {
  const root = fixtureRoot({ entry: { wireDocumentPath: undefined }, writeDocument: false });
  try {
    const { calls, generate } = recordingGenerate();
    const result = await generateWireTypes({ root, generate, log: () => {} });

    assert.equal(result.ok, true);
    assert.equal(calls.length, 0);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("a declared document that does not exist fails, naming the entry and the path", async () => {
  const root = fixtureRoot({ writeDocument: false });
  try {
    const { calls, generate } = recordingGenerate();
    const result = await generateWireTypes({ root, generate, log: () => {} });

    assert.equal(result.ok, false);
    assert.equal(calls.length, 0);
    assert.equal(result.failures.length, 1);
    assert.match(result.failures[0], new RegExp(ID));
    assert.match(result.failures[0], /extensions\/Fixture\/wire\/openapi\.json/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("--extension scopes the run to one entry, and an unknown id is a failure", async () => {
  const root = fixtureRoot();
  try {
    const scoped = recordingGenerate();
    const hit = await generateWireTypes({
      root,
      extension: ID,
      generate: scoped.generate,
      log: () => {},
    });
    assert.equal(hit.ok, true);
    assert.equal(scoped.calls.length, 1);

    const missed = recordingGenerate();
    const miss = await generateWireTypes({
      root,
      extension: "com.example.absent",
      generate: missed.generate,
      log: () => {},
    });
    assert.equal(miss.ok, false);
    assert.equal(missed.calls.length, 0);
    assert.match(miss.failures[0], /no catalog entry matches id\/name: com\.example\.absent/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
