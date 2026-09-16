/**
 * The folder agreement section's data layer: the folders whose Whisparr path is not Cove's own to
 * work out, the path typed under each, and the save that puts one to the instance.
 *
 * No store beside it. Nothing here is shared with another surface, cached across a visit or
 * coordinated with anything, so the request lives in the hook the way the neighbouring sections keep
 * theirs.
 *
 * A save that settled a folder re-reads the lines rather than changing one locally: the server
 * decides which folders are listed, and a local change would be a second answer to that question.
 * A withdrawal is what takes a folder off the page, and it is the re-read that does it.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type {
  FolderAgreementView,
  FolderMappingSaveRequest,
  FolderMappingSaveResult,
} from "../wire/api";
import { api } from "../common/lib/extension";
import { INITIAL_ASYNC_READ, type AsyncRead } from "../common/ui/asyncRegionLogic";
import { saveSettled, type FolderSaveAnswer } from "./folderAgreementLogic";

const FOLDER_MAPPINGS_PATH = api("addressing/folder-mappings");

export interface UseFolderAgreement {
  /** Which of the four states the prompts are in. */
  readonly read: AsyncRead;
  /** The folders listed, or null before the read answers. */
  readonly view: FolderAgreementView | null;
  /** The path typed under each folder, by folder. */
  readonly drafts: Readonly<Record<string, string>>;
  /** The folder whose save is in flight, or null when none is. */
  readonly saving: string | null;
  /** What the last save under each folder came to, by folder. */
  readonly answers: Readonly<Record<string, FolderSaveAnswer>>;
  readonly editPath: (root: string, next: string) => void;
  readonly save: (root: string) => void;
}

export function useFolderAgreement(): UseFolderAgreement {
  const [view, setView] = useState<FolderAgreementView | null>(null);
  const [read, setRead] = useState<AsyncRead>(INITIAL_ASYNC_READ);
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState<string | null>(null);
  const [answers, setAnswers] = useState<Record<string, FolderSaveAnswer>>({});

  const load = useCallback(() => {
    requestJson<FolderAgreementView>(FOLDER_MAPPINGS_PATH)
      .then((answer) => {
        setView(answer);
        setRead({ reading: false, failed: false, hasContent: answer.roots.length > 0 });
      })
      .catch(() => {
        setRead((held) => ({ reading: false, failed: true, hasContent: held.hasContent }));
      });
  }, []);

  const primed = useRef(false);
  useEffect(() => {
    if (primed.current) return;
    primed.current = true;
    load();
  }, [load]);

  const editPath = useCallback((root: string, next: string) => {
    setDrafts((held) => ({ ...held, [root]: next }));
  }, []);

  const save = useCallback(
    (root: string) => {
      if (saving !== null) return;
      setSaving(root);

      requestJson<FolderMappingSaveResult>(FOLDER_MAPPINGS_PATH, {
        method: "PUT",
        body: JSON.stringify({
          coveRoot: root,
          instancePath: drafts[root] ?? "",
        } satisfies FolderMappingSaveRequest),
      })
        .then((result) => {
          const answer: FolderSaveAnswer = { kind: "answered", result };
          setSaving(null);
          setAnswers((held) => ({ ...held, [root]: answer }));
          if (saveSettled(answer)) load();
        })
        .catch(() => {
          setSaving(null);
          setAnswers((held) => ({ ...held, [root]: { kind: "didNotReach" } }));
        });
    },
    [drafts, load, saving],
  );

  return { read, view, drafts, saving, answers, editPath, save };
}
