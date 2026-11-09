// Drives the real publish-extensions.mjs as a child process against a fixture repository. The script
// resolves the repository from its own location, so each fixture gets a copy of it, and of the module
// it imports, under its own `scripts/`.
//
// Every case here stops before `dotnet publish` is reached, so the suite stays offline and needs no
// SDK. What a publish does once it starts is the end-to-end tier's subject; what is only checkable
// here is the selection it makes and the refusals it makes first - one of which stands in front of a
// recursive delete.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const SCRIPT = "publish-extensions.mjs";

const e2eEntry = {
  name: "Renamer",
  id: "com.example.renamer",
  path: "extensions/Renamer",
  projectPath: "extensions/Renamer/src/Renamer/Renamer.csproj",
  e2ePath: "extensions/Renamer/e2e",
  e2eProject: "renamer",
};

function fixture(entries) {
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), "publish-extensions-"));
  const repo = path.join(parent, "repo");
  fs.mkdirSync(path.join(repo, "scripts"), { recursive: true });
  for (const module of [SCRIPT, "repo-files.mjs"]) {
    fs.copyFileSync(path.join(import.meta.dirname, module), path.join(repo, "scripts", module));
  }
  fs.mkdirSync(path.join(repo, "extensions"), { recursive: true });
  fs.writeFileSync(
    path.join(repo, "extensions", "catalog.json"),
    JSON.stringify({ schemaVersion: 1, extensions: entries }),
  );
  return { parent, repo };
}

function run({ repo }) {
  return spawnSync(process.execPath, [path.join(repo, "scripts", SCRIPT)], { encoding: "utf8" });
}

// `arrange` runs against the fixture before the script does, so anything it plants is in place for
// the run to act on.
function check(entries, assertions, arrange) {
  const f = fixture(entries);
  try {
    arrange?.(f);
    assertions(run(f), f);
  } finally {
    fs.rmSync(f.parent, { recursive: true, force: true });
  }
}

test("a catalog whose entries declare no e2e suite fails rather than reporting a satisfied publish", () => {
  // The harness reads what this step produced. Exiting 0 having published nothing reads as a
  // satisfied prerequisite while providing none, and the install then fails far from the cause.
  check([{ name: "Renamer", id: "com.example.renamer", path: "extensions/Renamer" }], (result) => {
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, /nothing to publish/);
    assert.match(result.stderr, /examined 1 catalog entries, of which 0 declared both/);
  });
});

test("an entry declaring only one half of the e2e pair is not selected", () => {
  // playwright.config.mjs derives its projects from both fields, so an entry carrying one of them
  // has no suite to run and must not be counted as publishable.
  check([{ ...e2eEntry, e2eProject: undefined }], (result) => {
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, /of which 0 declared both/);
  });
});

test("the examined and selected counts are printed on every run, pass or fail", () => {
  // A step whose only output is its exit status cannot be told apart from one that examined nothing.
  // The selected entry carries a bad projectPath, so the line is printed on a run that then fails.
  check(
    [
      { ...e2eEntry, projectPath: "/etc/passwd" },
      { name: "Other", id: "com.example.other", path: "extensions/Other" },
    ],
    (result) => {
      assert.equal(result.status, 1, result.stdout + result.stderr);
      assert.match(result.stdout, /examined 2 catalog entries, of which 1 declared both/);
    },
  );
});

test("a catalog path climbing out of the repository is refused before anything is deleted", () => {
  // The selected entry's publish directory is emptied with a recursive delete before the publish
  // runs, so a path that escapes the repository has to be refused in front of it.
  const outside = "../outside-the-repo";
  const planted = (f) =>
    path.join(path.resolve(f.repo, outside), "artifacts", "publish", "keep.txt");

  check(
    [{ ...e2eEntry, path: outside }],
    (result, f) => {
      assert.equal(result.status, 1, result.stdout + result.stderr);
      assert.match(result.stderr, /Renamer FAILED/);
      assert.match(result.stderr, /path must contain no "\.\." segment/);

      // Planted before the run, at the exact path the recursive delete would have been aimed at.
      assert.ok(
        fs.existsSync(planted(f)),
        "the publish emptied a directory outside the repository",
      );
    },
    (f) => {
      fs.mkdirSync(path.dirname(planted(f)), { recursive: true });
      fs.writeFileSync(planted(f), "a file the publish has no claim on");
    },
  );
});

test("an absolute projectPath is refused, naming the field rather than the first one checked", () => {
  check([{ ...e2eEntry, projectPath: "/etc/passwd" }], (result) => {
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, /projectPath must be repo-relative/);
  });
});

test("a uiPath is checked when declared and skipped when absent", () => {
  check([{ ...e2eEntry, uiPath: "../elsewhere/ui" }], (result) => {
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, /uiPath must contain no "\.\." segment/);
  });
});
