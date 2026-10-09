// @vitest-environment jsdom
import { expect, test, vi } from "vitest";
import { createElement } from "react";

const routes: string[] = [];
let answers: Record<string, unknown> = {};

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {},
  requestJson: (route: string) => {
    routes.push(route);
    const arranged = Object.entries(answers).find(([path]) => route.endsWith(path));
    return arranged === undefined
      ? Promise.reject(new Error(`no answer arranged for ${route}`))
      : Promise.resolve(arranged[1]);
  },
}));

const { useSettingsDraft } = await import("./useSettingsDraft");
const { onConnectionChanged } = await import("./connectionChangedStore");
const { press, render } = await import("../common/lib/testRender");

const STORED = {
  selectedGeneration: "v3",
  upgradeBehavior: "add",
  v3: {
    address: "http://whisparr-v3:6969",
    keyIsSet: true,
    recordedVersion: null,
    versionVerifiedAtUtc: null,
    lastReachableAtUtc: null,
  },
  v2: {
    address: "",
    keyIsSet: false,
    recordedVersion: null,
    versionVerifiedAtUtc: null,
    lastReachableAtUtc: null,
  },
};

const CONNECTED = { kind: "connected", generation: "v3", version: "3.6.2.1727", capabilities: [] };

// Two controls, so a test of the stored connection and a test of a typed address are both reachable
// through the hook's own surface rather than through a page of sections.
function Harness() {
  const { state, editAddress, test: runTest } = useSettingsDraft(() => undefined);
  return createElement("div", null, [
    createElement(
      "button",
      {
        key: "type",
        type: "button",
        onClick: () => {
          editAddress("http://elsewhere:6969");
        },
      },
      "type an address",
    ),
    createElement(
      "button",
      { key: "test", type: "button", disabled: state.settings === null, onClick: runTest },
      "test",
    ),
  ]);
}

function arrange(): { heard: () => number; stop: () => void } {
  routes.length = 0;
  answers = { settings: STORED, "connection/test": CONNECTED };
  let count = 0;
  const stop = onConnectionChanged(() => count++);
  return { heard: () => count, stop };
}

// The stored test is the one call that re-reads the callback registration off the instance, so the
// sections holding an answer from before it have to read again. Without the announcement the
// webhook section goes on reporting a registration the instance no longer holds until a reload.
test("a test of the stored connection tells the page's sections to read again", async () => {
  const { heard, stop } = arrange();

  const container = await render(createElement(Harness));
  await press(container.querySelectorAll("button")[1]);

  expect(routes.some((route) => route.endsWith("connection/test"))).toBe(true);
  expect(heard()).toBe(1);
  stop();
});

// A test of an address typed into the form reached an instance the user may only be considering,
// and records nothing against the stored connection the sections read under.
test("a test of a typed address announces nothing", async () => {
  const { heard, stop } = arrange();

  const container = await render(createElement(Harness));
  const buttons = container.querySelectorAll("button");
  await press(buttons[0]);
  await press(buttons[1]);

  expect(routes.some((route) => route.endsWith("connection/test"))).toBe(true);
  expect(heard()).toBe(0);
  stop();
});
