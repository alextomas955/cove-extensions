// Drives cove-providers.mjs without depending on the machine's real Cove install: the parse half
// takes its document as a string, and the lift half is pointed at an empty directory through
// COVE_HOME.
import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  PLACEHOLDER_SERVERS,
  coveDataRoot,
  describeServers,
  liftMetadataServers,
  parseMetadataServers,
  placeholderProviderEnv,
  providerEnv,
} from "./cove-providers.mjs";

/** Runs `body` with `overrides` applied to process.env, restoring every touched key afterwards. */
function withEnv(overrides, body) {
  const saved = new Map();
  for (const [key, value] of Object.entries(overrides)) {
    saved.set(key, process.env[key]);
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  }
  try {
    return body();
  } finally {
    for (const [key, value] of saved) {
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
  }
}

const DOCUMENT = JSON.stringify({
  scraping: {
    metadataServers: [
      {
        endpoint: "https://stashdb.example/graphql",
        apiKey: "aaaa-bbbb",
        name: "stashdb",
        maxRequestsPerMinute: 240,
      },
      {
        endpoint: "https://theporndb.example/graphql",
        apiKey: "cccc",
        name: "ThePornDB",
        maxRequestsPerMinute: 60,
      },
    ],
  },
});

test("coveDataRoot takes COVE_HOME when it is set, trimmed", () => {
  withEnv({ COVE_HOME: "  C:/probe/cove-home  " }, () => {
    assert.equal(coveDataRoot(), "C:/probe/cove-home");
  });
});

test("coveDataRoot falls back to the local application data directory", () => {
  withEnv({ COVE_HOME: undefined, LOCALAPPDATA: "C:/probe/lad" }, () => {
    assert.equal(coveDataRoot(), join("C:/probe/lad", "cove"));
  });
  withEnv({ COVE_HOME: "   ", LOCALAPPDATA: "C:/probe/lad" }, () => {
    assert.equal(coveDataRoot(), join("C:/probe/lad", "cove"));
  });
});

test("a data root holding no cove-config.json is a named skip, not a throw", () => {
  const empty = mkdtempSync(join(tmpdir(), "cove-providers-"));
  withEnv({ COVE_HOME: empty }, () => {
    const lifted = liftMetadataServers();
    assert.deepEqual(lifted.servers, []);
    assert.match(lifted.skip, /no cove-config\.json/);
    assert.ok(
      lifted.skip.includes(empty),
      `skip should name the path it looked in: ${lifted.skip}`,
    );
  });
});

test("a document with no scraping.metadataServers is a named skip", () => {
  const lifted = parseMetadataServers("{}", { path: "P" });
  assert.deepEqual(lifted.servers, []);
  assert.match(lifted.skip, /scraping\.metadataServers/);
  assert.ok(lifted.skip.includes("P"));
});

test("a document that is not JSON is a named skip", () => {
  const lifted = parseMetadataServers("{not json", { path: "P" });
  assert.deepEqual(lifted.servers, []);
  assert.ok(lifted.skip.includes("P"));
});

test("a document carrying metadata servers returns them with no skip", () => {
  const lifted = parseMetadataServers(DOCUMENT, { path: "P" });
  assert.equal(lifted.skip, null);
  assert.deepEqual(
    lifted.servers.map((server) => server.name),
    ["stashdb", "ThePornDB"],
  );
});

test("a requested provider the document does not carry is named in the skip", () => {
  const lifted = parseMetadataServers(DOCUMENT, { path: "P", names: ["ThePornDB", "FansDB"] });
  assert.deepEqual(
    lifted.servers.map((server) => server.name),
    ["ThePornDB"],
  );
  assert.ok(lifted.skip.includes("FansDB"), `skip should name the absent provider: ${lifted.skip}`);
  assert.ok(!lifted.skip.includes("ThePornDB"), `skip should not name a provider it found`);
});

test("providerEnv writes the four keys per entry, indexed from zero", () => {
  const env = providerEnv(parseMetadataServers(DOCUMENT, { path: "P" }).servers);
  assert.deepEqual(Object.keys(env).sort(), [
    "COVE__Scraping__MetadataServers__0__ApiKey",
    "COVE__Scraping__MetadataServers__0__Endpoint",
    "COVE__Scraping__MetadataServers__0__MaxRequestsPerMinute",
    "COVE__Scraping__MetadataServers__0__Name",
    "COVE__Scraping__MetadataServers__1__ApiKey",
    "COVE__Scraping__MetadataServers__1__Endpoint",
    "COVE__Scraping__MetadataServers__1__MaxRequestsPerMinute",
    "COVE__Scraping__MetadataServers__1__Name",
  ]);
  assert.equal(env.COVE__Scraping__MetadataServers__0__Name, "stashdb");
  assert.equal(
    env.COVE__Scraping__MetadataServers__1__Endpoint,
    "https://theporndb.example/graphql",
  );
  assert.equal(env.COVE__Scraping__MetadataServers__1__MaxRequestsPerMinute, "60");
});

test("providerEnv defaults an absent rate limit and stringifies it", () => {
  const env = providerEnv([{ endpoint: "e", apiKey: "k", name: "n" }]);
  assert.equal(env.COVE__Scraping__MetadataServers__0__MaxRequestsPerMinute, "240");
});

test("placeholderProviderEnv configures a StashDB and a ThePornDB entry with synthetic keys", () => {
  const env = placeholderProviderEnv();
  assert.deepEqual(Object.keys(env).sort(), Object.keys(providerEnv(PLACEHOLDER_SERVERS)).sort());
  assert.deepEqual(
    PLACEHOLDER_SERVERS.map((server) => server.name),
    ["stashdb", "ThePornDB"],
  );
  for (const server of PLACEHOLDER_SERVERS) {
    assert.match(server.apiKey, /placeholder/);
  }
});

test("describeServers reports the key's character count and never its characters", () => {
  const theKey = "zzzz-secret-key-value-zzzz";
  const described = describeServers([
    { endpoint: "https://stashdb.example/graphql", apiKey: theKey, name: "stashdb" },
  ]);
  assert.equal(described[0].endpoint, "https://stashdb.example/graphql");
  assert.equal(described[0].name, "stashdb");
  assert.equal(described[0].maxRequestsPerMinute, 240);
  assert.deepEqual(described[0].apiKey, { present: true, chars: theKey.length });
  assert.equal(JSON.stringify(described).includes(theKey), false);
});

test("describeServers reports an absent key as absent with a zero count", () => {
  assert.deepEqual(describeServers([{ endpoint: "e", name: "n" }])[0].apiKey, {
    present: false,
    chars: 0,
  });
});
