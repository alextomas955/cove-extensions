/**
 * The connect surface's data layer: the only place that requests the connection-test route.
 *
 * Loading, answered and failed are distinct states, because a surface that is still reading must
 * never render the answer it does not have yet.
 */
import { useCallback, useRef, useState } from "react";
import { ApiError, requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { ConnectionTestView } from "../wire/api";
import { api } from "../common/lib/extension";

const CONNECTION_TEST_PATH = api("connection/test");

/** What the surface knows about the connection right now. */
export type ConnectionState =
  | { readonly status: "idle" }
  | { readonly status: "testing"; readonly address: string }
  | { readonly status: "answered"; readonly result: ConnectionTestView }
  | { readonly status: "failed"; readonly message: string };

export interface UseConnection {
  readonly state: ConnectionState;
  /** Tests one address and key. A test started while another is in flight supersedes it. */
  readonly test: (address: string, apiKey: string) => void;
  /** Drops the current answer, and any answer still in flight. */
  readonly clear: () => void;
}

export function useConnection(): UseConnection {
  // The answer itself is state, so rendering follows it. The token that decides WHICH answer may
  // commit is a ref: it changes without a render, and a useMemo would be wrong for it twice over,
  // because a memo is a cache React may legitimately discard and a fresh token would let a superseded
  // response land over a later one.
  const issued = useRef(0);
  const [state, setState] = useState<ConnectionState>({ status: "idle" });

  const test = useCallback((address: string, apiKey: string) => {
    issued.current += 1;
    const token = issued.current;
    setState({ status: "testing", address });

    requestJson<ConnectionTestView>(CONNECTION_TEST_PATH, {
      method: "POST",
      body: JSON.stringify({ address, apiKey }),
    })
      .then((result) => {
        if (token !== issued.current) return;
        setState({ status: "answered", result });
      })
      .catch((err: unknown) => {
        if (token !== issued.current) return;
        setState({
          status: "failed",
          message: err instanceof ApiError ? `${err.status} ${err.body}` : String(err),
        });
      });
  }, []);

  const clear = useCallback(() => {
    // Retiring the token is what stops an answer already in flight from landing under a field it no
    // longer describes.
    issued.current += 1;
    setState({ status: "idle" });
  }, []);

  return { state, test, clear };
}
