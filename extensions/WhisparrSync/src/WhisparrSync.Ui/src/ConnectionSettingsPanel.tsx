/**
 * ConnectionSettingsPanel — the connection form: a Whisparr URL, an API key, a Whisparr-version selector,
 * and a Test connection button wired to POST /extensions/{id}/test-connection via the SDK. The backend
 * returns a `result` discriminator (CONN-02); each of the four failure classes renders its own copy + icon
 * + color (never a single generic "failed"), and a non-v3 instance is refused with an amber advisory
 * (VER-04) rather than a silent success. On success the version selector auto-selects the detected version
 * (CONN-04). The API-key input is `type="password"` and is never pre-filled from the server (CONN-06).
 *
 * Root-folder / quality-profile dropdowns, options save, and the webhook section arrive in plan 01-03.
 */
import { useState } from "react";
import { CheckCircle2, XCircle, AlertTriangle } from "lucide-react";
import { request } from "@cove/extension-sdk";
import { Badge, Button, Chip, Field, Spinner, StatusText, TextInput } from "./primitives";
import { DEFAULT_OPTIONS } from "./options";
import {
  connectionCopy,
  selectorForDetected,
  type ConnectionResult,
  type ConnectionTone,
  type WhisparrVersion,
} from "./connectionResult";

const EXTENSION_ID = "com.alextomas955.whisparrsync";

interface TestConnectionResponse {
  result: ConnectionResult["kind"];
  version?: string | null;
  instanceName?: string | null;
  reason?: string | null;
  detected?: string | null;
}

const VERSION_OPTIONS: readonly { value: WhisparrVersion; label: string }[] = [
  { value: "v3", label: "v3 (Eros)" },
  { value: "v2", label: "v2" },
];

function ToneIcon({ tone }: { tone: ConnectionTone }) {
  if (tone === "success") {
    return <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-green-400" />;
  }
  if (tone === "error") {
    return <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-red-400" />;
  }
  return <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-400" />;
}

/**
 * The distinct-state result banner: composes a lucide status icon + StatusText (+ a version Badge on
 * success) per the classified result. Presentational; `role="status" aria-live="polite"` announces the
 * outcome to assistive tech.
 */
function ConnectionResultBanner({ result }: { result: ConnectionResult }) {
  const copy = connectionCopy(result);
  return (
    <div role="status" aria-live="polite" className="flex items-start gap-2">
      <ToneIcon tone={copy.tone} />
      <StatusText kind={copy.tone}>{copy.message}</StatusText>
      {result.kind === "success" && result.version ? <Badge mono>{result.version}</Badge> : null}
    </div>
  );
}

export function ConnectionSettingsPanel() {
  const [baseUrl, setBaseUrl] = useState(DEFAULT_OPTIONS.BaseUrl);
  const [apiKey, setApiKey] = useState("");
  const [selectedVersion, setSelectedVersion] = useState<WhisparrVersion>(
    DEFAULT_OPTIONS.SelectedVersion,
  );
  const [autoDetected, setAutoDetected] = useState(false);
  const [testing, setTesting] = useState(false);
  const [result, setResult] = useState<ConnectionResult | null>(null);

  async function testConnection() {
    setTesting(true);
    setResult(null);
    try {
      const resp = await request<TestConnectionResponse>(
        `/extensions/${EXTENSION_ID}/test-connection`,
        { method: "POST", body: JSON.stringify({ baseUrl, apiKey }) },
      );
      switch (resp.result) {
        case "success": {
          setResult({
            kind: "success",
            instanceName: resp.instanceName ?? "Whisparr",
            version: resp.version ?? "unknown",
          });
          const detected = selectorForDetected(resp.version);
          if (detected) {
            setSelectedVersion(detected);
            setAutoDetected(true);
          }
          break;
        }
        case "badKey":
          setResult({ kind: "badKey" });
          break;
        case "notWhisparr":
          setResult({ kind: "notWhisparr" });
          break;
        case "versionMismatch":
          setResult({ kind: "versionMismatch", detected: resp.detected ?? "an unknown version" });
          setAutoDetected(false);
          break;
        default:
          setResult({ kind: "unreachable", url: baseUrl });
          break;
      }
    } catch {
      // A thrown request (the endpoint itself failed / the URL didn't answer) is the unreachable class;
      // the backend returns a 200 discriminator for every reachable-Whisparr outcome (bad key, HTML, etc.).
      setResult({ kind: "unreachable", url: baseUrl });
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

      <Field
        label="Whisparr version"
        helper={autoDetected ? "Detected v3 (Eros) — change only if this is wrong." : undefined}
      >
        <div className="flex gap-2">
          {VERSION_OPTIONS.map((o) => (
            <Chip
              key={o.value}
              selected={selectedVersion === o.value}
              onClick={() => {
                setSelectedVersion(o.value);
                setAutoDetected(false);
              }}
            >
              {o.label}
            </Chip>
          ))}
        </div>
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

      {result ? <ConnectionResultBanner result={result} /> : null}
    </div>
  );
}
