/**
 * The import callback: the address to hand Whisparr, the two ways of handing it over, and the
 * registration status.
 *
 * The address shown carries the secret, because an address pasted into Whisparr by hand has
 * nowhere else to put one. Register sends the whole edited address, and the server honours only
 * its scheme, host, port and path prefix.
 */
import { Field, SectionCard, Spinner, StatusText, TextInput } from "@cove-extensions/ui-shared";

import type { CallbackView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import {
  READ_IS_STALE,
  WEBHOOK_ADDRESS,
  WEBHOOK_ADDRESS_COPIED,
  WEBHOOK_ADDRESS_HELPER,
  WEBHOOK_CLIPBOARD_NOT_REACHED,
  WEBHOOK_COPY,
  WEBHOOK_DESCRIPTION,
  WEBHOOK_NOTHING_TO_COPY,
  WEBHOOK_READING_THE_STATUS,
  WEBHOOK_REGISTER,
  WEBHOOK_REGISTERING,
  WEBHOOK_STATUS_NOT_READ,
  WEBHOOK_TITLE,
  REGISTRATION_WOULD_LOCK_COVE_DOWN,
  LESS_PRIVATE_FORM_NOTE,
  webhookRefusedSentence,
  webhookRegistrationFailedSentence,
} from "../common/ui/copy";
import type { CopyResult } from "./useRegistration";
import {
  describeRegistration,
  missingSettingSentence,
  registerRefusal,
  registrationRead,
  shouldShowLessPrivateFormNote,
} from "./registrationLogic";

export interface ImportWebhookSectionProps {
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
}: Readonly<ImportWebhookSectionProps>) {
  const registerReason = registerRefusal({ sharedReason, registering, address });

  return (
    <SectionCard title={WEBHOOK_TITLE} description={WEBHOOK_DESCRIPTION}>
      <div className="space-y-4">
        <Field label={WEBHOOK_ADDRESS} labelStyle="mono" helper={WEBHOOK_ADDRESS_HELPER}>
          {(id) => <TextInput id={id} value={address} onChange={onAddressChange} mono />}
        </Field>

        {view !== null && !view.registrationIsSafe ? (
          <div role="note">
            <StatusText kind="warning">{REGISTRATION_WOULD_LOCK_COVE_DOWN}</StatusText>
          </div>
        ) : null}

        {/* Handing the address to Whisparr is a different subject from setting it, so a hairline
            closes the address above rather than spacing alone. */}
        <div className="space-y-2 border-t border-border pt-4">
          <div className="flex flex-wrap items-center gap-3" aria-busy={registering}>
            <OptionallyDisabled
              name={WEBHOOK_COPY}
              variant="ghost"
              reason={address.trim() === "" ? WEBHOOK_NOTHING_TO_COPY : null}
              onClick={onCopy}
            />
            <OptionallyDisabled
              name={registering ? WEBHOOK_REGISTERING : WEBHOOK_REGISTER}
              reason={registerReason}
              onClick={onRegister}
            />
            {registering ? <Spinner /> : null}
            <CopyOutcome result={copyResult} />

            {/* The status sits at the row's right edge while it fits on the line, and wraps under
                the controls when it does not. */}
            <div className="ml-auto">
              <AsyncRegion
                state={deriveAsyncRegionState(registrationRead(view, readFailed))}
                reading={<StatusText kind="muted">{WEBHOOK_READING_THE_STATUS}</StatusText>}
                outageNotice={<StatusText kind="error">{READ_IS_STALE}</StatusText>}
                content={<Status view={view} />}
                empty={<Status view={view} />}
                failed={<StatusText kind="error">{WEBHOOK_STATUS_NOT_READ}</StatusText>}
              />
            </div>
          </div>

          {registerError === null ? null : (
            <StatusText kind="error">{webhookRegistrationFailedSentence(registerError)}</StatusText>
          )}
        </div>
      </div>
    </SectionCard>
  );
}

function Status({ view }: Readonly<{ view: CallbackView | null }>) {
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
          <StatusText kind="error">{webhookRefusedSentence(view.refusal)}</StatusText>
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

function CopyOutcome({ result }: Readonly<{ result: CopyResult }>) {
  if (result.status === "copied") {
    return <StatusText kind="success">{WEBHOOK_ADDRESS_COPIED}</StatusText>;
  }
  if (result.status === "failed") {
    return <StatusText kind="error">{WEBHOOK_CLIPBOARD_NOT_REACHED}</StatusText>;
  }
  return null;
}
