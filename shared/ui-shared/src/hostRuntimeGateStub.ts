/**
 * The offline gates' stand-in for `@cove/runtime/api`.
 *
 * A gate compiles and runs a slice of bundle source outside a browser and outside the host, so the host's
 * authenticated fetch cannot exist there. Any gated module that reaches the transport is being exercised
 * for its PURE behavior; a call that actually reaches this stub means the gate wandered into I/O, so it
 * throws rather than resolving a fake response — a silent fake would let a gate pass while asserting
 * nothing about the code under test.
 *
 * It is wired per gate through the manifest's `aliases`, never imported by bundle source.
 */
export function extensionFetch(
  _input: string,
  _init?: RequestInit,
): Promise<Response> {
  return Promise.reject(
    new Error(
      "extensionFetch is unavailable in an offline gate: this path must not reach the transport.",
    ),
  );
}
