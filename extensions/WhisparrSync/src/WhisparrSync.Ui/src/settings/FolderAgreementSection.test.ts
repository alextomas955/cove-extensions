// @vitest-environment jsdom
import { afterEach, expect, test, vi } from "vitest";
import { act, createElement, type ReactNode } from "react";

import { press, render as renderNode } from "../common/lib/testRender";
import type { FolderAgreementRootLine, FolderAgreementView } from "../wire/api";
import {
  FOLDER_AGREEMENT_CHANGE,
  FOLDER_AGREEMENT_SAVE,
  FOLDER_AGREEMENT_SETTLED,
  FOLDER_AGREEMENT_UNREADABLE,
  FOLDER_AGREEMENT_WITHDRAW,
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
    StatusPill: (props: { children: ReactNode }) => h("span", null, props.children),
    // A real button, because the native disabled attribute decides whether a press can act. The
    // variant is rendered because the section's claim is that it asks for no accent control.
    Button: (props: {
      children: ReactNode;
      disabled?: boolean;
      variant?: string;
      onClick: () => void;
    }) =>
      h(
        "button",
        {
          disabled: props.disabled,
          onClick: props.onClick,
          "data-variant": props.variant ?? "primary",
        },
        props.children,
      ),
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

// GET answers taken in order, so a re-read can answer differently from the first read.
let reads: (FolderAgreementView | Error)[] = [];

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
    onWithdraw: agreement.withdraw,
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
    // The last control on a row: the row's own save or withdrawal, never the disclosure that
    // opens the field above it.
    saveFor: (root: string) => {
      const buttons = [...(promptFor(root)?.querySelectorAll("button") ?? [])];
      return buttons.length === 0 ? null : buttons[buttons.length - 1];
    },
    changeFor: (root: string) =>
      [...(promptFor(root)?.querySelectorAll("button") ?? [])].find(
        (button) => button.textContent === FOLDER_AGREEMENT_CHANGE,
      ) ?? null,
    buttonNamesFor: (root: string) =>
      [...(promptFor(root)?.querySelectorAll("button") ?? [])].map((button) => button.textContent),
    text: () => container.textContent,
  };
}

// React replaces the node's own value setter to track what it last rendered. A plain assignment
// reads back as no change and the dispatched event is dropped, so write through the prototype setter.
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

test("a folder whose stated path is working reads as settled, with its field behind the disclosure", async () => {
  reads = [viewOf(settledLineFor("/media", "/data/media"))];

  const page = await mount();

  expect(page.prompts().length).toBe(1);
  expect(page.text()).toContain(FOLDER_AGREEMENT_SETTLED);
  expect(page.text()).toContain("/data/media");
  expect(page.inputFor("/media")).toBeNull();

  await press(page.changeFor("/media"));

  expect(page.inputFor("/media")).not.toBeNull();
  expect(page.saveFor("/media")?.textContent).toContain(FOLDER_AGREEMENT_SAVE);
});

test("saving under a working path with the field left blank withdraws it", async () => {
  reads = [viewOf(settledLineFor("/media", "/data/media")), viewOf()];
  saves = { "/media": { outcome: "removed", refusal: null, tried: [] } };

  const page = await mount();
  await press(page.changeFor("/media"));
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
  await press(page.changeFor("/media"));
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

test("a folder with a stated path and nothing to probe with offers only the withdrawal", async () => {
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
  expect(page.inputFor("/media")).toBeNull();
  expect(page.buttonNamesFor("/media")).toEqual([FOLDER_AGREEMENT_WITHDRAW]);
});

test("pressing that withdrawal sends no path, whatever is left typed under the folder", async () => {
  const withdrawable: FolderAgreementRootLine = {
    root: "/media",
    refusal: "noFileToProbeWith",
    pathsTried: [],
    mapping: "/data/media",
  };
  // The draft survives the re-read that turns /media into a line with no field, because only the
  // saved folder's draft is cleared. So the press below meets a draft nothing on screen can reach.
  reads = [viewOf(lineFor("/media"), lineFor("/archive")), viewOf(withdrawable)];
  saves = { "/archive": { outcome: "stored", refusal: null, tried: ["/data/archive"] } };

  const page = await mount();
  await type(page.inputFor("/media"), "/typed/earlier");
  await type(page.inputFor("/archive"), "/data/archive");
  await press(page.saveFor("/archive"));

  expect(page.buttonNamesFor("/media")).toEqual([FOLDER_AGREEMENT_WITHDRAW]);

  saves = { "/media": { outcome: "removed", refusal: null, tried: [] } };
  await press(page.saveFor("/media"));

  expect(JSON.parse(puts()[1].body ?? "{}")).toEqual({ coveRoot: "/media", instancePath: "" });
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

  expect(page.text()).toContain(FOLDER_SAVE_STORED);

  await press(page.changeFor("/media"));

  expect(page.inputFor("/media")?.value).toBe("");
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

test("no row asks for an accent control, whatever each row is asking for", async () => {
  reads = [viewOf(lineFor("/media"), settledLineFor("/films", "/data/films"))];

  const page = await mount();

  const buttons = [...page.container.querySelectorAll("button")];
  expect(buttons.length, "the section offers nothing to press").toBeGreaterThan(1);
  expect(
    buttons.filter(
      (button) => button.dataset.variant !== undefined && button.dataset.variant !== "ghost",
    ),
    "a row asks for an accent control, which would make the whole section accent",
  ).toEqual([]);
});

test("a hairline closes a row's state off from what can be done about it", async () => {
  reads = [viewOf(lineFor("/media"))];

  const page = await mount();

  const divided = page.saveFor("/media")?.closest(".border-t");
  expect(divided, "the row's control is separated from its state by spacing alone").not.toBeNull();
  expect([...(divided?.classList ?? [])]).toEqual(
    expect.arrayContaining(["border-t", "border-border"]),
  );
});
