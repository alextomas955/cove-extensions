// Copyable starting point for a new extension's E2E suite. Copy this file (and this comment
// block) into your extension's own test directory, update the `extension` fixture option to
// point at your build output, and replace the two example tests with your own.
//
// Run: cd extensions/e2e && npm test -- template.spec.mjs
import { test, expect } from '../lib/fixtures.mjs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(__dirname, '..', '..', '..');

test.use({
  extension: {
    publishDir: join(repoRoot, 'extensions/Renamer/artifacts/publish'),
    manifestPath: join(repoRoot, 'extensions/Renamer/src/Renamer/extension.json'),
    uiBundlePath: join(repoRoot, 'extensions/Renamer/src/Renamer.Ui/dist/index.mjs'),
  },
});

test('extension installs and reports enabled via the API', async ({ api }) => {
  const { ok, json } = await api.get('/api/extensions');
  expect(ok).toBe(true);
  const installed = json.find((e) => e.id === 'com.alextomas955.renamer');
  expect(installed?.enabled).toBe(true);
});

test('Cove home page loads in the browser against the containerized instance', async ({ page }) => {
  await expect(page).toHaveTitle(/Cove/i);
});
