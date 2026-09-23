// @vitest-environment jsdom
import { expect, test } from "vitest";
import { createElement } from "react";

import { render } from "../common/lib/testRender";
import type { CallbackView } from "../wire/api";
import { ImportWebhookSection } from "./ImportWebhookSection";

const REGISTERED: CallbackView = {
  generation: "v3",
  status: "registered",
  copyableAddress: "http://cove:5073/api/extensions/whisparrsync/callback?s=secret",
  registeredAddress: "http://cove:5073/api/extensions/whisparrsync/callback",
  secretTravelsOutOfBand: true,
  lastEventSecretPosition: "outOfBand",
  missingSetting: null,
  refusal: null,
  registrationIsSafe: true,
};

const NOT_CHECKED: CallbackView = { ...REGISTERED, status: "notCheckedYet" };

function section(overrides: {
  view?: CallbackView | null;
  readFailed?: boolean;
  sharedReason?: string;
}) {
  return createElement(ImportWebhookSection, {
    view: overrides.view === undefined ? REGISTERED : overrides.view,
    readFailed: overrides.readFailed ?? false,
    address: REGISTERED.copyableAddress,
    registering: false,
    registerError: null,
    copyResult: { status: "idle" },
    sharedReason: overrides.sharedReason ?? null,
    onAddressChange: () => undefined,
    onCopy: () => undefined,
    onRegister: () => undefined,
  });
}

test("the callback address label is an uppercase mono micro-label", async () => {
  const host = await render(section({}));

  const label = [...host.querySelectorAll("span")].find(
    (span) => span.textContent === "Callback address",
  );
  expect(label, "the section draws no label for the callback address").toBeDefined();
  expect([...(label?.classList ?? [])]).toEqual(
    expect.arrayContaining(["uppercase", "font-mono", "text-xs", "tracking-wide"]),
  );
});

test("the section offers one accent control, and it is the one that registers", async () => {
  const host = await render(section({}));

  const buttons = [...host.querySelectorAll("button")];
  expect(buttons.length, "the section offers nothing to press").toBeGreaterThan(1);
  const accent = buttons.filter((button) => button.className.split(" ").includes("bg-accent"));
  expect(accent, "the section draws more than one accent control").toHaveLength(1);
  expect(
    accent[0].textContent.startsWith("Register in Whisparr"),
    "the section's accent control is something other than the registration",
  ).toBe(true);
});

test("a hairline closes the address off from the controls that hand it over", async () => {
  const host = await render(section({}));

  const register = [...host.querySelectorAll("button")].find((button) =>
    button.textContent.startsWith("Register in Whisparr"),
  );
  const divided = register?.closest(".border-t");
  expect(divided, "the controls are separated from the address by spacing alone").not.toBeNull();
  expect([...(divided?.classList ?? [])]).toEqual(
    expect.arrayContaining(["border-t", "border-border"]),
  );
});

test("the status tells reading, checked, never checked and unreadable apart", async () => {
  const reading = await render(section({ view: null }));
  const checked = await render(section({}));
  const never = await render(section({ view: NOT_CHECKED }));
  const failed = await render(section({ view: null, readFailed: true }));

  const sentences = [reading, checked, never, failed].map((host) => host.textContent);
  expect(sentences[0]).toContain("Reading the callback status…");
  expect(sentences[1]).toContain("Registered, and imports are reaching Cove through it.");
  expect(sentences[2]).toContain("Cove has not checked this instance for its callback yet.");
  expect(sentences[3]).toContain("Cove could not read the callback status.");
  expect(new Set(sentences).size, "two of the four read the same").toBe(4);
});

test("the shared reason takes the registration out without repeating itself beside it", async () => {
  const reason = "Cove could not read the stored connection.";
  const host = await render(section({ sharedReason: reason }));

  const refused = [...host.querySelectorAll("button")].filter((button) => button.disabled);
  expect(refused.length, "nothing was refused while the store was unreadable").toBeGreaterThan(0);
  for (const button of refused) {
    expect(
      button.textContent.endsWith(reason),
      "a refusal ends with something other than its reason",
    ).toBe(true);
    expect(button.textContent, "a refused control is its reason and nothing else").not.toBe(reason);
  }
  const stated = host.textContent.split(reason).length - 1;
  expect(stated, "the section states the shared reason somewhere other than on a control").toBe(
    refused.length,
  );
});
