import path from "node:path";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";
import { createExtensionViteConfig } from "../../../../shared/ui-shared/vite/createExtensionViteConfig";

const sharedUiRoot = path.resolve(__dirname, "../../../../shared/ui-shared");

const base = createExtensionViteConfig({ packageDir: __dirname, reactPlugin: react() });

// Cove's import map serves these, and a bundle externalizes them, so nothing resolves them in a test
// run: a component reaching one fails to transform rather than to render. Declared per test project
// rather than in `resolve.alias`, which the build reads too - an alias there would defeat the
// externals and bundle a stand-in into the shipped file.
const hostRuntimeStubs = {
  "@cove/runtime/api": path.resolve(sharedUiRoot, "src/coveRuntimeApiStub.ts"),
  "@cove/runtime/components": path.resolve(sharedUiRoot, "src/coveRuntimeComponentsStub.tsx"),
};

export default defineConfig({
  ...base,
  resolve: {
    ...base.resolve,
    // A bundle externalizes these, so nothing resolves them there. A test run has to, and the shared
    // package's source - including the JSX runtime the transform injects into it - would otherwise
    // resolve them against a node_modules that package does not have.
    dedupe: ["react", "react-dom", "lucide-react"],
  },
  test: {
    coverage: {
      provider: "v8",
      // lcovonly writes the report alone; the lcov reporter also emits an HTML tree, which a scanner
      // pointed at this directory then indexes as source.
      reporter: ["text-summary", "lcovonly"],
      include: ["src/**/*.{ts,tsx}"],
      exclude: ["**/src/wire/**", "**/*.test.ts", "**/*.test.tsx"],
    },
    projects: [
      {
        // Without `extends: true` an inline project inherits neither the react plugin nor the
        // `@cove-extensions/ui-shared` alias, and every test whose module graph reaches the shared
        // barrel fails to resolve.
        extends: true,
        test: {
          name: "scanandidentify-ui",
          root: __dirname,
          include: ["src/**/*.test.{ts,tsx}"],
          environment: "node",
          alias: hostRuntimeStubs,
          // The vendored SDK ships extensionless relative imports, which a bundler resolves and
          // Node's ESM loader refuses. Transforming it here is what lets a test render a component
          // whose graph reaches the host request helper.
          server: { deps: { inline: ["@cove/extension-sdk"] } },
        },
      },
    ],
  },
});
