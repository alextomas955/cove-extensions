// Renamer-specific wiring on top of the shared extensions/e2e harness: pre-fills the `extension`
// fixture option with Renamer's own build paths and re-exports the shared helpers so individual
// test files stay focused on behavior, not plumbing. Uses the shared harness's own
// node_modules/@playwright/test install (see package.json) — a second, separate
// @playwright/test install under this directory breaks Playwright's module singleton.
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { test as baseTest, expect } from '../../../e2e/lib/fixtures.mjs';
import { seedVideo } from '../../../e2e/lib/seed-media.mjs';
import { pollJob, pollUntil } from '../../../e2e/lib/poll.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(__dirname, '..', '..', '..', '..');

export const RENAMER_EXTENSION = {
  publishDir: join(repoRoot, 'extensions/Renamer/artifacts/publish'),
  manifestPath: join(repoRoot, 'extensions/Renamer/src/Renamer/extension.json'),
  uiBundlePath: join(repoRoot, 'extensions/Renamer/src/Renamer.Ui/dist/index.mjs'),
};

export const test = baseTest.extend({
  extension: [RENAMER_EXTENSION, { option: true }],
});

export { expect, seedVideo, pollJob, pollUntil };
