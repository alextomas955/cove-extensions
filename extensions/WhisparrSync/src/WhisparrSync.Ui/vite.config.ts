import path from "node:path";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";
import { createExtensionViteConfig } from "../../../../shared/ui-shared/vite/createExtensionViteConfig";

export default defineConfig({
  ...createExtensionViteConfig({ packageDir: __dirname, reactPlugin: react() }),
  test: {
    name: "whisparr-sync-ui",
    include: ["src/**/*.test.ts"],
    environment: "node",
    coverage: {
      provider: "v8",
      // lcovonly writes the report alone; the lcov reporter also emits an HTML tree, which a scanner
      // pointed at this directory then indexes as source.
      reporter: ["text-summary", "lcovonly"],
      // Only this package. The shared UI package is measured by Renamer's suite, which runs it as a
      // second project; a second set of records for those files reaches the scanner as duplicates.
      include: ["src/**/*.{ts,tsx}"],
      exclude: ["**/src/wire/**", "**/*.test.ts", "**/*.test.tsx"],
    },
    // The shared UI source has no node_modules of its own. In a build its React and icon imports
    // are host externals, so nothing ever has to resolve them; a test runs that same source for
    // real and does. Pinned to this package's own copy, and only under test, so the build's
    // externalization - which is what keeps a second React out of the bundle - is untouched.
    alias: {
      "react-dom": path.resolve(__dirname, "node_modules/react-dom"),
      react: path.resolve(__dirname, "node_modules/react"),
      "lucide-react": path.resolve(__dirname, "node_modules/lucide-react"),
    },
  },
});
