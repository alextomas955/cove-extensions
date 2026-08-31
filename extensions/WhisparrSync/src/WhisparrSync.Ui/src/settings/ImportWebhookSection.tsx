/**
 * The import callback: the address to hand Whisparr, the two ways of handing it over, and a status
 * that distinguishes never-checked from absent and registered-with-nothing-arriving from working.
 *
 * Presentational. Every value arrives as a prop and no request is issued here.
 *
 * The address the field shows is the form that carries the secret, because an address pasted into
 * Whisparr by hand has nowhere else to put one. Register sends the whole edited address and the
 * server honours only its scheme, host, port and path prefix - the route and the secret are always
 * this product's own.
 */
import { Field, SectionCard, Spinner, StatusText, TextInput } from "@cove-extensions/ui-shared";

import type { CallbackView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import type { CopyResult } from "./useRegistration";
import {
  describeRegistration,
  LESS_PRIVATE_FORM_NOTE,
  missingSettingSentence,
  registrationRead,
  shouldShowLessPrivateFormNote,
} from "./registrationLogic";

export interface ImportWebhookSectionProps {
  /** The callback as it stands, or null before the status read answers. */
  view: CallbackView | null;
  readFailed: boolean;
  address: string;
  registering: boolean;
  registerError: string | null;
  copyResult: CopyResult;
  /** The one reason several controls on this page share, stated once by the page's own notice. */
  sharedReason: string | null;
  onAddressChange: (next: string) => void;
  onCopy: () => void;
  onRegister: () => void;
}

export function ImportWebhookSection({
  view,
  readFailed,
  address,
  registering,
  registerError,
  copyResult,
  sharedReason,
  onAddressChange,
  onCopy,
  onRegister,
}: ImportWebhookSectionProps) {
  const registerReason =
    sharedReason ??
    (registering
      ? "This registration is still running."
      : address.trim() === ""
        ? "There is no callback address to register."
        : null);

  return (
    <SectionCard
      title="Import webhook"
      description="The address Whisparr calls when it finishes an import."
    >
      <div className="space-y-4">
        <Field
          label="Callback address"
          helper="Correct the scheme, host, port or path prefix if Whisparr reaches Cove somewhere other than you do. The rest is Cove's own."
        >
          <TextInput value={address} onChange={onAddressChange} mono />
        </Field>

        <div className="flex items-center gap-3" aria-busy={registering}>
          <OptionallyDisabled
            name="Copy URL"
            variant="ghost"
            reason={address.trim() === "" ? "There is no callback address to copy." : null}
            onClick={onCopy}
          />
          <OptionallyDisabled
            name={registering ? "Registering…" : "Register in Whisparr"}
            reason={registerReason}
            onClick={onRegister}
          />
          {registering ? <Spinner /> : null}
          <CopyOutcome result={copyResult} />
        </div>

        <AsyncRegion
          state={deriveAsyncRegionState(registrationRead(view, readFailed))}
          reading={<StatusText kind="muted">Reading the callback status…</StatusText>}
          content={<Status view={view} />}
          empty={<Status view={view} />}
          failed={<StatusText kind="error">Cove could not read the callback status.</StatusText>}
        />

        {registerError === null ? null : (
          <StatusText kind="error">
            Cove could not register the callback: {registerError}
          </StatusText>
        )}
      </div>
    </SectionCard>
  );
}

/** The status line, the refusal beneath it, and the standing note. */
function Status({ view }: { view: CallbackView | null }) {
  if (view === null) {
    return null;
  }

  const described = describeRegistration(view);
  const missing = missingSettingSentence(view.missingSetting);

  return (
    <div className="space-y-1">
      <div>
        <StatusText kind={described.tone}>{described.sentence}</StatusText>
      </div>
      {missing === null ? null : (
        <div>
          <StatusText kind="warning">{missing}</StatusText>
        </div>
      )}
      {view.refusal === null ? null : (
        <div>
          {/* The instance's own words, so a refusal reports what it said rather than a guess at why. */}
          <StatusText kind="error">Whisparr refused it: {view.refusal}</StatusText>
        </div>
      )}
      {shouldShowLessPrivateFormNote(view) ? (
        <div role="note">
          <StatusText kind="warning">{LESS_PRIVATE_FORM_NOTE}</StatusText>
        </div>
      ) : null}
    </div>
  );
}

/** Copy's own confirmation, in proportion to the action: a line beside the control, never a toast. */
function CopyOutcome({ result }: { result: CopyResult }) {
  if (result.status === "copied") {
    return <StatusText kind="success">Copied.</StatusText>;
  }
  if (result.status === "failed") {
    return (
      <StatusText kind="error">
        Cove could not reach the clipboard - select the address above and copy it.
      </StatusText>
    );
  }
  return null;
}
