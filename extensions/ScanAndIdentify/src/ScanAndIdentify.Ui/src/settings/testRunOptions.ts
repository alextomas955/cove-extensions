/**
 * The stand-in server and the DOM lookups the options suites share.
 *
 * The request double refuses a call no test configured, so a suite that forgets to seed a response
 * fails loudly instead of rendering something nobody chose.
 */
import type { RunOptions } from "./runOptions";

/** One request the form issued through the write stand-in. */
export interface RecordedWrite {
  path: string;
  method: string | undefined;
  body: string | undefined;
}

export const server = {
  /** What the options read answers with. Null until a test sets it. */
  respond: null as (() => Promise<RunOptions>) | null,
  writes: [] as RecordedWrite[],
  /** What the write fails with. Null means it succeeds. */
  writeError: null as Error | null,
};

/** Resets the stand-in and the document between tests. */
export function resetServer(): void {
  server.respond = null;
  server.writes = [];
  server.writeError = null;
  document.body.replaceChildren();
}

/** The module shape `vi.mock` returns for `@cove-extensions/ui-shared/extensionRequest`. */
export function extensionRequestDouble() {
  return {
    errorText: (err: unknown) => (err instanceof Error ? err.message : String(err)),
    requestJson: (path: string) => {
      if (server.respond === null) throw new Error(`unconfigured request to ${path}`);
      return server.respond();
    },
    request: (path: string, options: RequestInit) => {
      server.writes.push({
        path,
        method: options.method,
        body: typeof options.body === "string" ? options.body : undefined,
      });
      return server.writeError === null
        ? Promise.resolve(undefined)
        : Promise.reject(server.writeError);
    },
  };
}

/**
 * A whole record, the way the route answers one. Copied from the C# record's declared defaults, so a
 * field the server stops writing shows up here as a type error rather than as an invented value.
 */
export const answered = (overrides: Partial<RunOptions> = {}): RunOptions => ({
  thumbnails: true,
  previews: true,
  sprites: true,
  vrStereo: false,
  imageThumbnails: true,
  galleryThumbnails: true,
  backfillFingerprints: true,
  rescan: false,
  createMissingEntities: true,
  markOrganized: false,
  ...overrides,
});

/**
 * The switch a label names.
 *
 * jsdom computes no accessible name, so the pairing is read the way the browser builds it: the
 * `label` whose text is this name, and the control its `for` points at.
 */
export function switchNamed(container: HTMLElement, name: string): HTMLButtonElement {
  const label = [...container.querySelectorAll("label")].find((e) => e.textContent.trim() === name);
  const control =
    label === undefined ? null : container.querySelector(`#${CSS.escape(label.htmlFor)}`);
  if (!(control instanceof HTMLButtonElement)) throw new Error(`no switch named ${name}`);
  return control;
}

/** The state every switch is in, keyed by the label that names it. */
export function switchStates(container: HTMLElement): Record<string, boolean> {
  const states: Record<string, boolean> = {};
  for (const control of container.querySelectorAll<HTMLButtonElement>('button[role="switch"]')) {
    const label = control.closest("label");
    states[label?.textContent.trim() ?? control.id] =
      control.getAttribute("aria-checked") === "true";
  }
  return states;
}

/** The button whose text reads exactly `text`. */
export function buttonNamed(container: HTMLElement, text: string): HTMLButtonElement {
  const found = [...container.querySelectorAll("button")].find(
    (e) => e.textContent.trim() === text,
  );
  if (found === undefined) throw new Error(`no button reading ${text}`);
  return found;
}
