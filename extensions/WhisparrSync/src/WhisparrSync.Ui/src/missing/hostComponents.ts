/**
 * The host symbols this surface renders through, in one module.
 *
 * Nothing in this repo can check the hand-transcribed declarations behind `@cove/runtime/components`:
 * a wrong prop shape type-checks, and a wrong export name throws at bundle load and takes every
 * surface of the extension with it. Importing them all here makes one module the whole surface's
 * exposure, and the containerized end-to-end run proves it by loading a built bundle.
 */

export {
  ConfirmDialog,
  DetailListPagination,
  PerformerTile,
  TagBadge,
  useKeySequence,
  useMultiSelect,
  toggleOptionsFromEvent,
  withOrderedToggle,
} from "@cove/runtime/components";
