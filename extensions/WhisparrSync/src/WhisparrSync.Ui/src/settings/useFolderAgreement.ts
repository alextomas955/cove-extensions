/**
 * The folder agreement section's data layer: the folders whose Whisparr path Cove cannot work out,
 * the path typed under each, and the save that puts one to the instance.
 *
 * A save that settled a folder re-reads the list rather than changing it locally, because the
 * server decides which folders are listed. That folder's draft is cleared with the re-read, so
 * withdrawing the path again is one press. The save answer stays, as the only confirmation the
 * store worked.
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
  readonly read: AsyncRead;
  readonly view: FolderAgreementView | null;
  /** The path typed under each folder, keyed by folder. */
  readonly drafts: Readonly<Record<string, string>>;
  /** The folder whose save is in flight, or null when none is. */
  readonly saving: string | null;
  /** What the last save under each folder came to, keyed by folder. */
  readonly answers: Readonly<Record<string, FolderSaveAnswer>>;
  readonly editPath: (root: string, next: string) => void;
  readonly save: (root: string) => void;
  /** Takes the path in force off `root`, ignoring what is typed under it. */
  readonly withdraw: (root: string) => void;
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

  const put = useCallback(
    (root: string, instancePath: string) => {
      if (saving !== null) return;
      setSaving(root);

      requestJson<FolderMappingSaveResult>(FOLDER_MAPPINGS_PATH, {
        method: "PUT",
        body: JSON.stringify({
          coveRoot: root,
          instancePath,
        } satisfies FolderMappingSaveRequest),
      })
        .then((result) => {
          const answer: FolderSaveAnswer = { kind: "answered", result };
          setSaving(null);
          setAnswers((held) => ({ ...held, [root]: answer }));
          if (saveSettled(answer)) {
            setDrafts((held) => ({ ...held, [root]: "" }));
            load();
          }
        })
        .catch(() => {
          setSaving(null);
          setAnswers((held) => ({ ...held, [root]: { kind: "didNotReach" } }));
        });
    },
    [load, saving],
  );

  const save = useCallback(
    (root: string) => {
      put(root, drafts[root] ?? "");
    },
    [drafts, put],
  );

  const withdraw = useCallback(
    (root: string) => {
      put(root, "");
    },
    [put],
  );

  return { read, view, drafts, saving, answers, editPath, save, withdraw };
}
