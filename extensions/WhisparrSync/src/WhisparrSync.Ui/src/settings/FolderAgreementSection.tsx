/**
 * One row per Cove library folder, pairing the folder with the path Whisparr reaches it at.
 *
 * A settled row keeps its path field closed, because the pair on its header line is the whole of
 * what it has to say. The field is blank even when a path is in force, because saving it blank is
 * what withdraws that path. Where no typed path can be stored, the field is replaced by a withdraw
 * control.
 */
import { INPUT_CLASS, SectionCard, StatusPill, StatusText } from "@cove-extensions/ui-shared";
import { useState } from "react";

import type { FolderAgreementRootLine, FolderAgreementView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import { StateGlyph } from "../common/ui/StateGlyph";
import { deriveAsyncRegionState, type AsyncRead } from "../common/ui/asyncRegionLogic";
import {
  folderAgreementTriedSummary,
  FOLDER_AGREEMENT_CHANGE,
  FOLDER_AGREEMENT_DESCRIPTION,
  FOLDER_AGREEMENT_NO_PATH_YET,
  FOLDER_AGREEMENT_PATH,
  FOLDER_AGREEMENT_PATH_HELPER,
  FOLDER_AGREEMENT_SAVE,
  FOLDER_AGREEMENT_SAVE_IS_RUNNING,
  FOLDER_AGREEMENT_TITLE,
  FOLDER_AGREEMENT_UNREADABLE,
  FOLDER_AGREEMENT_WITHDRAW,
} from "../common/ui/copy";
import {
  agreementLines,
  asksForAPath,
  hasAnythingToShow,
  reasonFor,
  saveAnswerSentence,
  stateLabel,
  stateOf,
  withdrawsOnly,
  type FolderAgreementState,
  type FolderSaveAnswer,
} from "./folderAgreementLogic";

// A tint and a mark, never a tint alone: every other status pill the product draws carries one.
const PILL_FACE: Record<
  FolderAgreementState,
  { variant: "green" | "amber" | "gray"; glyph: string }
> = {
  settled: { variant: "green", glyph: "check" },
  needsAPath: { variant: "amber", glyph: "circleAlert" },
  nothingToSettle: { variant: "gray", glyph: "circleDashed" },
};

export interface FolderAgreementSectionProps {
  read: AsyncRead;
  view: FolderAgreementView | null;
  /** The path typed under each folder, keyed by folder. */
  drafts: Readonly<Record<string, string>>;
  /** The folder whose save is in flight, or null when none is. */
  saving: string | null;
  /** What the last save under each folder came to, keyed by folder. */
  answers: Readonly<Record<string, FolderSaveAnswer>>;
  onPathChange: (root: string, next: string) => void;
  onSave: (root: string) => void;
  onWithdraw: (root: string) => void;
}

export function FolderAgreementSection({
  read,
  view,
  drafts,
  saving,
  answers,
  onPathChange,
  onSave,
  onWithdraw,
}: FolderAgreementSectionProps) {
  const lines = agreementLines(view);

  return (
    <AsyncRegion
      // A failed read must still draw something. Drawing nothing reads as no folders outstanding.
      available={hasAnythingToShow(view) || read.failed}
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
          <ul className="list-none space-y-2">
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
                onWithdraw={onWithdraw}
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
  onWithdraw,
}: {
  line: FolderAgreementRootLine;
  draft: string;
  saving: boolean;
  /** Another folder's save is in flight, so this one would be refused. */
  blocked: boolean;
  answer: FolderSaveAnswer | null;
  onPathChange: (root: string, next: string) => void;
  onSave: (root: string) => void;
  onWithdraw: (root: string) => void;
}) {
  const [opened, setOpened] = useState(false);
  const state = stateOf(line);
  const reason = reasonFor(line);
  const blockedReason = saving || blocked ? FOLDER_AGREEMENT_SAVE_IS_RUNNING : null;
  // A settled folder asks for nothing, so its field stays closed until the reader opens it.
  const fieldShown = asksForAPath(line) && (state !== "settled" || opened);

  return (
    <li
      data-root={line.root}
      className="space-y-2 rounded-xl border border-border bg-card px-3 py-2"
    >
      <div className="flex flex-wrap items-center gap-2">
        <StatusPill
          variant={PILL_FACE[state].variant}
          shape="tag"
          icon={<StateGlyph iconKey={PILL_FACE[state].glyph} />}
        >
          {stateLabel(state)}
        </StatusPill>
        <span className="font-mono text-sm text-primary">{line.root}</span>
        <span className="text-muted">→</span>
        {line.mapping === null ? (
          <span className="text-sm text-muted">{FOLDER_AGREEMENT_NO_PATH_YET}</span>
        ) : (
          <span className="font-mono text-sm text-primary">{line.mapping}</span>
        )}
        {state === "settled" && !opened ? (
          <button
            type="button"
            className="ml-auto rounded px-2 py-0.5 text-xs text-secondary hover:text-primary"
            onClick={() => {
              setOpened(true);
            }}
          >
            {FOLDER_AGREEMENT_CHANGE}
          </button>
        ) : null}
      </div>

      {reason === null ? null : <p className="text-xs text-secondary">{reason}</p>}

      {line.pathsTried.length === 0 ? null : (
        <details className="text-xs text-muted">
          <summary className="cursor-pointer">
            {folderAgreementTriedSummary(line.pathsTried.length)}
          </summary>
          <ul className="mt-1 list-none space-y-0.5 font-mono">
            {line.pathsTried.map((path) => (
              <li key={path}>{path}</li>
            ))}
          </ul>
        </details>
      )}

      {/* What can be done about the folder is a different subject from what is outstanding on it,
          so a hairline closes the lines above rather than spacing alone. */}
      {fieldShown ? (
        <div className="flex flex-wrap items-center gap-2 border-t border-border pt-2">
          <input
            type="text"
            aria-label={`${FOLDER_AGREEMENT_PATH}: ${line.root}`}
            placeholder={FOLDER_AGREEMENT_PATH_HELPER}
            value={draft}
            disabled={blockedReason !== null}
            onChange={(e) => {
              onPathChange(line.root, e.target.value);
            }}
            className={`${INPUT_CLASS} flex-1 font-mono`}
          />
          <OptionallyDisabled
            name={FOLDER_AGREEMENT_SAVE}
            variant="ghost"
            reason={blockedReason}
            onClick={() => {
              onSave(line.root);
            }}
          />
        </div>
      ) : null}

      {withdrawsOnly(line) ? (
        <div className="border-t border-border pt-2">
          <OptionallyDisabled
            name={FOLDER_AGREEMENT_WITHDRAW}
            variant="ghost"
            reason={blockedReason}
            onClick={() => {
              onWithdraw(line.root);
            }}
          />
        </div>
      ) : null}

      {answer === null ? null : <StatusText kind="muted">{saveAnswerSentence(answer)}</StatusText>}
    </li>
  );
}
