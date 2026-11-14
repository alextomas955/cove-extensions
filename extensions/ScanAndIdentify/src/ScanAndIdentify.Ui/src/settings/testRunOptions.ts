/**
 * The stand-in Cove and the DOM lookups the options suites share.
 *
 * The seam is the host's own fetch, which the generated client reaches through the adapter in
 * common/lib/wireClient, so what is scripted here is the request the form really issues. A read no
 * test configured is refused, so a suite that forgets to seed a response fails loudly instead of
 * rendering something nobody chose.
 */
import type { RunOptions } from "./runOptions";

/** One write the form issued through the stand-in. */
export interface RecordedWrite {
  path: string;
  method: string | undefined;
  body: string | undefined;
}

/** A reply the server refuses with: the status it sends and the body it writes. */
export interface Refusal {
  status: number;
  body: string;
}

/** A refusal the stand-in answers with, in the two parts an error branch reads. */
export const refusal = (status: number, body: string): Refusal => ({ status, body });

export const server = {
  /** What the options read answers with. Null until a test sets it. */
  respond: null as (() => Promise<RunOptions>) | null,
  /**
   * The document the last write stored, which every later read answers with.
   *
   * The PUT replaces the whole document, so a read after it reports what was written. Modelled
   * here because the hook asks the server rather than assuming either.
   */
  stored: null as RunOptions | null,
  /** What the options read fails with, in place of answering. */
  readError: null as Refusal | null,
  writes: [] as RecordedWrite[],
  /** What the write fails with. Null means it succeeds. */
  writeError: null as Refusal | null,
};

/** Resets the stand-in and the document between tests. */
export function resetServer(): void {
  server.respond = null;
  server.stored = null;
  server.readError = null;
  server.writes = [];
  server.writeError = null;
  document.body.replaceChildren();
}

// The generated client reads a reply by its Content-Type, so every reply carries one: a bare
// Response takes a different path through the client than the server's own reply does.
// A bodyless reply carries no Content-Type: the generated client reads a reply by that header, and
// a 204 that claims JSON is parsed as an empty document and fails with "Unexpected end of JSON
// input" before any handler sees the status.
const reply = (value: unknown, status = 200): Response =>
  value === undefined
    ? new Response(null, { status: status === 200 ? 204 : status })
    : new Response(JSON.stringify(value), {
        status,
        headers: { "Content-Type": "application/json" },
      });

const refused = ({ status, body }: Refusal): Response =>
  new Response(body, { status, headers: { "Content-Type": "application/json" } });

/** The module shape `vi.mock` returns for `@cove/runtime/api`. */
export function coveRuntimeApiDouble() {
  return {
    extensionFetch: async (path: string, init: RequestInit = {}): Promise<Response> => {
      // Recorded without the `/api` prefix every caller shares, so a recorded path reads as the
      // route rather than as the mount point.
      const route = path.startsWith("/api") ? path.slice(4) : path;
      const method = (init.method ?? "GET").toUpperCase();

      if (method === "PUT") {
        const body = typeof init.body === "string" ? init.body : undefined;
        server.writes.push({ path: route, method, body });
        if (server.writeError !== null) return refused(server.writeError);
        if (body !== undefined) server.stored = JSON.parse(body) as RunOptions;
        return reply(undefined);
      }

      if (server.readError !== null) return refused(server.readError);
      if (server.stored !== null) return reply(server.stored);
      if (server.respond === null) throw new Error(`unconfigured request to ${route}`);
      return reply(await server.respond());
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
