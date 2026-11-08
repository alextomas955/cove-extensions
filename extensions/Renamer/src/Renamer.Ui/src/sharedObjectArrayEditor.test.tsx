// @vitest-environment jsdom
// This test of a shared component lives in the consuming package because `react` resolves only here.
// The editor's row identity: which DOM node renders which row after a remove, after a reorder, and
// after the parent replaces the whole list. Identity is read off the node itself - each row's input
// is stamped before the step and looked up after it - because nothing the editor renders from `rows`
// can tell a reused node from a fresh one.
import { test, expect } from "vitest";
import { useState } from "react";
import { createRoot } from "react-dom/client";

import { ObjectArrayEditor, TextInput } from "@cove-extensions/ui-shared";

import { waitFor } from "./common/lib/flushRender";

interface Row {
  text: string;
}

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

function render(initial: string[]) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(<Host initial={initial} />);
  return {
    container,
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

function rowInputs(container: HTMLElement): HTMLInputElement[] {
  return [...container.querySelectorAll<HTMLInputElement>('input[type="text"]')];
}

function rowValues(container: HTMLElement): string[] {
  return rowInputs(container).map((el) => el.value);
}

/** Marks every row's input, so the same node is recognisable wherever it ends up next. */
function stampRows(container: HTMLElement) {
  rowInputs(container).forEach((el, i) => {
    el.dataset.stamp = `s${String(i)}`;
  });
}

/** Where each stamped node sits now; a row the editor rebuilt carries no stamp. */
function stamps(container: HTMLElement): string[] {
  return rowInputs(container).map((el) => el.dataset.stamp ?? "");
}

function click(container: HTMLElement, label: string) {
  const button = container.querySelector<HTMLElement>(`button[aria-label="${label}"]`);
  if (!button) throw new Error(`no button labelled ${label}`);
  button.click();
}

async function renderRows(initial: string[]) {
  const view = render(initial);
  await waitFor("the rows to render", () => rowInputs(view.container).length === initial.length);
  return view;
}

test("removing a row carries the rows below it, with their nodes, up one place", async () => {
  const view = await renderRows(["A", "B", "C"]);
  stampRows(view.container);

  click(view.container, "Remove row 1");
  await waitFor("the row to go", () => rowInputs(view.container).length === 2);

  expect(rowValues(view.container)).toEqual(["B", "C"]);
  // B and C keep the nodes they had, rather than inheriting the node of the row above.
  expect(stamps(view.container)).toEqual(["s1", "s2"]);

  view.unmount();
});

test("moving a row down carries its node with it, so a second press moves the same row", async () => {
  const view = await renderRows(["A", "B", "C"]);
  stampRows(view.container);

  click(view.container, "Move row 1 down");
  await waitFor("the rows to swap", () => rowValues(view.container)[0] === "B");

  expect(rowValues(view.container)).toEqual(["B", "A", "C"]);
  // A's node is now second. Without that, the button the user is holding down belongs to B and the
  // next press swaps the pair back instead of moving A on.
  expect(stamps(view.container)).toEqual(["s1", "s0", "s2"]);

  click(view.container, "Move row 2 down");
  await waitFor("the rows to swap again", () => rowValues(view.container)[1] === "C");

  expect(rowValues(view.container)).toEqual(["B", "C", "A"]);

  view.unmount();
});

test("a list replaced from outside never moves a surviving node to another row's place", async () => {
  const view = await renderRows(["A", "B", "C", "D", "E"]);

  // Two removes leave the editor's row identities out of step with the row positions, which is the
  // state the replacement below has to be reconciled against.
  click(view.container, "Remove row 1");
  await waitFor("the first row to go", () => rowInputs(view.container).length === 4);
  click(view.container, "Remove row 1");
  await waitFor("the second row to go", () => rowInputs(view.container).length === 3);
  expect(rowValues(view.container)).toEqual(["C", "D", "E"]);

  stampRows(view.container);

  const replace = [...view.container.querySelectorAll("button")].find(
    (b) => b.textContent === "Replace from outside",
  );
  replace?.click();
  await waitFor("the replacement to render", () => rowValues(view.container)[0] === "W");

  expect(rowValues(view.container)).toEqual(["W", "X", "Y", "Z"]);
  // The replacement rows are unrelated to what was there, so the editor may rebuild any row. What it
  // must never do is hand a kept node to a different row: a node that survives stays where it was,
  // or the caret, the selection and anything else the DOM holds follows the wrong row.
  for (const [index, stamp] of stamps(view.container).entries()) {
    expect(
      stamp === "" || stamp === `s${String(index)}`,
      `row ${String(index)} holds ${stamp}`,
    ).toBe(true);
  }

  view.unmount();
});
