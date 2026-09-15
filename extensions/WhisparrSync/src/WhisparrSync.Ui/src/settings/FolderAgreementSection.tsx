/**
 * One prompt per Cove library folder the connected instance could not be shown to hold: what
 * happened to it, what was asked, and a field for the path Whisparr really holds it at.
 *
 * Presentational. Every value arrives as a prop and no request is issued here.
 *
 * Nothing to ask about renders nothing at all. The prompt exists only where a path an operator can
 * state is the missing fact, so a settled page carries no folder controls of any kind.
 */
import { Field, INPUT_CLASS, SectionCard, StatusText } from "@cove-extensions/ui-shared";

import type { FolderAgreementRootLine, FolderAgreementView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import { deriveAsyncRegionState, type AsyncRead } from "../common/ui/asyncRegionLogic";
import {
  FOLDER_AGREEMENT_DESCRIPTION,
  FOLDER_AGREEMENT_PATH,
  FOLDER_AGREEMENT_PATH_HELPER,
  FOLDER_AGREEMENT_SAVE,
  FOLDER_AGREEMENT_SAVE_IS_RUNNING,
  FOLDER_AGREEMENT_TITLE,
  FOLDER_AGREEMENT_UNREADABLE,
} from "../common/ui/copy";
import {
  agreementLines,
  asksForAPath,
  hasAnythingToAsk,
  mappingSentenceFor,
  saveAnswerSentence,
  sentenceFor,
  type FolderSaveAnswer,
} from "./folderAgreementLogic";

export interface FolderAgreementSectionProps {
  read: AsyncRead;
  /** The folders nothing resolved for, or null before the read answers. */
  view: FolderAgreementView | null;
  /** The path typed under each folder, by folder. */
  drafts: Readonly<Record<string, string>>;
  /** The folder whose save is in flight, or null when none is. */
  saving: string | null;
  /** What the last save under each folder came to, by folder. */
  answers: Readonly<Record<string, FolderSaveAnswer>>;
  onPathChange: (root: string, next: string) => void;
  onSave: (root: string) => void;
}

export function FolderAgreementSection({
  read,
  view,
  drafts,
  saving,
  answers,
  onPathChange,
  onSave,
}: FolderAgreementSectionProps) {
  const lines = agreementLines(view);

  return (
    <AsyncRegion
      // A read that failed has to say so: a page drawing nothing reads as one with no folders
      // outstanding, which is the opposite of what an unanswered read established.
      available={hasAnythingToAsk(view) || read.failed}
      state={deriveAsyncRegionState(read)}
      reading={null}
      empty={null}
      failed={
        <SectionCard title={FOLDER_AGREEMENT_TITLE}>
          <StatusText kind="error">{FOLDER_AGREEMENT_UNREADABLE}</StatusText>
        </SectionCard>
      }
      content={
        <SectionCard title={FOLDER_AGREEMENT_TITLE} description={FOLDER_AGREEMENT_DESCRIPTION}>
          <ul className="list-none space-y-5">
            {lines.map((line) => (
              <Prompt
                key={line.root}
                line={line}
                draft={drafts[line.root] ?? ""}
                saving={saving === line.root}
                blocked={saving !== null && saving !== line.root}
                answer={answers[line.root] ?? null}
                onPathChange={onPathChange}
                onSave={onSave}
              />
            ))}
          </ul>
        </SectionCard>
      }
    />
  );
}

function Prompt({
  line,
  draft,
  saving,
  blocked,
  answer,
  onPathChange,
  onSave,
}: {
  line: FolderAgreementRootLine;
  draft: string;
  saving: boolean;
  /** Another folder's save is in flight, so this one would be refused if it were pressed. */
  blocked: boolean;
  answer: FolderSaveAnswer | null;
  onPathChange: (root: string, next: string) => void;
  onSave: (root: string) => void;
}) {
  const mapping = mappingSentenceFor(line);
  const reason = saving || blocked ? FOLDER_AGREEMENT_SAVE_IS_RUNNING : null;

  return (
    <li data-root={line.root} className="space-y-2">
      <p className="text-sm text-secondary">{sentenceFor(line)}</p>
      {mapping === null ? null : <p className="text-xs text-muted">{mapping}</p>}

      {asksForAPath(line.refusal) ? (
        <div className="space-y-2">
          <Field label={FOLDER_AGREEMENT_PATH} helper={FOLDER_AGREEMENT_PATH_HELPER}>
            <input
              type="text"
              value={draft}
              disabled={reason !== null}
              onChange={(e) => {
                onPathChange(line.root, e.target.value);
              }}
              className={`${INPUT_CLASS} font-mono`}
            />
          </Field>
          <OptionallyDisabled
            name={FOLDER_AGREEMENT_SAVE}
            variant="ghost"
            reason={reason}
            onClick={() => {
              onSave(line.root);
            }}
          />
        </div>
      ) : null}

      {answer === null ? null : <StatusText kind="muted">{saveAnswerSentence(answer)}</StatusText>}
    </li>
  );
}
