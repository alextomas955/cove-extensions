/**
 * The host symbols this surface renders through.
 *
 * Nothing in this repo can check the hand-transcribed declarations behind
 * `@cove/runtime/components`: a wrong prop shape type-checks, and a wrong export name throws at
 * bundle load and takes every surface of the extension with it. The end-to-end run in a container
 * is what proves them, by loading a built bundle.
 */

export { ConfirmDialog } from "@cove/runtime/components";
