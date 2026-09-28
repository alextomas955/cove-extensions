#!/usr/bin/env node
// Asserts the Vite externals list names every module the host's import map serves.
//
// A specifier the rollup config does not externalize is bundled instead of resolved against the
// host at runtime. For `react` that means a second React in the page and hook identity breaking
// inside the extension's own tree, and the build reports nothing: an external is a name match, so a
// spelling nobody listed simply compiles. Cove serves each module under a canonical
// `@cove/runtime/*` name and most of them under a bare alias too, so a list covering one spelling
// leaves the other open.
//
// Cove's `ui/scripts/extension-runtime-contract.ts` is the source those names come from, and it is
// committed, so this reads it rather than mirroring it. With no checkout the check skips loudly,
// the same way check-host-imports does; ci.yml's ui-verify job sets COVE_REPO, which turns the skip
// into a failure.

import { readFileSync, existsSync } from "node:fs";
import path from "node:path";

const repoRoot = path.resolve(import.meta.dirname, "..");
const coveRepo = process.env.COVE_REPO ?? path.resolve(repoRoot, "../cove");
const contract = path.join(coveRepo, "ui/scripts/extension-runtime-contract.ts");
const HOST_EXTERNALS = JSON.parse(
  readFileSync(path.join(repoRoot, "shared/ui-shared/vite/host-externals.json"), "utf8"),
);

if (!existsSync(contract)) {
  if (process.env.COVE_REPO) {
    console.error(
      `check-host-externals: COVE_REPO names ${coveRepo}, which holds no runtime contract at ${contract}.`,
    );
    process.exit(1);
  }
  console.log(
    `check-host-externals: SKIPPED - no runtime contract at ${contract} (set COVE_REPO or add a ../cove sibling)`,
  );
  process.exit(0);
}

const source = readFileSync(contract, "utf8");
const canonical = [...source.matchAll(/specifier:\s*"([^"]+)"/g)].map((m) => m[1]);
const aliases = [...source.matchAll(/legacySpecifiers:\s*\[([^\]]*)\]/g)].flatMap((m) =>
  [...m[1].matchAll(/"([^"]+)"/g)].map((quoted) => quoted[1]),
);
const served = [...canonical, ...aliases];

// A contract this reads but cannot parse would otherwise report an empty host import map as a pass.
if (canonical.length === 0) {
  console.error(
    `check-host-externals: parsed no specifiers out of ${contract}. Its shape changed, so this check ` +
      `no longer reads it. Fix the parse rather than the externals list.`,
  );
  process.exit(1);
}

const listed = new Set(HOST_EXTERNALS);
const missing = served.filter((specifier) => !listed.has(specifier));
const unknown = HOST_EXTERNALS.filter((specifier) => !served.includes(specifier));

if (missing.length > 0 || unknown.length > 0) {
  if (missing.length > 0) {
    console.error(
      `check-host-externals: the host serves these, and the bundle would carry its own copy of each:\n  ` +
        missing.join("\n  "),
    );
  }
  if (unknown.length > 0) {
    console.error(
      `check-host-externals: externalized here but absent from the host import map, so nothing resolves ` +
        `them at runtime:\n  ` +
        unknown.join("\n  "),
    );
  }
  console.error(`Both lists come from ${contract}.`);
  process.exit(1);
}

console.log(
  `check-host-externals: OK (${served.length} host import-map specifiers, all externalized)`,
);
