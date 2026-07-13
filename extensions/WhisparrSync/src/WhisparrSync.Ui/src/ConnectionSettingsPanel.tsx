/**
 * ConnectionSettingsPanel — the minimal walking-skeleton connection form: a Whisparr URL, an API key,
 * and a Test connection button wired to POST /extensions/{id}/test-connection via the SDK. On success
 * it shows the detected instance name + version returned by the backend's real GET /api/v3/system/status
 * round-trip.
 *
 * Deliberately minimal (the skeleton): rich per-class error states, the version selector, root-folder /
 * quality-profile dropdowns, options save, and the webhook section arrive in plans 01-02 / 01-03. The
 * API-key input is `type="password"` and is never pre-filled from the server (CONN-06).
 */
import { useState } from "react";
import { request, ApiError } from "@cove/extension-sdk";
import { Button, Field, Spinner, StatusText, TextInput } from "./primitives";
import { DEFAULT_OPTIONS } from "./options";

const EXTENSION_ID = "com.alextomas955.whisparrsync";

interface TestConnectionResponse {
  status: string;
  version?: string | null;
  instanceName?: string | null;
}

type TestResult =
  | { kind: "idle" }
  | { kind: "success"; instanceName: string; version: string }
  | { kind: "error"; message: string };

export function ConnectionSettingsPanel() {
  const [baseUrl, setBaseUrl] = useState(DEFAULT_OPTIONS.BaseUrl);
  const [apiKey, setApiKey] = useState("");
  const [testing, setTesting] = useState(false);
  const [result, setResult] = useState<TestResult>({ kind: "idle" });

  async function testConnection() {
    setTesting(true);
    setResult({ kind: "idle" });
    try {
      const resp = await request<TestConnectionResponse>(
        `/extensions/${EXTENSION_ID}/test-connection`,
        { method: "POST", body: JSON.stringify({ baseUrl, apiKey }) },
      );
      if (resp.status === "connected") {
        setResult({
          kind: "success",
          instanceName: resp.instanceName ?? "Whisparr",
          version: resp.version ?? "unknown",
        });
      } else {
        setResult({
          kind: "error",
          message: "Couldn't connect to Whisparr. Check the URL and API key, then try again.",
        });
      }
    } catch (e) {
      const message =
        e instanceof ApiError
          ? `Couldn't reach Whisparr (HTTP ${e.status}). Check the URL and that Whisparr is running.`
          : "Couldn't reach Whisparr. Check the URL and that Whisparr is running.";
      setResult({ kind: "error", message });
    } finally {
      setTesting(false);
    }
  }

  return (
    <div className="space-y-4">
      <Field label="Whisparr URL" helper="The address of your Whisparr instance.">
        <TextInput value={baseUrl} onChange={setBaseUrl} placeholder="http://localhost:6969" mono />
      </Field>

      <Field label="API key" helper="Whisparr → Settings → General → API Key.">
        <input
          type="password"
          value={apiKey}
          placeholder="Your Whisparr API key"
          onChange={(e) => {
            setApiKey(e.target.value);
          }}
          className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
        />
      </Field>

      <div className="flex items-center gap-3">
        <Button
          variant="primary"
          onClick={() => {
            void testConnection();
          }}
          disabled={testing}
        >
          {testing ? <Spinner /> : null}
          Test connection
        </Button>
        {testing ? <StatusText kind="muted">Testing…</StatusText> : null}
      </div>

      <div role="status" aria-live="polite">
        {result.kind === "success" ? (
          <StatusText kind="success">
            Connected to {result.instanceName} — Whisparr {result.version}.
          </StatusText>
        ) : result.kind === "error" ? (
          <StatusText kind="error">{result.message}</StatusText>
        ) : null}
      </div>
    </div>
  );
}
