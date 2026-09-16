// @vitest-environment jsdom
/**
 * What the settings page shows for a folder Whisparr could not be shown to hold and for one whose
 * stated path is working, and what pressing save puts to Cove.
 *
 * The hook and the section are mounted together, because the properties under test span both: which
 * lines are on screen after a save is what the re-read answers, and what the save carried is a
 * request rather than a value a component returns.
 *
 * The fake refuses a call it was not configured for, so a surface reaching a route nobody arranged
 * fails here rather than resolving to a convenient default.
 */
import { afterEach, expect, test, vi } from "vitest";
import { act, createElement, type ReactNode } from "react";

import { press, render as renderNode } from "../common/lib/testRender";
import type { FolderAgreementRootLine, FolderAgreementView } from "../wire/api";
import {
  FOLDER_AGREEMENT_SAVE,
  FOLDER_AGREEMENT_SETTLED,
  FOLDER_AGREEMENT_UNREADABLE,
  FOLDER_NOTHING_RESOLVED,
  FOLDER_SAVE_STORED,
} from "../common/ui/copy";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    SectionCard: (props: { title?: string; description?: string; children: ReactNode }) =>
      h("section", null, props.title, props.description, props.children),
    StatusText: (props: { children: ReactNode }) => h("span", null, props.children),
    Spinner: () => h("span", { "data-spinner": "true" }, "…"),
    Field: (props: { label: string; helper?: string; children: ReactNode }) =>
      h("label", null, props.label, props.children, props.helper),
    // The native attribute is what decides whether a press can act, so the button is a real one.
    Button: (props: { children: ReactNode; disabled?: boolean; onClick: () => void }) =>
      h("button", { disabled: props.disabled, onClick: props.onClick }, props.children),
    INPUT_CLASS: "",
    extensionApi: (id: string) => (path: string) => `/extensions/${id}/${path}`,
  };
});

interface Sent {
  readonly path: string;
  readonly method: string;
  readonly body: string | undefined;
}

const sent: Sent[] = [];

/** The GET answers, taken in order, so a re-read can answer differently from the first read. */
let reads: (FolderAgreementView | Error)[] = [];

/** What the PUT answers, keyed by the folder the request names. */
let saves: Record<string, unknown> = {};

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: (path: string, init?: RequestInit): Promise<unknown> => {
    const method = init?.method ?? "GET";
    const body = typeof init?.body === "string" ? init.body : undefined;
    sent.push({ path, method, body });

    if (method === "GET") {
      const answer = reads.shift();
      if (answer === undefined) return Promise.reject(new Error(`no read arranged for ${path}`));
      return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
    }
    if (method === "PUT") {
      const root = (JSON.parse(body ?? "{}") as { coveRoot?: string }).coveRoot ?? "";
      const answer = saves[root];
      if (answer === undefined) return Promise.reject(new Error(`no save arranged for ${root}`));
      return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
    }
    return Promise.reject(new Error(`no ${method} arranged for ${path}`));
  },
}));

const { FolderAgreementSection } = await import("./FolderAgreementSection");
const { useFolderAgreement } = await import("./useFolderAgreement");

afterEach(() => {
  sent.length = 0;
  reads = [];
  saves = {};
});

function lineFor(
  root: string,
  pathsTried: string[] = [`${root}/scene/clip.mp4`],
  mapping: string | null = null,
): FolderAgreementRootLine {
  return { root, refusal: "nothingResolved", pathsTried, mapping };
}

function settledLineFor(root: string, mapping: string): FolderAgreementRootLine {
  return { root, refusal: null, pathsTried: [], mapping };
}

function viewOf(...roots: FolderAgreementRootLine[]): FolderAgreementView {
  return { roots };
}

function Page() {
  const agreement = useFolderAgreement();
  return createElement(FolderAgreementSection, {
    read: agreement.read,
    view: agreement.view,
    drafts: agreement.drafts,
    saving: agreement.saving,
    answers: agreement.answers,
    onPathChange: agreement.editPath,
    onSave: agreement.save,
  });
}

async function mount() {
  const container = await renderNode(createElement(Page));
  const prompts = () => container.querySelectorAll("li[data-root]");
  const promptFor = (root: string) =>
    container.querySelector<HTMLElement>(`li[data-root="${root}"]`);
  return {
    container,
    prompts,
    promptFor,
    roots: () => [...prompts()].map((item) => item.getAttribute("data-root")),
    inputFor: (root: string) => promptFor(root)?.querySelector("input") ?? null,
    saveFor: (root: string) => promptFor(root)?.querySelector("button") ?? null,
    text: () => container.textContent,
  };
}

/**
 * Types `value` into `input` the way a person does.
 *
 * React replaces the node's own `value` setter to track what it last rendered, so a plain assignment
 * is read back as no change and the dispatched event is dropped. Writing through the prototype's
 * setter is what a keystroke does.
 */
async function type(input: HTMLInputElement | null, value: string) {
  if (input === null) throw new Error("No path field found to type into");
  // eslint-disable-next-line @typescript-eslint/unbound-method -- called with `input` as its `this`.
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")?.set;
  await act(() => {
    setter?.call(input, value);
    input.dispatchEvent(new Event("input", { bubbles: true }));
    return Promise.resolve();
  });
}

const puts = () => sent.filter((call) => call.method === "PUT");

test("a page with every folder resolved shows nothing at all", async () => {
  reads = [viewOf()];

  const page = await mount();

  expect(page.prompts().length).toBe(0);
  expect(page.text()).toBe("");
});

test("one unresolved folder shows one prompt, naming it and the path that was tried", async () => {
  reads = [viewOf(lineFor("/media"))];

  const page = await mount();

  expect(page.prompts().length).toBe(1);
  expect(page.text()).toContain("/media");
  expect(page.text()).toContain(FOLDER_NOTHING_RESOLVED);
  expect(page.text()).toContain("/media/scene/clip.mp4");
});

test("two unresolved folders show two prompts, each with its own field", async () => {
  reads = [viewOf(lineFor("/media"), lineFor("/archive"))];

  const page = await mount();

  expect(page.roots()).toEqual(["/media", "/archive"]);
  expect(page.inputFor("/media")).not.toBeNull();
  expect(page.inputFor("/archive")).not.toBeNull();
});

test("a folder whose stated path is working shows one line, naming the path and offering a field", async () => {
  reads = [viewOf(settledLineFor("/media", "/data/media"))];

  const page = await mount();

  expect(page.prompts().length).toBe(1);
  expect(page.text()).toContain(FOLDER_AGREEMENT_SETTLED);
  expect(page.text()).toContain("/data/media");
  expect(page.inputFor("/media")).not.toBeNull();
  expect(page.saveFor("/media")).not.toBeNull();
});

test("saving under a working path with the field left blank withdraws it", async () => {
  reads = [viewOf(settledLineFor("/media", "/data/media")), viewOf()];
  saves = { "/media": { outcome: "removed", refusal: null, tried: [] } };

  const page = await mount();
  await press(page.saveFor("/media"));

  expect(JSON.parse(puts()[0].body ?? "{}")).toEqual({ coveRoot: "/media", instancePath: "" });
  expect(page.prompts().length).toBe(0);
});

test("a folder nothing resolved for and one whose path works show a field each", async () => {
  reads = [viewOf(lineFor("/archive"), settledLineFor("/media", "/data/media"))];
  let settle: (result: unknown) => void = () => undefined;
  saves = {
    "/archive": new Promise((resolve) => {
      settle = resolve;
    }),
  };

  const page = await mount();
  expect(page.inputFor("/archive")).not.toBeNull();
  expect(page.inputFor("/media")).not.toBeNull();

  await type(page.inputFor("/archive"), "/data/archive");
  await press(page.saveFor("/archive"));

  expect(page.saveFor("/media")?.disabled).toBe(true);

  await act(() => {
    settle({ outcome: "refused", refusal: "nothingResolved", tried: ["/data/archive"] });
    return Promise.resolve();
  });

  expect(page.saveFor("/media")?.disabled).toBe(false);
});

test("a folder with nothing to ask about states it and offers no field", async () => {
  reads = [viewOf({ root: "/empty", refusal: "noFileToProbeWith", pathsTried: [], mapping: null })];

  const page = await mount();

  expect(page.prompts().length).toBe(1);
  expect(page.inputFor("/empty")).toBeNull();
  expect(page.saveFor("/empty")).toBeNull();
});

test("a folder with a stated path and nothing to probe with offers the field that withdraws it", async () => {
  reads = [
    viewOf({
      root: "/media",
      refusal: "noFileToProbeWith",
      pathsTried: [],
      mapping: "/data/media",
    }),
  ];

  const page = await mount();

  expect(page.text()).toContain("/data/media");
  expect(page.inputFor("/media")).not.toBeNull();
  expect(page.saveFor("/media")).not.toBeNull();
});

test("saving sends the folder it is under and the path that was typed, and nothing else", async () => {
  reads = [viewOf(lineFor("/media"), lineFor("/archive"))];
  saves = { "/media": { outcome: "refused", refusal: "nothingResolved", tried: ["/data/media"] } };

  const page = await mount();
  await type(page.inputFor("/media"), "/data/media");
  await press(page.saveFor("/media"));

  expect(puts()).toHaveLength(1);
  expect(puts()[0].path.endsWith("/addressing/folder-mappings")).toBe(true);
  expect(JSON.parse(puts()[0].body ?? "{}")).toEqual({
    coveRoot: "/media",
    instancePath: "/data/media",
  });
});

test("a save that did not resolve leaves the prompt standing and says what was tried", async () => {
  reads = [viewOf(lineFor("/media"))];
  saves = {
    "/media": { outcome: "refused", refusal: "nothingResolved", tried: ["/wrong/media"] },
  };

  const page = await mount();
  await type(page.inputFor("/media"), "/wrong");
  await press(page.saveFor("/media"));

  expect(page.prompts().length).toBe(1);
  expect(page.text()).toContain("/wrong/media");
});

test("a stored path leaves the field under that folder blank, ready to withdraw it", async () => {
  reads = [viewOf(lineFor("/media")), viewOf(settledLineFor("/media", "/data/media"))];
  saves = { "/media": { outcome: "stored", refusal: null, tried: ["/data/media"] } };

  const page = await mount();
  await type(page.inputFor("/media"), "/data/media");
  await press(page.saveFor("/media"));

  expect(page.inputFor("/media")?.value).toBe("");
  expect(page.text()).toContain(FOLDER_SAVE_STORED);
});

test("a save that was refused leaves the typed path in the field to be corrected", async () => {
  reads = [viewOf(lineFor("/media"))];
  saves = { "/media": { outcome: "refused", refusal: "nothingResolved", tried: ["/wrong/media"] } };

  const page = await mount();
  await type(page.inputFor("/media"), "/wrong/media");
  await press(page.saveFor("/media"));

  expect(page.inputFor("/media")?.value).toBe("/wrong/media");
});

test("a save that resolved removes its own prompt and leaves the other standing", async () => {
  reads = [viewOf(lineFor("/media"), lineFor("/archive")), viewOf(lineFor("/archive"))];
  saves = { "/media": { outcome: "stored", refusal: null, tried: ["/data/media"] } };

  const page = await mount();
  await type(page.inputFor("/media"), "/data/media");
  await press(page.saveFor("/media"));

  expect(page.roots()).toEqual(["/archive"]);
});

test("the field and the control cannot act while that folder's save is in flight", async () => {
  reads = [viewOf(lineFor("/media"))];
  let settle: (result: unknown) => void = () => undefined;
  saves = {
    "/media": new Promise((resolve) => {
      settle = resolve;
    }),
  };

  const page = await mount();
  await type(page.inputFor("/media"), "/data/media");
  await press(page.saveFor("/media"));

  expect(page.inputFor("/media")?.disabled).toBe(true);
  expect(page.saveFor("/media")?.disabled).toBe(true);

  await act(() => {
    settle({ outcome: "refused", refusal: "nothingResolved", tried: ["/data/media"] });
    return Promise.resolve();
  });

  expect(page.saveFor("/media")?.disabled).toBe(false);
});

test("a read that failed says so rather than reading as a page with no problems", async () => {
  reads = [new Error("the read did not answer")];

  const page = await mount();

  expect(page.text()).toContain(FOLDER_AGREEMENT_UNREADABLE);
  expect(page.prompts().length).toBe(0);
});

test("the control is named", async () => {
  reads = [viewOf(lineFor("/media"))];

  const page = await mount();

  expect(page.saveFor("/media")?.textContent).toContain(FOLDER_AGREEMENT_SAVE);
});
