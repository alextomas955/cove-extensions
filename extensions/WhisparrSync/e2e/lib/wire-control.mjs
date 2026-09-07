// The four-part control every narrowing of a Whisparr read must pass, and the shape of the failure it
// exists to catch.
//
// Whisparr's movie index binds a fixed set of filter parameters and its filter chain ends in an `else`
// that returns the entire set. A parameter it does not bind therefore answers 200 with every row — which
// is indistinguishable from a filter that worked, unless the assertion is specific about WHICH rows came
// back. That is why part 2 asserts row IDENTITY rather than a row count: on a seeded set, "one row came
// back" and "the one row I asked for came back" only differ when the filter is silently ignored and the
// set happens to hold a single row.
//
// The control returns a per-part report and never throws, so the SAME control can be applied to a
// narrowed read (expected all-green) and to the un-narrowed read it replaces (expected red at parts 2
// and 3, green at 1 and 4). A control that has only ever been seen green is an assertion, not a gate.
//
// Deliberately free of any container, fetch or fixture dependency: the reads arrive as callbacks, so a
// later phase applies it to a different endpoint without a second implementation, and it can be reasoned
// about with no Docker daemon.

/** The minimum seeded set size at which part 2 distinguishes a working filter from an ignored one. */
export const MIN_SEED_SIZE = 3;

/**
 * Applies the four-part control and returns a report.
 *
 * @param {{
 *   setSize: () => Promise<number>,
 *   byId: (id: string) => Promise<unknown[]>,
 *   unrecognisedParam: () => Promise<unknown[]>,
 *   identityOf: (row: unknown) => string | undefined,
 * }} reads
 *   - `setSize` the instance's own count of the seeded set.
 *   - `byId` the read under control. On the inverted leg this ignores its argument.
 *   - `unrecognisedParam` the same endpoint carrying a parameter it does not bind.
 *   - `identityOf` pulls the value the filter selects on out of a returned row.
 * @param {{ seedSize: number, presentId: string, absentId: string, expectedIdentities?: string[] }} expected
 *   - `expectedIdentities` (optional) the identity SET part 2 must return. A read keyed on a ROW's own id
 *     returns exactly that row, which is the default; a read keyed on an ENTITY returns that entity's whole
 *     catalogue, so the caller states the set. Either way the comparison is by identity, never by count.
 * @returns {Promise<{ parts: Array<{part: number, name: string, passed: boolean, detail: string}>,
 *   allPassed: boolean, passedParts: number[], failedParts: number[] }>}
 */
export async function runFilterControl(reads, { seedSize, presentId, absentId, expectedIdentities }) {
  const parts = [];

  const observedSetSize = await reads.setSize();
  parts.push(
    part(
      1,
      'the seeded set is the asserted size and holds at least ' + MIN_SEED_SIZE + ' rows',
      observedSetSize === seedSize && seedSize >= MIN_SEED_SIZE,
      `expected ${seedSize} (>= ${MIN_SEED_SIZE}), instance reports ${observedSetSize}`,
    ),
  );

  const present = await reads.byId(presentId);
  const presentIdentities = present.map((row) => reads.identityOf(row));
  const wanted = expectedIdentities ?? [presentId];
  parts.push(
    part(
      2,
      expectedIdentities
        ? 'a present id returns exactly the rows that id selects, by identity'
        : 'a present id returns exactly the row asked for, by identity',
      sameSet(presentIdentities, wanted),
      `asked for ${presentId}, wanted identities ${JSON.stringify([...wanted].sort())}, ` +
        `got ${present.length} row(s) with identities ${JSON.stringify(presentIdentities)}`,
    ),
  );

  const absent = await reads.byId(absentId);
  parts.push(
    part(
      3,
      'a well-formed but absent id returns zero rows',
      absent.length === 0,
      `asked for ${absentId}, got ${absent.length} row(s)`,
    ),
  );

  const unrecognised = await reads.unrecognisedParam();
  parts.push(
    part(
      4,
      'an unrecognised parameter returns the whole set',
      unrecognised.length === seedSize && seedSize > 1,
      `got ${unrecognised.length} row(s), whole set is ${seedSize}`,
    ),
  );

  return {
    parts,
    allPassed: parts.every((p) => p.passed),
    passedParts: parts.filter((p) => p.passed).map((p) => p.part),
    failedParts: parts.filter((p) => !p.passed).map((p) => p.part),
  };
}

function part(number, name, passed, detail) {
  return { part: number, name, passed, detail };
}

// Set equality, not sequence equality: response ORDER is not part of any contract asserted here, and a
// sequence comparison would fail for a re-ordering that is not the defect this control exists to catch.
function sameSet(got, wanted) {
  const a = [...new Set(got)].sort();
  const b = [...new Set(wanted)].sort();
  return a.length === b.length && got.length === b.length && a.every((value, index) => value === b[index]);
}

/** Renders a report as one line per part, for an assertion message that names what moved. */
export function describeReport(report) {
  return report.parts.map((p) => `  part ${p.part} ${p.passed ? 'PASS' : 'FAIL'}: ${p.name} — ${p.detail}`).join('\n');
}
