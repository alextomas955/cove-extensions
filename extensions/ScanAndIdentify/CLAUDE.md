# ScanAndIdentify

Cove extension `com.alextomas955.scanandidentify`. It drives Cove's own import pipeline as one
ordered run: scan, backfill the missing fingerprints, identify, then generate the visual assets. If
everything else is cut, a run that executes the four stages in order and stops at the first failure
must still work.

The repo-root `CLAUDE.md` rules apply here. This file adds only what is specific to ScanAndIdentify.

## Facts the code does not show

- The extension owns no scanning, hashing or matching logic. Every stage is a host job the extension
  enqueues through `IJobService` and `IScanService`. The product is the ordering, not the work.
- The order is a data dependency, not a preference. Identify matches on fingerprints the stages
  before it produce, and the visual-asset pass needs the metadata identify writes.
- Cove drains one exclusive job queue with a single processor, so two stages cannot overlap. A stage
  starts only after the one before it reports completed.
- `GET /api/extensions/com.alextomas955.scanandidentify/capabilities` reports the host version
  captured in `ConfigureServices` and which host services resolved in `InitializeAsync`. It carries no
  library data, which is why it declares `AllowWithoutCovePermission`.
- The settings section's `componentName` and the key in the bundle's `defineExtension` components map
  are the same string. A mismatch renders a host placeholder and logs nothing.
- One store key, `options`, holds the whole run options document. Its field set is fixed, so nothing
  stored grows with the library.
- `GET` and `PUT /options` both declare `extensions.configure` and re-check the principal in the
  handler, because the document decides what a later run does to the whole library.
- `overwrite` is hardcoded false in both generate stages and is deliberately not a user switch.
  Nothing a run does replaces an asset that already exists, which is what keeps a repeat run cheap.

## Build and deploy

- `pwsh scripts/deploy-dev.ps1` builds against the local Cove checkout, builds the UI, assembles the
  catalog's file set and installs it. It does not restart Cove, and the host loads extension
  assemblies once per run. Always `pwsh`. Set `COVE_HOME` off Windows.
- After installing, refetch the SPA index with `cache: 'reload'` before trusting what the browser
  shows. An ordinary refresh serves a cached index against the new API routes.
- `@cove/extension-sdk` is not on npm. It is vendored as a tarball under
  `src/ScanAndIdentify.Ui/vendor/` and installs offline.

## Where comments are needed

The stage ordering and its data dependency, the host-job contract each stage is enqueued against, and
any place a stage's options must be set explicitly rather than left to the host default.
