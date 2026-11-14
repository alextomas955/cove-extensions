# ScanAndIdentify

Cove extension `com.alextomas955.scanandidentify`. It drives Cove's own import pipeline as one
ordered run: scan, backfill the missing fingerprints, identify, then generate the visual assets. If
everything else is cut, a run that executes the four stages in order and stops at the first failure
must still work.

The repo-root `CLAUDE.md` rules apply here. This file adds only what is specific to ScanAndIdentify.

## Facts the code does not show

- The extension owns no scanning, hashing or matching logic, and it enqueues nothing. The settings
  page issues all four host POSTs with the signed-in session and reports the job id each one answers
  with. Identify and generate exist only as `Cove.Api` controller actions, with no extension-facing
  service. The product is the ordering, not the work.
- The order is a data dependency, not a preference. Identify matches on fingerprints the stages
  before it produce, and the visual-asset pass needs the metadata identify writes.
- Nothing in the host orders the four stages. Scan and generate share Cove's one exclusive queue, but
  identify is enqueued non-exclusive and starts the moment it is asked for. The order comes from the
  page waiting for each stage to reach a terminal state before it starts the next, and from nothing
  else.
- The chain advances only while the settings page is open. The page is the only thing that starts a
  stage, so a closed page leaves the stage in flight running and the ones after it waiting until the
  page is open again.
- A second store key, `run`, holds the whole run record: one run, its four stages, the job id each
  one was started as, and the options the run was claimed with. Its field set is fixed, so nothing
  stored grows with the library.
- Four routes carry the run: `POST /run` claims one, `GET /run` reports it, `POST /run/stages`
  records a stage being started and then its job id, and `POST /run/end` ends a run the stage states
  cannot end by themselves. All four demand `library.scan`, `jobs.run`, `library.identify` and
  `extensions.configure` together. The last is in the union because the run view answers the options
  document, which the options routes gate on that permission; answering the same bytes under a weaker
  gate would be a way around it.
- The backend derives every unsettled stage's state from `IJobService.GetJob` on each read, and
  copies a terminal state into the record the first time it sees one. The host forgets a finished job
  once a hundred others have finished, so a state read later would report as unknown.
- A stage whose job no longer resolves reads unknown and ends the run, so the guard against a second
  run releases rather than refusing every later run.
- Cancelling goes through Cove's own `DELETE /api/jobs/{jobId}`, which demands `jobs.cancel`. A
  cancel is a request, not a terminal outcome: a running job only has its token cancelled, so the
  stage's outcome is read from the next run report.
- `GET /api/extensions/com.alextomas955.scanandidentify/capabilities` reports the host version
  captured in `ConfigureServices` and which host services resolved in `InitializeAsync`. It carries no
  library data, which is why it declares `AllowWithoutCovePermission`.
- The settings section's `componentName` and the key in the bundle's `defineExtension` components map
  are the same string. A mismatch renders a host placeholder and logs nothing.
- The `options` key holds the whole run options document. Its field set is fixed, so nothing stored
  grows with the library.
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

The stage ordering and its data dependency, the host-job contract each stage is started against, and
any place a stage's options must be set explicitly rather than left to the host default.
