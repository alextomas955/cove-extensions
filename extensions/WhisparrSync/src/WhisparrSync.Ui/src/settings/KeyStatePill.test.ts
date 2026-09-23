// @vitest-environment jsdom
import { expect, test } from "vitest";
import { createElement } from "react";

import { render } from "../common/lib/testRender";
import {
  KEY_PILL_OFFSET_PX,
  KEY_PILL_WIDTH_PX,
  KeyStateField,
  KEY_FIELD_PADDING_RIGHT_PX,
} from "./KeyStatePill";

function field(storedKeyIsSet: boolean | null, value = "") {
  return createElement(KeyStateField, {
    id: "key",
    value,
    storedKeyIsSet,
    onChange: () => undefined,
  });
}

test("a stored key is reported on the field itself, and no part of a key is drawn", async () => {
  const typed = "e2ewriteonly7c41b9a6d2f80e35a1c4";
  const host = await render(field(true, typed));

  const input = host.querySelector("input");
  expect(input?.type).toBe("password");

  const pill = [...host.querySelectorAll("span")].filter(
    (span) => span.textContent === "Key is set" && span.childElementCount === 0,
  );
  expect(pill, "the field reported nothing about the stored key").toHaveLength(1);

  expect(host.textContent).not.toContain(typed);
  expect(host.textContent).not.toContain(typed.slice(0, 4));
});

test("no stored key is its own state, not the absence of the pill", async () => {
  const host = await render(field(false));

  expect(host.textContent).toContain("Key not stored");
});

test("a generation whose stored values have not arrived claims nothing about its key", async () => {
  const host = await render(field(null));

  expect(host.textContent).toBe("");
});

test("the field's right padding clears the pill it carries", async () => {
  const host = await render(field(true));

  const input = host.querySelector("input");
  expect(input?.style.paddingRight).toBe(`${KEY_FIELD_PADDING_RIGHT_PX}px`);
  expect(KEY_FIELD_PADDING_RIGHT_PX).toBeGreaterThanOrEqual(KEY_PILL_WIDTH_PX + KEY_PILL_OFFSET_PX);
});

test("a press over the pill reaches the field under it", async () => {
  const host = await render(field(true));

  const pill = [...host.querySelectorAll("span")].find((span) => span.textContent === "Key is set");
  const positioned = pill?.closest(".absolute");
  expect(positioned, "the pill is not positioned over the field").not.toBeNull();
  expect(
    positioned?.classList.contains("pointer-events-none"),
    "the pill takes presses meant for the field",
  ).toBe(true);
});

test("the field refuses the credential a browser saved for this origin", async () => {
  const host = await render(field(true));
  const input = host.querySelector("input");

  // `off` is ignored on a password field, so the sign-in password gets filled in here and reads as
  // a typed key. Only `new-password` suppresses it.
  expect(input?.getAttribute("autocomplete")).toBe("new-password");
});
