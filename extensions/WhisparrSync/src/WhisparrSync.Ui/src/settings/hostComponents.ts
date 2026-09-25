/**
 * The host symbols this surface renders through.
 *
 * Nothing in this repo checks the hand-transcribed declarations behind `@cove/runtime/components`.
 * A wrong prop shape type-checks, and a wrong export name throws at bundle load and takes every
 * extension surface with it. The containerized end-to-end run loads a built bundle to prove them.
 */

export { ConfirmDialog } from "@cove/runtime/components";
