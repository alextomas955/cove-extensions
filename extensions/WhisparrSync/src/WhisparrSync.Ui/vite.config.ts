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
    // The shared UI source has no node_modules of its own. In a build its React imports are host
    // externals, so nothing ever has to resolve them; a test runs that same source for real and does.
    // Pinned to this package's own copy, and only under test, so the build's externalization - which
    // is what keeps a second React out of the bundle - is untouched.
    alias: {
      "react-dom": path.resolve(__dirname, "node_modules/react-dom"),
      react: path.resolve(__dirname, "node_modules/react"),
    },
  },
});
