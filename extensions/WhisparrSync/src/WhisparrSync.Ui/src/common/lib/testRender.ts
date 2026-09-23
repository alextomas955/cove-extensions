// The helper itself lives in the shared package, where the bar's own suite can reach it. The
// barrel deliberately does not carry it: that entry is the shipped bundle's.
export { press, render } from "../../../../../../../shared/ui-shared/src/testRender";
