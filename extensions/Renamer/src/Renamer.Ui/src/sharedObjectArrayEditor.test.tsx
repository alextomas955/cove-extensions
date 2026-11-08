// @vitest-environment jsdom
// This test of a shared component lives in the consuming package because `react` resolves only here.
// The editor's row identity: which DOM node renders which row after a remove, after a reorder, and
// after the parent replaces the whole list. Identity is read off the node itself - each row's input
// is stamped before the step and looked up after it - because nothing the editor renders from `rows`
// can tell a reused node from a fresh one.
import { test, expect, afterEach } from "vitest";
import { useState } from "react";
import { render, screen, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { ObjectArrayEditor, TextInput } from "@cove-extensions/ui-shared";

interface Row {
  text: string;
}

afterEach(cleanup);

function Host(props: Readonly<{ initial: string[] }>) {
  const [rows, setRows] = useState<Row[]>(props.initial.map((text) => ({ text })));
  return (
    <div>
      <button
        type="button"
        onClick={() => {
          setRows([{ text: "W" }, { text: "X" }, { text: "Y" }, { text: "Z" }]);
        }}
      >
        Replace from outside
      </button>
      <ObjectArrayEditor<Row>
        rows={rows}
        onChange={setRows}
        makeRow={() => ({ text: "" })}
        renderRow={(row, i, update) => (
          <TextInput
            value={row.text}
            ariaLabel={`Row ${String(i + 1)}`}
            onChange={(v) => {
              update({ text: v });
            }}
          />
        )}
        addLabel="Add row"
        ordered
      />
    </div>
  );
}

const rowInputs = () => screen.queryAllByRole<HTMLInputElement>("textbox");
const rowValues = () => rowInputs().map((el) => el.value);

// Marks every row's input, so the same node is recognisable wherever it ends up next.
function stampRows() {
  rowInputs().forEach((el, i) => {
    el.dataset.stamp = `s${String(i)}`;
  });
}

// Where each stamped node sits now; a row the editor rebuilt carries no stamp.
const stamps = () => rowInputs().map((el) => el.dataset.stamp ?? "");

function renderRows(initial: string[]) {
  const user = userEvent.setup();
  render(<Host initial={initial} />);
  expect(rowValues()).toEqual(initial);
  return {
    user,
    press: (label: string) => user.click(screen.getByRole("button", { name: label })),
  };
}

test("removing a row carries the rows below it, with their nodes, up one place", async () => {
  const view = renderRows(["A", "B", "C"]);
  stampRows();

  await view.press("Remove row 1");

  expect(rowValues()).toEqual(["B", "C"]);
  // B and C keep the nodes they had, rather than inheriting the node of the row above.
  expect(stamps()).toEqual(["s1", "s2"]);
});

test("moving a row down carries its node with it, so a second press moves the same row", async () => {
  const view = renderRows(["A", "B", "C"]);
  stampRows();

  await view.press("Move row 1 down");

  expect(rowValues()).toEqual(["B", "A", "C"]);
  // A's node is now second. Without that, the button the user is holding down belongs to B and the
  // next press swaps the pair back instead of moving A on.
  expect(stamps()).toEqual(["s1", "s0", "s2"]);

  await view.press("Move row 2 down");

  expect(rowValues()).toEqual(["B", "C", "A"]);
});

test("a list replaced from outside rebuilds every row, so no node lands on another row's place", async () => {
  const view = renderRows(["A", "B", "C", "D", "E"]);

  // Two removes leave the editor's row identities out of step with the row positions, which is the
  // state the replacement below has to be reconciled against.
  await view.press("Remove row 1");
  await view.press("Remove row 1");
  expect(rowValues()).toEqual(["C", "D", "E"]);

  stampRows();

  await view.press("Replace from outside");

  expect(rowValues()).toEqual(["W", "X", "Y", "Z"]);
  // The replacement rows share no identity with what was there, so every row is rebuilt and no stamp
  // survives. Asserted position by position, as the two cases above do: any stamp reappearing here is
  // a kept node handed to a row it did not belong to, which drags the caret and the selection with it.
  expect(stamps()).toEqual(["", "", "", ""]);
});
