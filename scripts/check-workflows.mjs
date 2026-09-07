#!/usr/bin/env node
// The workflow gate: the workflow linter over every file under .github/workflows, wrapped in the two
// assertions the linter cannot make about itself.
//
// FIRST, the shell checker's presence. Every finding these workflows have ever produced came from the
// linter's shellcheck integration rather than from its own rules, and the linter disables that
// integration SILENTLY when the binary is unavailable — measured here: with shellcheck off PATH it
// exits 0 reporting nothing, and it does the same when handed an explicit path to a binary that does
// not exist. So a green run on a runner whose image dropped shellcheck would report zero findings for
// entirely the wrong reason, which is the precise shape of unearned pass this gate exists to remove.
// The remedy for a failure here is to install the checker, never to drop the assertion.
//
// SECOND, the file set. These workflow files always exist, so a scan that matched none of them means
// the scan broke — a run that inspected nothing is not evidence that anything is clean.
//
// The linter is invoked over an explicitly sorted file list rather than left to discover files itself,
// so its report order is the same on every run and does not depend on directory-read order.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { spawnSync } from "node:child_process";
import { pathToFileURL } from "node:url";

const root = path.resolve(import.meta.dirname, "..");

const WORKFLOW_DIR = ".github/workflows";

const EXIT_OK = 0;
const EXIT_FINDING = 1;

const AllowedCommands = new Set(["actionlint", "shellcheck"]);

// Both commands are literals from this file and every argument is a path read from a directory
// listing, never from argv.
function spawnAllowed(command, args, options) {
  if (!AllowedCommands.has(command)) throw new Error("refusing to run non-allowlisted command: " + command);
  // SonarCloud's agentic argument-injection rule (S8705) is a verified false positive here: spawnSync
  // with an ARRAY of args and no shell never spawns a shell (Node's own recommended-safe form), the
  // command is allowlisted above, and no external input reaches either the command or an argument.
  return spawnSync(command, args, options); // NOSONAR
}

/**
 * Reads the environment the gate runs against.
 *
 * @returns {{listFiles: () => string[], probeShellcheck: () => {ok: boolean, detail: string}, lint: (files: string[]) => number}}
 */
export function diskContext() {
  return {
    listFiles: () => {
      const dir = path.join(root, WORKFLOW_DIR);
      if (!fs.existsSync(dir)) return [];
      // Both extensions: a .yaml workflow is equally valid to the platform, and a .yml-only scan would
      // be silently blind to one.
      return fs
        .readdirSync(dir)
        .filter((name) => name.endsWith(".yml") || name.endsWith(".yaml"))
        .sort()
        .map((name) => WORKFLOW_DIR + "/" + name);
    },
    probeShellcheck: () => {
      const probe = spawnAllowed("shellcheck", ["--version"], { encoding: "utf8" });
      if (probe.error) return { ok: false, detail: "shellcheck could not be spawned: " + probe.error.message };
      if (probe.status !== 0) return { ok: false, detail: "shellcheck --version exited " + String(probe.status) };
      const version = /version:\s*(\S+)/.exec(probe.stdout ?? "")?.[1] ?? "an unreported version";
      return { ok: true, detail: version };
    },
    lint: (files) => {
      const result = spawnAllowed("actionlint", files, { stdio: "inherit", cwd: root });
      // A signal-terminated linter counts as a finding rather than as a clean run.
      return result.status ?? EXIT_FINDING;
    },
  };
}

/**
 * Runs the workflow gate.
 *
 * @param {ReturnType<typeof diskContext>} ctx - the environment to read; injectable so each failure
 *   mode has a unit-level reading independent of the machine the gate happens to run on.
 * @returns {{code: number, reason: string}} `code` is the gate's exit contribution.
 */
export function checkWorkflows(ctx) {
  const files = ctx.listFiles();
  if (files.length === 0) {
    return {
      code: EXIT_FINDING,
      reason:
        "no workflow file was found under " +
        WORKFLOW_DIR +
        ", so nothing was inspected — these files always exist, and an empty match is a broken scan rather than a clean result",
    };
  }

  const shell = ctx.probeShellcheck();
  if (!shell.ok) {
    return {
      code: EXIT_FINDING,
      reason:
        shell.detail +
        " — the workflow linter drops its shell checking silently when that binary is missing, and every finding these workflows have comes from that half, so continuing would report zero findings for the wrong reason. Install the checker rather than removing this assertion.",
    };
  }

  const code = ctx.lint(files);
  return code === EXIT_OK
    ? {
        code: EXIT_OK,
        reason:
          String(files.length) + " workflow file(s) inspected with shellcheck " + shell.detail + ", no findings",
      }
    : {
        code: EXIT_FINDING,
        reason: "the workflow linter exited " + String(code) + " over " + String(files.length) + " file(s)",
      };
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  const { code, reason } = checkWorkflows(diskContext());
  console.log("check-workflows: " + (code === EXIT_OK ? "OK" : "FAILED") + " — " + reason);
  process.exit(code);
}
