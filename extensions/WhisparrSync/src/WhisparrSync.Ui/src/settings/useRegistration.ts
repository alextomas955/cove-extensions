/**
 * The import webhook's data layer: the only place that reads the callback status, registers the
 * callback, or puts the address on the clipboard.
 *
 * The status comes from what the last registration attempt recorded, not from asking Whisparr, so
 * opening the page cannot turn an outbound failure into an apparent absence.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { CallbackView } from "../wire/api";
import { api } from "../common/lib/extension";

const CALLBACK_STATUS_PATH = api("callback/status");
const CALLBACK_REGISTER_PATH = api("callback/register");

export type CopyResult =
  { readonly status: "idle" } | { readonly status: "copied" } | { readonly status: "failed" };

export interface UseRegistration {
  /** Null while the read is in flight, and after one that failed. */
  readonly view: CallbackView | null;
  readonly readFailed: boolean;
  /** The address the field shows: the form carrying the secret, as edited. */
  readonly address: string;
  readonly editAddress: (next: string) => void;
  readonly registering: boolean;
  readonly registerError: string | null;
  readonly register: () => void;
  readonly copy: () => void;
  readonly copyResult: CopyResult;
}

function messageFor(err: unknown): string {
  return err instanceof ApiError ? `${String(err.status)} ${err.body}` : String(err);
}

export function useRegistration(): UseRegistration {
  const [view, setView] = useState<CallbackView | null>(null);
  const [readFailed, setReadFailed] = useState(false);
  const [address, setAddress] = useState("");
  const [registering, setRegistering] = useState(false);
  const [registerError, setRegisterError] = useState<string | null>(null);
  const [copyResult, setCopyResult] = useState<CopyResult>({ status: "idle" });

  // Once the field has been edited, a later answer must not type over it.
  const edited = useRef(false);

  const take = useCallback((answer: CallbackView) => {
    setView(answer);
    setReadFailed(false);
    if (!edited.current) {
      setAddress(answer.copyableAddress);
    }
  }, []);

  const primed = useRef(false);
  useEffect(() => {
    if (primed.current) return;
    primed.current = true;
    requestJson<CallbackView>(CALLBACK_STATUS_PATH)
      .then((answer) => {
        take(answer);
      })
      .catch(() => {
        setReadFailed(true);
      });
  }, [take]);

  const editAddress = useCallback((next: string) => {
    edited.current = true;
    setAddress(next);
  }, []);

  const register = useCallback(() => {
    setRegistering(true);
    setRegisterError(null);
    requestJson<CallbackView>(CALLBACK_REGISTER_PATH, {
      method: "POST",
      body: JSON.stringify({ callbackAddress: address }),
    })
      .then((answer) => {
        // The edit is stored, so the answer's address is authoritative again.
        edited.current = false;
        take(answer);
      })
      .catch((err: unknown) => {
        setRegisterError(messageFor(err));
      })
      .finally(() => {
        setRegistering(false);
      });
  }, [address, take]);

  const copy = useCallback(() => {
    // The DOM types declare the clipboard as always present, but a Cove reached over plain http
    // is not a secure context and has none, so the property access itself can throw.
    try {
      navigator.clipboard
        .writeText(address)
        .then(() => {
          setCopyResult({ status: "copied" });
        })
        .catch(() => {
          setCopyResult({ status: "failed" });
        });
    } catch {
      setCopyResult({ status: "failed" });
    }
  }, [address]);

  return {
    view,
    readFailed,
    address,
    editAddress,
    registering,
    registerError,
    register,
    copy,
    copyResult,
  };
}
