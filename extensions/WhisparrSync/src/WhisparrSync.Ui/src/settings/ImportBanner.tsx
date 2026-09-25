/**
 * One block for the files Whisparr reported and Cove could not take.
 *
 * Nothing to report renders nothing. The root list and the passed-over line each appear on their
 * own.
 */
import { SectionCard, StatusText } from "@cove-extensions/ui-shared";

import type { ImportBannerView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { IMPORT_REPORT_UNREADABLE, IMPORTS_UNREADABLE } from "../common/ui/copy";
import {
  bannerLines,
  describeCause,
  hasAnythingToSay,
  headingFor,
  passedOverLine,
  pathsShownFor,
} from "./importBannerLogic";

export interface ImportBannerProps {
  read: AsyncRead;
  view: ImportBannerView | null;
  /** The instant the recorded ages are measured against, in epoch milliseconds. */
  now: number;
}

export function ImportBanner({ read, view, now }: Readonly<ImportBannerProps>) {
  const lines = bannerLines(view);
  const passedOver = passedOverLine(view, now);

  return (
    <AsyncRegion
      state={deriveAsyncRegionState(read)}
      // A failed read must still draw something. Drawing nothing reads as an import that went
      // through.
      available={hasAnythingToSay(view) || read.failed}
      reading={null}
      empty={null}
      failed={
        <SectionCard>
          <div role="alert">
            <StatusText kind="error">{IMPORT_REPORT_UNREADABLE}</StatusText>
          </div>
        </SectionCard>
      }
      content={
        <SectionCard>
          <div role="alert" className="space-y-2">
            {lines.length === 0 ? null : (
              <>
                <StatusText kind="error">{IMPORTS_UNREADABLE}</StatusText>
                <ul className="list-none space-y-2">
                  {lines.map((line) => (
                    <li key={line.root} className="space-y-1">
                      <p className="text-sm text-foreground">{headingFor(line)}</p>
                      <ul className="list-none space-y-1">
                        {pathsShownFor(line).map((path) => (
                          <li key={path.path} className="text-xs text-secondary">
                            {/* Two blocks rather than one line: a path can contain spaces, so a
                                space does not read as a boundary between it and the cause. */}
                            <p className="break-all font-mono">{path.path}</p>
                            <p className="pl-4">{describeCause(path.cause)}</p>
                          </li>
                        ))}
                      </ul>
                    </li>
                  ))}
                </ul>
              </>
            )}

            {passedOver === null ? null : <p className="text-sm text-red-400">{passedOver}</p>}
          </div>
        </SectionCard>
      }
    />
  );
}
