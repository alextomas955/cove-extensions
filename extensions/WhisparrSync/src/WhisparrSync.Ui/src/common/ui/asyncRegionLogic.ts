/**
 * The four-way split every read surface renders through: reading, loaded with content, loaded and
 * genuinely empty, failed.
 *
 * Pure, so the two rules that are easy to get wrong are settled without a DOM: a surface still
 * reading never returns its empty state, and a refresh that fails over content on screen keeps the
 * content.
 */

/** The four states. */
export type AsyncRegionStatus = "reading" | "content" | "empty" | "failed";

/** What a region is in, and whether a failed refresh is sitting behind the content it kept. */
export interface AsyncRegionState {
  readonly status: AsyncRegionStatus;
  /** A read failed while content was already on screen. */
  readonly outage: boolean;
}

/** What the state is derived from. */
export interface AsyncRead {
  readonly reading: boolean;
  /** The most recent completed read failed. */
  readonly failed: boolean;
  /** Content from an earlier successful read is on screen. */
  readonly hasContent: boolean;
}

/**
 * A region before its first read completes.
 *
 * Reading rather than empty: a surface that has never completed a read is not a surface reporting an
 * empty answer, and an initial value equal to the empty-success value is how a momentary blank comes
 * to read as a factual zero.
 */
export const INITIAL_ASYNC_READ: AsyncRead = { reading: true, failed: false, hasContent: false };

/**
 * Which of the four <code>read</code> is in.
 *
 * A read in flight over content keeps the content rather than blanking it, and a failed read over
 * content keeps it too and raises the outage flag - blanking on a failed refresh throws away a
 * correct answer to show an incorrect one.
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
