// @vitest-environment jsdom
// The Dry Run shell's own cancel paths. The overlay hook's keys are covered by its own suite; what is
// this component's is the scrim, and holding the dialog open while an operation is in flight.
import { test, expect, vi, afterEach } from "vitest";
import { createElement } from "react";
import { render, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { Dialog } from "./Dialog";

afterEach(cleanup);

function mount(pending: boolean) {
  const onCancel = vi.fn();
  const { container } = render(
    createElement(Dialog, {
      titleId: "t",
      pending,
      onCancel,
      children: [
        createElement("h2", { id: "t", key: "title" }, "Dry run"),
        createElement("button", { type: "button", key: "close" }, "Close"),
      ],
    }),
  );

  return {
    onCancel,
    // The backdrop is the one element the dialog hides from assistive technology, so it carries no
    // role and no name to find it by.
    scrim: () => {
      const found = container.querySelector<HTMLElement>('[aria-hidden="true"]');
      if (!found) throw new Error("no scrim");
      return found;
    },
  };
}

test("a click on the scrim cancels", async () => {
  const user = userEvent.setup();
  const view = mount(false);

  await user.click(view.scrim());

  expect(view.onCancel).toHaveBeenCalledTimes(1);
});

test("while an operation is pending neither Escape nor the scrim cancels", async () => {
  const user = userEvent.setup();
  const view = mount(true);

  await user.keyboard("{Escape}");
  await user.click(view.scrim());

  expect(view.onCancel, "the dialog closed over an operation in flight").not.toHaveBeenCalled();
});
