# Evidence probes

On-demand probes that run against the e2e fixtures and write one JSON record per row. They exist so
an external fact this repo's specs rely on is settled by asking the running fixture, and so a fixture
image bump re-verifies every one of those facts with a single command.

They are **not** part of `npm test` and gate no merge. This directory sits beside `tests/`, never
inside it: Playwright globs a project's test directory, so a probe placed there would be swept into a
suite run, and the rows that reach a live third-party provider cannot run in CI at all.

## Running

```sh
npm run probe --workspace @cove-extensions/e2e -- --out <dir>
# or, from tests/e2e:
node probes/run.mjs --out <dir>
```

| Flag          | Meaning                                                                                                                                                                                      |
| ------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--out <dir>` | **Required.** Where the records are written, one `<row-id>.json` per row. There is no default: the records are the caller's to keep, and this repo does not own the document they end up in. |
| `--row <id>`  | Run only this row. Repeatable. An id no row declares is refused rather than ignored.                                                                                                         |
| `--json`      | Also print the records to stdout.                                                                                                                                                            |
| `--live`      | Opt in to rows that reach a live third-party provider. Without it those rows are skipped, and the skip is recorded with its reason.                                                          |

The runner starts one Cove and one set of Whisparr containers for the whole run, shared by every
selected row, and stops Whisparr before Cove. Everything it starts is Testcontainers-managed, so an
interrupted run is reaped rather than left behind.

Which Cove image boots is decided by the harness, so `COVE_E2E_TAG` selects a leg here exactly as it
does for the suites.

## Providers on the fixture Cove

The bring-up configures the fixture Cove with the metadata servers read out of this machine's own
Cove install, or with the placeholder set when there is none, and reports the outcome on
`ctx.providers`:

| Field                | Meaning                                                                                                                    |
| -------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| `source`             | `install` or `placeholder`.                                                                                                |
| `skip`               | Why the lift found nothing, or `null`. A machine with no install is the ordinary case, never an error.                     |
| `servers`            | The entries as configured, described: endpoint, name, rate limit, and the key's presence and character count. Never a key. |
| `env`                | The `COVE__Scraping__MetadataServers__N__*` environment built for them.                                                    |
| `envVarsInContainer` | How many of those variables the container actually received.                                                               |
| `observedFromEnv`    | What the instance reported before anything was saved to it.                                                                |
| `delivery.by`        | `environment`, `configuration-api`, or `none`.                                                                             |

Both delivery routes are tried because only one of them works today: `startHarness({ env })` reaches
the compose process, where it feeds `${VAR}` interpolation, and a variable the compose file does not
name never reaches the container at all. So the bring-up saves the entries through Cove's own
configuration API instead and re-reads them, which is why `envVarsInContainer` is worth recording
next to any verdict about the environment form: it separates a binder that refused the entries from a
delivery that never carried them.

A row that needs providers should read `ctx.providers`, never re-read the install for itself, and
never widen `servers` back into raw entries.

## Re-verifying after an image bump

A fixture image bump is a deliberate edit to `lib/whisparr-images.mjs`, which is the one place the
Whisparr references are declared. Re-run the probes afterwards and compare the records: the versions
each record carries are read back off the running instances, not off the declaration.

## Adding a row

Add one file to `rows/`. The runner discovers the directory, so there is no registry to edit and two
people adding a row do not contend for one file.

```js
export const row = {
  id: "my-row", // lowercase and hyphenated; it becomes the record's filename
  label: "What this row settles",
  requires: {
    cove: true,
    whisparr: ["v3"],
    seedHistory: false,
    support: [],
    network: false,
    live: false,
  },
  async run(ctx) {
    // ctx.harness, ctx.whisparr, ctx.providers, ctx.builds, ctx.outDir
    return { method: { verb: "GET", path: "/api/system/config" }, verdict: "bound", observed: {} };
  },
};
```

`requires.support` names the support containers the row needs by id, and the bring-up hands them back
on `ctx.support` under the same ids. A support container joins the Cove instance's own network, so a
row asking for one asks for `cove` too, and an id no starter is wired for is refused by name rather
than ignored. Adding one is adding a starter to `lib/context.mjs`'s own table; the bring-up stops
them before the harness, because the daemon refuses to remove a network that still has an attached
endpoint.

`webhook-listener` is the one that exists today. It gives an application under test somewhere to call
and reads back what arrived:

```js
const listener = ctx.support["webhook-listener"];
// Register a callback pointing at listener.url("/my-row/v3"), then:
const [delivery] = await listener.waitForCaptures(1, {
  match: (capture) => capture.path === "/my-row/v3",
});
// delivery: { ts, verb, path, headers, body }
```

It is reachable only by its network alias and publishes no host port, since the only callers are
containers on that network. `waitForCaptures` polls rather than sleeping, and `match` exists because
every row in a run shares one listener: take the deliveries your row caused and leave the rest. A row
that asserts anything about what arrived must read it from the capture, never from the registration's
echo — a field a build accepts and then does not send answers the echo exactly as a working one does.

`requires.network` and `requires.live` are separate on purpose. `network` says the row depends on
outbound internet, which the record then names as an external dependency; a row that cannot reach it
must fail with a named error rather than record a silent empty result. `live` is the stronger case:
the row reaches a third-party provider with real credentials under that provider's configured rate
limit, and it never runs without `--live`.

**No credential value may reach a record.** `lib/record.mjs` is the single choke point: every value
written passes through its redactor, which replaces any provider entry with a presence-and-length
description. Take the fields a row needs and discard the rest; never persist a whole response.

## Writing evidence too large for a record

A record is read by a person, so an artefact like a published API document belongs beside it rather
than inside it. `lib/record.mjs`'s `writeCompanion(ctx.outDir, name, contents)` writes one, and the
row then summarises it into its own record and records the path.

Key the name by the build that produced the artefact. Two runs against different images then leave
two files a later phase can diff, where one fixed name would leave the newer overwriting the older.
The name is held to a plain-lowercase rule because it is normally assembled from a version string a
third party reported.

`ctx.outDir` is the directory the caller named on `--out`. It is `undefined` when a row is exercised
outside the runner, so a row that writes one must say what it did when there was nowhere to write.

## Checking a record's size

A record is read by a person and transcribed by hand, so no array in one may grow past the checker's
limit. Check it by running the committed checker and grepping for the token it prints:

```sh
node probes/lib/check-record-bounds.mjs <dir>/my-row.json | grep -q RECORD_BOUNDS_OK
```

The `grep -q` is the whole pass condition, and that is deliberate. An inline `node -e` on this
machine can do nothing at all and still exit 0, which makes a check that passed and a check that
never ran the same observation. So the logic lives in a committed file, the file prints
`RECORD_BOUNDS_OK` on stdout and nothing else on success, and the caller greps for that exact
spelling. A run that printed nothing fails. Do not re-implement this check inline, and do not rename
the token.

## Hand-running against a container

`docker exec` through git-bash rewrites a leading-slash argument into a Windows path. Prefix the
command with `MSYS_NO_PATHCONV=1`, or write the path as `//config`.
