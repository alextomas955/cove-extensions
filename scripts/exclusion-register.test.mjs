// A positive assertion per audited analyzer/linter exclusion in this repo.
//
// The risk these entries carry is not that they are wrong — each was audited against the real system
// and found correct. The risk is that a later cleanup reads one cold, assumes it is stale, and
// removes or demotes it; a reviewer scanning a diff of unrelated work is the only thing standing in
// the way. This check is what makes such a removal fail instead of pass.
//
// Presence is asserted from the contents of the file that actually holds each entry, never from a
// diff against another branch: a diff is empty on a fresh clone, so it would report a pass having
// inspected nothing. An unreadable file is a failure for the same reason. Nothing here writes.
//
// One entry is the exception, marked `inverse`: it asserts a deliberately removed suppression stays
// ABSENT. It rides the same accumulate-then-report path so it reports in register order alongside the
// presence entries, and it is labelled so a reader does not "fix" it by putting the token back.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "..");

const uiSourceRoots = ["extensions", "shared"];
const skipDirs = new Set(["node_modules", "dist", "vendor", "obj", "bin", ".git", "coverage"]);

// The audited inline-disable inventory, measured per area rather than as a bare total: an area-level
// move that happened to preserve the total would otherwise read as unchanged.
const disableInventory = {
  total: 14,
  byArea: { "extensions/Renamer": 6, "extensions/WhisparrSync": 5, shared: 3 },
  // The three sites in the shared package, each pinned by its own directive text — no glob written
  // against the two extensions' UI sources reaches any of them.
  sharedSites: [
    {
      file: "shared/cove-extensions-ui/src/primitives.tsx",
      directive: "/* eslint-disable react-hooks/set-state-in-effect */",
      closer: "/* eslint-enable react-hooks/set-state-in-effect */",
      disables: 2,
      enables: 2,
    },
    {
      file: "shared/cove-extensions-ui/src/overlay.ts",
      directive: "// eslint-disable-next-line react-hooks/exhaustive-deps",
      closer: null,
      disables: 1,
      enables: 0,
    },
  ],
};

function diskContext() {
  return {
    readFile: (rel) => fs.readFileSync(path.join(root, rel), "utf8"),
    listUiSources: () => {
      const found = [];
      const walk = (dir, rel) => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
          if (entry.isDirectory()) {
            if (!skipDirs.has(entry.name)) walk(path.join(dir, entry.name), rel + "/" + entry.name);
          } else if (entry.name.endsWith(".ts") || entry.name.endsWith(".tsx")) {
            found.push(rel + "/" + entry.name);
          }
        }
      };
      for (const top of uiSourceRoots) walk(path.join(root, top), top);
      return found.sort();
    },
    // Every MSBuild file a suppression could live in, rather than the two projects one was removed
    // from: a re-add that landed in a different project or in a root property file would otherwise be
    // invisible to a check written against a fixed pair of paths.
    listMsBuildFiles: () => {
      const found = [];
      const walk = (dir, rel) => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
          const child = rel === "" ? entry.name : rel + "/" + entry.name;
          if (entry.isDirectory()) {
            if (!skipDirs.has(entry.name)) walk(path.join(dir, entry.name), child);
          } else if (/\.(csproj|props|targets)$/.test(entry.name)) {
            found.push(child);
          }
        }
      };
      walk(root, "");
      return found.sort();
    },
  };
}

// .editorconfig severities are scoped by their section header, so a token found anywhere in the file
// is not the same fact as a token found under the header that scopes it — the DiskMover relaxation is
// the whole point of the distinction.
function sectionBody(text, header) {
  const lines = text.split("\n");
  const collected = [];
  let inside = false;
  for (const line of lines) {
    const isHeader = /^\[.*\]\s*$/.test(line.trim());
    if (isHeader) {
      inside = line.trim() === header;
      continue;
    }
    if (inside) collected.push(line);
  }
  return collected.join("\n");
}

function requireIn(text, token, failures, label) {
  if (!text.includes(token)) failures.push(`${label}: missing ${token}`);
}

function forbidIn(text, token, failures, label, why) {
  if (text.includes(token)) failures.push(`${label}: ${token} is back. ${why}`);
}

// The extends/languageOptions body of one ESLint config block, located by its exact files pattern so
// the two blocks that also match the shared package are not confused with it.
function eslintBlockBody(text, filesLine) {
  const lines = text.split("\n");
  const start = lines.findIndex((line) => line.trim() === filesLine);
  if (start === -1) return null;
  const end = lines.findIndex((line, i) => i > start && line === "  },");
  return end === -1 ? null : lines.slice(start, end).join("\n");
}

const REGISTER = [
  {
    number: 1,
    name: "CS1591 NoWarn (docs earned, not mandated)",
    check: (ctx, failures, label) => {
      requireIn(ctx.readFile("Directory.Build.props"), "<NoWarn>$(NoWarn);CS1591</NoWarn>", failures, label);
    },
  },
  {
    number: 2,
    name: "CA2007 = none (ConfigureAwait not needed in an ASP.NET-hosted library)",
    check: (ctx, failures, label) => {
      const body = sectionBody(ctx.readFile(".editorconfig"), "[*.cs]");
      requireIn(body, "dotnet_diagnostic.CA2007.severity = none", failures, label);
    },
  },
  {
    number: 3,
    name: "CA1062 = suggestion (public-arg validation)",
    check: (ctx, failures, label) => {
      const body = sectionBody(ctx.readFile(".editorconfig"), "[*.cs]");
      requireIn(body, "dotnet_diagnostic.CA1062.severity = suggestion", failures, label);
    },
  },
  {
    number: 4,
    name: "CA1822 = none scoped to DiskMover.cs (the DI seam)",
    check: (ctx, failures, label) => {
      const text = ctx.readFile(".editorconfig");
      const header = "[extensions/Renamer/src/Renamer/Execution/DiskMover.cs]";
      if (!text.includes(header)) {
        failures.push(`${label}: missing the ${header} section, so the relaxation is no longer scoped to that file`);
        return;
      }
      requireIn(sectionBody(text, header), "dotnet_diagnostic.CA1822.severity = none", failures, label);
    },
  },
  {
    number: 5,
    name: 'CA1716 NoWarn on the four projects carrying the "Shared" namespace segment',
    check: (ctx, failures, label) => {
      const projects = [
        "shared/Cove.Extensions.Shared/Cove.Extensions.Shared.csproj",
        "shared/Cove.Extensions.Shared.Testing/Cove.Extensions.Shared.Testing.csproj",
        "extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj",
        "extensions/WhisparrSync/src/WhisparrSync.Tests/WhisparrSync.Tests.csproj",
      ];
      for (const project of projects) {
        const noWarn = /<NoWarn>([^<]*)<\/NoWarn>/.exec(ctx.readFile(project));
        if (!noWarn) {
          failures.push(`${label}: ${project} has no NoWarn element at all`);
        } else if (!noWarn[1].includes("CA1716")) {
          failures.push(`${label}: ${project} NoWarn no longer carries CA1716 (found: ${noWarn[1]})`);
        }
      }
    },
  },
  {
    number: 6,
    name: "the test-project relaxations CA1707 / CA1822 / CA2007 / CA1861",
    check: (ctx, failures, label) => {
      const text = ctx.readFile(".editorconfig");
      const sections = [
        "[extensions/Renamer/src/Renamer.Tests/**.cs]",
        "[extensions/WhisparrSync/src/WhisparrSync.Tests/**.cs]",
      ];
      for (const header of sections) {
        if (!text.includes(header)) {
          failures.push(`${label}: missing the ${header} section`);
          continue;
        }
        const body = sectionBody(text, header);
        for (const rule of ["CA1707", "CA1822", "CA2007", "CA1861"]) {
          requireIn(body, `dotnet_diagnostic.${rule}.severity = none`, failures, `${label} ${header}`);
        }
      }
    },
  },
  {
    number: 7,
    name: "the CA1304 / CA1311 / CA1862 pragmas in CoveLibraryPort.cs (EF translates ToUpper to SQL)",
    check: (ctx, failures, label) => {
      const file = "extensions/WhisparrSync/src/WhisparrSync/Library/CoveLibraryPort.cs";
      const text = ctx.readFile(file);
      requireIn(text, "#pragma warning disable CA1304, CA1311, CA1862", failures, label);
      requireIn(text, "#pragma warning restore CA1304, CA1311, CA1862", failures, label);
      const disables = text.split("#pragma warning disable CA1304").length - 1;
      const restores = text.split("#pragma warning restore CA1304").length - 1;
      if (disables !== restores) {
        failures.push(
          `${label}: ${String(disables)} CA1304 disable(s) against ${String(restores)} restore(s) — an unbalanced pragma widens its own scope`,
        );
      }
    },
  },
  {
    number: 8,
    name: "CA1001 suppression on ListCaches.cs's process-lifetime memoizer",
    check: (ctx, failures, label) => {
      const text = ctx.readFile("extensions/WhisparrSync/src/WhisparrSync/Caching/ListCaches.cs");
      requireIn(text, "CA1001:Types that own disposable fields should be disposable", failures, label);
    },
  },
  {
    number: 9,
    name: "the shared UI package is linted non-type-aware on purpose",
    check: (ctx, failures, label) => {
      const body = eslintBlockBody(ctx.readFile("eslint.config.mjs"), 'files: ["shared/cove-extensions-ui/**/*.{ts,tsx}"],');
      if (body === null) {
        failures.push(`${label}: no config block whose files pattern is the shared package alone`);
        return;
      }
      requireIn(body, "tseslint.configs.recommended", failures, label);
      if (body.includes("projectService")) {
        failures.push(
          `${label}: the block now enables projectService, which is the type-aware linting this entry exists to keep out`,
        );
      }
    },
  },
  {
    number: 10,
    name: "the fourteen audited inline eslint-disable sites",
    check: (ctx, failures, label) => {
      const perArea = { "extensions/Renamer": 0, "extensions/WhisparrSync": 0, shared: 0 };
      let total = 0;
      for (const rel of ctx.listUiSources()) {
        const count = ctx.readFile(rel).split("eslint-disable").length - 1;
        if (count === 0) continue;
        total += count;
        const area = Object.keys(perArea).find((prefix) => rel.startsWith(prefix + "/"));
        if (area === undefined) {
          failures.push(`${label}: ${rel} holds ${String(count)} disable(s) outside every audited area`);
        } else {
          perArea[area] += count;
        }
      }
      if (total !== disableInventory.total) {
        failures.push(
          `${label}: observed ${String(total)} inline disable(s) against the audited ${String(disableInventory.total)}. ` +
            "A removal is a regression; a legitimate new site raises the recorded inventory in the same change that adds it.",
        );
      }
      for (const [area, expected] of Object.entries(disableInventory.byArea)) {
        if (perArea[area] !== expected) {
          failures.push(
            `${label}: ${area} holds ${String(perArea[area])} disable(s) against the audited ${String(expected)}`,
          );
        }
      }
      for (const site of disableInventory.sharedSites) {
        const text = ctx.readFile(site.file);
        const directives = text.split(site.directive).length - 1;
        const disables = text.split("eslint-disable").length - 1;
        const enables = text.split("eslint-enable").length - 1;
        if (directives !== site.disables) {
          failures.push(
            `${label}: ${site.file} holds ${String(directives)} occurrence(s) of "${site.directive}" against the audited ${String(site.disables)}`,
          );
        }
        if (disables !== site.disables) {
          failures.push(
            `${label}: ${site.file} holds ${String(disables)} disable(s) against the audited ${String(site.disables)}`,
          );
        }
        // A block disable left unclosed silently applies to the rest of the file, which is a
        // different defect from a removal and needs its own reading.
        if (enables !== site.enables) {
          failures.push(
            `${label}: ${site.file} holds ${String(enables)} re-enable(s) against the audited ${String(site.enables)} — an unclosed block disable widens its own scope`,
          );
        }
        if (site.closer !== null && text.split(site.closer).length - 1 !== site.enables) {
          failures.push(`${label}: ${site.file} no longer closes each block disable with "${site.closer}"`);
        }
      }
    },
  },
  {
    number: 11,
    name: "check-response-casing's deliberately WhisparrSync-only POLICY",
    check: (ctx, failures, label) => {
      const text = ctx.readFile("scripts/check-response-casing.mjs");
      requireIn(text, 'const POLICY = ["extensions/WhisparrSync"];', failures, label);
      requireIn(text, "SCOPE IS DELIBERATELY NOT REPO-WIDE", failures, label);
    },
  },
  // The register's one INVERSE entry. Every entry above keeps an audited exclusion alive; this one
  // keeps a removed one dead, which is the same defect class read in the other direction.
  {
    number: 12,
    inverse: true,
    name: "INVERSE — the NuGet audit suppression stays removed from every MSBuild file",
    check: (ctx, failures, label) => {
      const projects = ctx.listMsBuildFiles();
      if (projects.length === 0) {
        failures.push(`${label}: no MSBuild file was found, so nothing was inspected`);
        return;
      }
      const why =
        "NU1903 is a whole diagnostic category, not one advisory: suppressing it again hides every " +
        "future high-severity finding, including ones nobody has seen yet. The affected transitive is " +
        "pinned past the advisory in Directory.Packages.props, and a pin is the response to a new " +
        "advisory too — never a NoWarn. This entry asserts ABSENCE; do not satisfy it by adding the token back.";
      for (const project of projects) {
        forbidIn(ctx.readFile(project), "NU1903", failures, `${label}: ${project}`, why);
      }
    },
  },
];

// Failures accumulate across every entry and report in register order, so a reviewer sees the whole
// set and two runs over the same tree print identically.
function auditRegister(ctx) {
  const failures = [];
  for (const entry of REGISTER) {
    const label = `exclusion-register: entry ${String(entry.number)} (${entry.name})`;
    try {
      entry.check(ctx, failures, label);
    } catch (error) {
      failures.push(`${label}: unreadable — ${error instanceof Error ? error.message : String(error)}`);
    }
  }
  return failures;
}

test("every audited exclusion is still present, and the one removed suppression is still absent", () => {
  const failures = auditRegister(diskContext());
  assert.deepEqual(failures, [], failures.join("\n"));
});

for (const entry of REGISTER) {
  test(`register entry ${String(entry.number)}: ${entry.name}`, () => {
    const failures = [];
    entry.check(diskContext(), failures, "entry");
    assert.deepEqual(failures, [], failures.join("\n"));
  });
}

test("a removed entry is reported by name rather than passing", () => {
  const real = diskContext();
  const withoutCa2007 = {
    ...real,
    readFile: (rel) =>
      rel === ".editorconfig"
        ? real.readFile(rel).replace("dotnet_diagnostic.CA2007.severity = none", "")
        : real.readFile(rel),
  };
  const failures = auditRegister(withoutCa2007);
  assert.ok(failures.length > 0);
  assert.ok(failures.some((f) => f.includes("entry 2") && f.includes("CA2007")), failures.join("\n"));
});

// The inverse entry's whole value is what happens when the token comes back, so that direction is
// driven here per project rather than left to the observation that it is currently absent.
test("the inverse entry goes red per project when the audit suppression is re-added", () => {
  const real = diskContext();
  const suppressed = "<NoWarn>$(NoWarn);NU1903;CA1716</NoWarn>";
  const current = "<NoWarn>$(NoWarn);CA1716</NoWarn>";
  const projects = [
    "extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj",
    "extensions/WhisparrSync/src/WhisparrSync.Tests/WhisparrSync.Tests.csproj",
  ];

  for (const project of projects) {
    const readded = {
      ...real,
      readFile: (rel) => (rel === project ? real.readFile(rel).replace(current, suppressed) : real.readFile(rel)),
    };
    const failures = auditRegister(readded);
    assert.ok(
      failures.some((f) => f.includes("entry 12") && f.includes(project) && f.includes("NU1903")),
      failures.join("\n"),
    );
    // Re-adding the token must not disturb entry 5: CA1716 shares the line and is a register entry of
    // its own, so a failure there would mean the two facts are not being read apart.
    assert.equal(
      failures.some((f) => f.includes("entry 5")),
      false,
      failures.join("\n"),
    );
  }

  // A re-add does not have to land where the removal happened, so the reach beyond that pair is
  // asserted too — here on a root property file that never carried the token.
  const elsewhere = "Directory.Build.props";
  const moved = {
    ...real,
    readFile: (rel) =>
      rel === elsewhere ? real.readFile(rel) + "\n<!-- " + suppressed + " -->\n" : real.readFile(rel),
  };
  assert.ok(
    auditRegister(moved).some((f) => f.includes("entry 12") && f.includes(elsewhere)),
    auditRegister(moved).join("\n"),
  );

  assert.deepEqual(
    auditRegister(real).filter((f) => f.includes("entry 12")),
    [],
  );
});

test("an unreadable file is a failure, not a satisfied entry", () => {
  const real = diskContext();
  const unreadable = {
    ...real,
    readFile: (rel) => {
      if (rel === "Directory.Build.props") throw new Error("ENOENT: no such file or directory");
      return real.readFile(rel);
    },
  };
  const failures = auditRegister(unreadable);
  assert.ok(failures.some((f) => f.includes("entry 1") && f.includes("unreadable")), failures.join("\n"));
});

test("the inline-disable inventory moves red in both directions, and failures report in register order", () => {
  const real = diskContext();
  const overlay = "shared/cove-extensions-ui/src/overlay.ts";

  const deleted = {
    ...real,
    readFile: (rel) => (rel === overlay ? real.readFile(rel).replace("// eslint-disable-next-line", "//") : real.readFile(rel)),
  };
  const onDeletion = auditRegister(deleted);
  assert.ok(onDeletion.some((f) => f.includes("entry 10") && f.includes("13")), onDeletion.join("\n"));

  const added = {
    ...real,
    readFile: (rel) => (rel === overlay ? real.readFile(rel) + "\n// eslint-disable-next-line no-console\n" : real.readFile(rel)),
  };
  const onAddition = auditRegister(added);
  assert.ok(onAddition.some((f) => f.includes("entry 10") && f.includes("15")), onAddition.join("\n"));

  const both = {
    ...real,
    readFile: (rel) => {
      if (rel === ".editorconfig") return real.readFile(rel).replace("dotnet_diagnostic.CA1062.severity = suggestion", "");
      if (rel === overlay) return real.readFile(rel).replace("// eslint-disable-next-line", "//");
      return real.readFile(rel);
    },
  };
  const ordered = auditRegister(both);
  const numbers = ordered.map((f) => Number(/entry (\d+)/.exec(f)?.[1] ?? 0));
  assert.deepEqual(numbers, [...numbers].sort((a, b) => a - b), ordered.join("\n"));
  assert.deepEqual(ordered, auditRegister(both));
});
