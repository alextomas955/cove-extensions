/**
 * One red block for the files Whisparr reported and Cove could not take: a line per Whisparr root,
 * the count since that root last worked, and its newest offending paths, each naming its own cause.
 *
 * Presentational. Every value arrives as a prop and no request is issued here.
 *
 * Nothing to report renders nothing at all, rather than an empty block reporting a healthy zero.
 */
import { StatusText } from "@cove-extensions/ui-shared";

import type { ImportBannerView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { IMPORTS_UNREADABLE } from "../common/ui/copy";
import {
  bannerLines,
  describeCause,
  hasAnythingToSay,
  headingFor,
  pathsShownFor,
} from "./importBannerLogic";

export interface ImportBannerProps {
  read: AsyncRead;
  /** The refusals outstanding, or null before the read answers. */
  view: ImportBannerView | null;
}

export function ImportBanner({ read, view }: ImportBannerProps) {
  return (
    <AsyncRegion
      state={deriveAsyncRegionState(read)}
      available={hasAnythingToSay(view)}
      reading={null}
      empty={null}
      failed={null}
      content={
        <div
          role="alert"
          className="space-y-2 rounded-lg border border-red-700 bg-red-950/60 px-3 py-2"
        >
          <StatusText kind="error">{IMPORTS_UNREADABLE}</StatusText>
          <ul className="list-none space-y-2">
            {bannerLines(view).map((line) => (
              <li key={line.root} className="space-y-1">
                <p className="text-sm text-red-200">{headingFor(line)}</p>
                <ul className="list-none space-y-1">
                  {pathsShownFor(line).map((path) => (
                    <li key={path.path} className="text-xs text-red-300">
                      <span className="break-all font-mono">{path.path}</span>{" "}
                      {describeCause(path.cause)}
                    </li>
                  ))}
                </ul>
              </li>
            ))}
          </ul>
        </div>
      }
    />
  );
}
