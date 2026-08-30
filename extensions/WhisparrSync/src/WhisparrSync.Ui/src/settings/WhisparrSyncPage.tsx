import { useState } from "react";
import {
  Button,
  Field,
  INPUT_CLASS,
  SectionCard,
  Spinner,
  StatusText,
  TextInput,
} from "@cove-extensions/ui-shared";

import { isAddressEdit, sentenceForKind, valuesOf } from "./connectLogic";
import { useConnection, type ConnectionState } from "./useConnection";

/**
 * The component the host mounts inside the "Whisparr Sync" settings tab.
 *
 * The tab uses the host's page layout, so the host draws the tab header from the manifest and no
 * card chrome around this component: the card below is the extension's own. No outer page heading
 * and no page gutter, or the tab name would be drawn twice.
 *
 * The host passes `{ onNavigate }`; this surface does not navigate and ignores it. Styling is host
 * Tailwind token classes only, because the host's Tailwind JIT never scans this bundle.
 *
 * Nothing here is stored. The address and key are held for the length of a test and travel with it,
 * which is what makes the answer describe the address that was in the field.
 */
export function WhisparrSyncPage() {
  const [address, setAddress] = useState("");
  const [apiKey, setApiKey] = useState("");
  const { state, test, clear } = useConnection();

  const onAddressChange = (next: string) => {
    if (state.status !== "idle" && isAddressEdit(address, next)) {
      clear();
    }
    setAddress(next);
  };

  const testing = state.status === "testing";
  const canTest = address.trim() !== "" && apiKey !== "" && !testing;

  return (
    <SectionCard title="Connection" description="The Whisparr instance Cove keeps in step with.">
      <div className="space-y-4">
        <Field
          label="Whisparr address"
          helper="The address Cove itself reaches Whisparr on, including the scheme and port."
        >
          <TextInput
            value={address}
            onChange={onAddressChange}
            placeholder="http://whisparr:6969"
          />
        </Field>

        <Field label="API key" helper="Taken for this test only; nothing is stored yet.">
          <input
            type="password"
            value={apiKey}
            onChange={(e) => {
              setApiKey(e.target.value);
            }}
            className={INPUT_CLASS}
            autoComplete="off"
          />
        </Field>

        <div className="flex items-center gap-3">
          <Button
            onClick={() => {
              test(address, apiKey);
            }}
            disabled={!canTest}
          >
            {testing ? <Spinner /> : null}
            Test connection
          </Button>
          <ConnectionResult state={state} />
        </div>
      </div>
    </SectionCard>
  );
}

/** The one result line. Reads nothing until there is something to read. */
function ConnectionResult({ state }: { state: ConnectionState }) {
  if (state.status === "idle") {
    return null;
  }

  if (state.status === "testing") {
    return <StatusText kind="muted">Testing {state.address}</StatusText>;
  }

  if (state.status === "failed") {
    return <StatusText kind="error">Cove could not run the test: {state.message}</StatusText>;
  }

  const { result } = state;
  if (result.kind === "connected") {
    // The instance's own version string, character for character. A rendering that reformatted it
    // would report a version no instance runs.
    return (
      <StatusText kind="success">
        Connected to Whisparr {result.version} ({result.generation})
      </StatusText>
    );
  }

  return <StatusText kind="error">{sentenceForKind(result.kind, valuesOf(result))}</StatusText>;
}
