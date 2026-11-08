// @vitest-environment jsdom
// This test of a shared component lives in the consuming package because `react` resolves only here.
// When the replacement input is on screen. "Replace with" is a mode the user picks, so it survives an
// empty field; a value emptied from outside the control is a different thing and puts it away.
import { test, expect, afterEach } from "vitest";
import { useState } from "react";
import { render, screen, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { SegmentedReplace } from "@cove-extensions/ui-shared";

afterEach(cleanup);

function Host(props: Readonly<{ initial: string }>) {
  const [value, setValue] = useState(props.initial);
  return (
    <div>
      <button
        type="button"
        onClick={() => {
          setValue("");
        }}
      >
        Reset from outside
      </button>
      <SegmentedReplace
        value={value}
        onChange={setValue}
        stripLabel="Strip"
        replaceLabel="Replace with"
        ariaLabel="Replacement"
      />
    </div>
  );
}

const field = () => screen.queryByRole<HTMLInputElement>("textbox", { name: "Replacement" });
// `pressed` is part of the query, so a mode that is on screen but not announced as chosen is not
// found at all, rather than found and then asserted about.
const chosen = (label: string) => screen.getByRole("button", { name: label, pressed: true });

test("a stored replacement opens in replace mode with the field showing it", () => {
  render(<Host initial="_" />);

  expect(chosen("Replace with")).toBeDefined();
  expect(field()?.value).toBe("_");
});

test("choosing Replace with on an empty value reveals the field to type into", async () => {
  const user = userEvent.setup();
  render(<Host initial="" />);
  expect(field()).toBeNull();

  await user.click(screen.getByRole("button", { name: "Replace with" }));

  expect(field()).not.toBeNull();
  expect(chosen("Replace with")).toBeDefined();
});

test("emptying the field while typing leaves it on screen", async () => {
  const user = userEvent.setup();
  render(<Host initial="ab" />);

  await user.clear(field()!);

  // Collapsing here would take the caret out of a field the user is still in.
  expect(field()?.value).toBe("");
  expect(chosen("Replace with")).toBeDefined();
});

test("choosing Strip clears the value and puts the field away", async () => {
  const user = userEvent.setup();
  render(<Host initial="_" />);

  await user.click(screen.getByRole("button", { name: "Strip" }));

  expect(field()).toBeNull();
  expect(chosen("Strip")).toBeDefined();
});

test("a value emptied from outside the control returns it to strip mode", async () => {
  const user = userEvent.setup();
  render(<Host initial="_" />);
  expect(field()).not.toBeNull();

  await user.click(screen.getByRole("button", { name: "Reset from outside" }));

  expect(field()).toBeNull();
  expect(chosen("Strip")).toBeDefined();
});
