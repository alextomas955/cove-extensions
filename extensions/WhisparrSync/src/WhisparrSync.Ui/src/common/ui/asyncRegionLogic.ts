/**
 * The four-way split every read surface renders through: reading, loaded with content, loaded and
 * empty, failed.
 *
 * A surface still reading never returns its empty state, and a refresh that fails over content on
 * screen keeps the content.
 */

export type AsyncRegionStatus = "reading" | "content" | "empty" | "failed";

export interface AsyncRegionState {
  readonly status: AsyncRegionStatus;
  /** A read failed while content was already on screen. */
  readonly outage: boolean;
}

export interface AsyncRead {
  readonly reading: boolean;
  /** The most recent completed read failed. */
  readonly failed: boolean;
  /** Content from an earlier successful read is on screen. */
  readonly hasContent: boolean;
}

/**
 * A region before its first read completes. Reading, not empty: an initial value equal to the
 * empty-success value makes a momentary blank read as a factual zero.
 */
export const INITIAL_ASYNC_READ: AsyncRead = { reading: true, failed: false, hasContent: false };

/**
 * Which of the four `read` is in.
 *
 * A read in flight over content keeps the content, and a failed read over content keeps it and
 * raises the outage flag. Blanking on a failed refresh would replace a correct answer with none.
 */
export function deriveAsyncRegionState(read: AsyncRead): AsyncRegionState {
  if (read.hasContent) {
    return { status: "content", outage: !read.reading && read.failed };
  }
  if (read.reading) {
    return { status: "reading", outage: false };
  }
  if (read.failed) {
    return { status: "failed", outage: false };
  }
  return { status: "empty", outage: false };
}
