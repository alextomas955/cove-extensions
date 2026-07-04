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
    // CSS is emitted by the separate `@tailwindcss/cli` step in the build script into
    // dist/index.css, not by Vite. `cssCodeSplit: false` is retained because Vite itself
    // produces no stylesheet here (lib mode with no imported CSS), so this only affirms that
    // Vite's output stays a single `index.mjs`.
    cssCodeSplit: false,
  },
});
