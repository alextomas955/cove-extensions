import path from "node:path";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";
import { createExtensionViteConfig } from "../../../../shared/ui-shared/vite/createExtensionViteConfig";

// The shared UI package is consumed as raw source through a Vite alias rather than installed, so it
// has no node_modules and cannot host a runner of its own. Its suite runs from here as a second
// vitest project rooted at that package — one install, one runner, both surfaces.
const sharedUiRoot = path.resolve(__dirname, "../../../../shared/ui-shared");

export default defineConfig({
  ...createExtensionViteConfig({ packageDir: __dirname, reactPlugin: react() }),
  test: {
    projects: [
      {
        // `extends: true` only becomes the default in vitest 5. On 4.x an inline project inherits
        // neither the react plugin nor the `@cove-extensions/ui-shared` alias without it, and every
        // test whose module graph reaches the shared barrel fails to resolve.
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
