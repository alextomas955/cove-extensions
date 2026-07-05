# Adding an Extension's E2E Suite

The shared end-to-end harness lives at [`tests/e2e/`](https://github.com/alextomas955/cove-extensions/tree/main/tests/e2e) and is published as the
npm-workspace package `@cove-extensions/e2e`. Every extension's own E2E suite imports it **by name**
— there is no `../../../e2e/...` relative-path archaeology and no hand-rolled repo-root math. Adding
a new extension's suite is three steps.

Renamer is the reference implementation — copy its shape from
[`extensions/Renamer/e2e/`](https://github.com/alextomas955/cove-extensions/tree/main/extensions/Renamer/e2e).

## Prerequisites

- Your extension builds to a publish output (`artifacts/publish/` with the built DLL +
  `extension.json`) and, if it has a frontend, a UI bundle at `src/<UiProject>/dist/index.mjs`. This
  is what Renamer's `scripts/deploy-dev.ps1` produces (minus the deploy).
- Docker running (the harness boots a real Cove + Postgres instance per worker).

## Step 1 — Create the extension's `e2e/` folder

Under your extension, create `extensions/<YourExt>/e2e/` with:

**`package.json`** — depends on the harness by name; **never** its own `@playwright/test` (a second
Playwright install breaks Playwright's module singleton — the single hoisted install is enforced by
this dependency shape):

```json
{
  "name": "@cove-extensions/<yourext>-e2e",
  "private": true,
  "type": "module",
  "version": "0.1.0",
  "scripts": {
    "test": "cd ../../../tests/e2e && npx playwright test --project=<yourext>"
  },
  "dependencies": {
    "@cove-extensions/e2e": "*"
  }
}
```

**`lib/<yourext>-fixtures.mjs`** — the thin wiring that pre-fills the `extension` fixture option so
individual specs don't repeat build paths. `resolveExtensionPaths` derives the paths from this
file's own location, so nothing hardcodes a distance to the repo root:

```js
import { test as baseTest, expect } from '@cove-extensions/e2e';
import { resolveExtensionPaths } from '@cove-extensions/e2e/resolve-extension';
// import any other helpers you need: '@cove-extensions/e2e/seed-media', '@cove-extensions/e2e/poll'

export const YOUREXT_EXTENSION = resolveExtensionPaths(import.meta.url, {
  srcProject: 'YourProject', // → src/YourProject/extension.json
  uiProject: 'YourProject.Ui', // → src/YourProject.Ui/dist/index.mjs
});

export const test = baseTest.extend({
  extension: [YOUREXT_EXTENSION, { option: true }],
});

export { expect };
```

**`tests/*.spec.mjs`** — your actual tests. Start from
[`tests/e2e/tests/template.spec.mjs`](https://github.com/alextomas955/cove-extensions/blob/main/tests/e2e/tests/template.spec.mjs) (change its imports from
`../lib/...` to `@cove-extensions/e2e` when you copy it out of the harness), or import your own
`../lib/<yourext>-fixtures.mjs` for the pre-wired `test`.

## Step 2 — Register the Playwright project

Add one entry to [`tests/e2e/playwright.config.mjs`](https://github.com/alextomas955/cove-extensions/blob/main/tests/e2e/playwright.config.mjs)'s
`projects` array, pointing `testDir` at your extension's co-located tests (the config lives at
`tests/e2e/`, so it hops up two levels then into `extensions/`):

```js
{
  name: '<yourext>',
  testDir: join(__dirname, '..', '..', 'extensions', 'YourExt', 'e2e', 'tests'),
},
```

## Step 3 — Register in the catalog + install

Add `e2ePath` and `e2eProject` to your extension's entry in
[`extensions/catalog.json`](https://github.com/alextomas955/cove-extensions/blob/main/extensions/catalog.json) (CI reads these to decide whether to run an
e2e suite and which `--project` to pass):

```json
"e2ePath": "extensions/YourExt/e2e",
"e2eProject": "<yourext>"
```

Then run `npm install` **at the repo root** once. The root `package.json`'s `extensions/*/e2e`
workspace glob picks up the new `e2e` folder automatically — no root-config edit is needed, and the
harness is symlinked into `node_modules` so `@cove-extensions/e2e` resolves by name.

## Run it

```sh
cd tests/e2e
npx playwright test --project=<yourext>
```

CI runs the same command for every catalog entry that declares `e2ePath`/`e2eProject` — see
`.github/workflows/build.yml`'s `e2e` job. There is no CI-only fork of the harness.

See [`tests/e2e/README.md`](https://github.com/alextomas955/cove-extensions/blob/main/tests/e2e/README.md) for the full harness reference (fixtures,
parallel execution, cleanup, implementation notes).
