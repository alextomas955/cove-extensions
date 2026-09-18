import path from "node:path";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";
import { createExtensionViteConfig } from "../../../../shared/ui-shared/vite/createExtensionViteConfig";

// The shared UI package is consumed as raw source through a Vite alias rather than installed, so it
// has no node_modules and cannot host a runner of its own. Its suite runs from here as a second
// vitest project rooted at that package — one install, one runner, both surfaces.
const repoRoot = path.resolve(__dirname, "../../../..");
const sharedUiRoot = path.resolve(repoRoot, "shared/ui-shared");

export default defineConfig({
  ...createExtensionViteConfig({ packageDir: __dirname, reactPlugin: react() }),
  test: {
    coverage: {
      provider: "v8",
      // lcovonly writes the report alone; the lcov reporter also emits an HTML tree, which a scanner
      // pointed at this directory then indexes as source.
      reporter: ["text-summary", "lcovonly"],
      // The shared package is a second project rooted outside this one. v8 records a file outside
      // the project root only with allowExternal, and matches it only against an absolute glob;
      // without both its files carry no record and a scanner reads that as nought per cent.
      allowExternal: true,
      include: ["src/**/*.{ts,tsx}", `${sharedUiRoot.replaceAll(path.sep, "/")}/src/**/*.{ts,tsx}`],
      exclude: ["**/src/wire/**", "**/*.test.ts", "**/*.test.tsx"],
    },
    projects: [
      {
        // Without `extends: true` an inline project inherits neither the react plugin nor the
        // `@cove-extensions/ui-shared` alias, and every test whose module graph reaches the shared
        // barrel fails to resolve.
        extends: true,
        test: {
          name: "renamer-ui",
          root: __dirname,
          include: ["src/**/*.test.ts"],
          environment: "node",
        },
      },
      {
        extends: true,
        test: {
          name: "ui-shared",
          root: sharedUiRoot,
          include: ["src/**/*.test.ts"],
          environment: "node",
        },
      },
    ],
  },
});
