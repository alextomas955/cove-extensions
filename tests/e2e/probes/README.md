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
    // ctx.harness, ctx.whisparr, ctx.providers, ctx.builds
    return { method: { verb: "GET", path: "/api/system/config" }, verdict: "bound", observed: {} };
  },
};
```

`requires.network` and `requires.live` are separate on purpose. `network` says the row depends on
outbound internet, which the record then names as an external dependency; a row that cannot reach it
must fail with a named error rather than record a silent empty result. `live` is the stronger case:
the row reaches a third-party provider with real credentials under that provider's configured rate
limit, and it never runs without `--live`.

**No credential value may reach a record.** `lib/record.mjs` is the single choke point: every value
written passes through its redactor, which replaces any provider entry with a presence-and-length
description. Take the fields a row needs and discard the rest; never persist a whole response.

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
