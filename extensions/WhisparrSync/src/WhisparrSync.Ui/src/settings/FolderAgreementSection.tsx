import type { FolderAgreementView } from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import type { FolderSaveAnswer } from "./folderAgreementLogic";

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

export function FolderAgreementSection(_props: FolderAgreementSectionProps) {
  return null;
}
