import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "node:path";

// Vite library mode → a single ESM bundle named `index.mjs`.
// The manifest's `jsBundle` field points at this exact filename.
//
// Externals: everything the host injects via its <script type="importmap">
// (cove/ui/scripts/extension-runtime-contract.ts legacy specifiers). Those modules
// resolve at runtime to the host's single React/lucide/react-query copies — bundling
// a second React would cause an invalid-hook-call crash.
//
// CRITICAL: `@cove/extension-sdk` is NOT in the host import-map, so it must be BUNDLED
// (omitted from `external`). Externalizing it would 404 at runtime.
export default defineConfig({
  plugins: [react()],
  // The shared UI module (`shared/cove-extensions-ui/`) is resolved from its raw TS source through
  // this alias — not a node_modules install — so Vite transforms it through the same pipeline as
  // this package's own `src/`, and its `react`/`lucide-react` imports stay externalized by the
  // rollup `external` list below (nothing host-provided is bundled). Kept identical to the sibling
  // extension's config and mirrored by the tsconfig `paths` entry.
  resolve: {
    alias: {
      "@cove-ext/ui-shared": path.resolve(
        __dirname,
        "../../../../shared/cove-extensions-ui/src/index.ts",
      ),
    },
  },
  // Bundled deps that branch on `process.env.NODE_ENV` (e.g. @tanstack/react-virtual's dev-only
  // warnings) would otherwise ship a live `process` reference — undefined in the browser, so the
  // panel throws `ReferenceError: process is not defined` on mount. Vite's library mode does NOT
  // auto-define `process`, so replace it at build time. The extension bundle is always the
  // production artifact, so "production" is the correct constant (drops the dev-only branches).
  define: {
    "process.env.NODE_ENV": JSON.stringify("production"),
  },
  build: {
    lib: {
      entry: path.resolve(__dirname, "src/index.ts"),
      formats: ["es"],
      fileName: () => "index.mjs",
    },
    rollupOptions: {
      external: [
        "react",
        "react-dom",
        "react-dom/client",
        "react/jsx-runtime",
        "react/jsx-dev-runtime",
        "@tanstack/react-query",
        "lucide-react",
      ],
    },
    // No CSS bundle: the panel styles exclusively via the host's already-emitted
    // Tailwind semantic utilities, so the extension ships no stylesheet of its own.
    cssCodeSplit: false,
  },
});
