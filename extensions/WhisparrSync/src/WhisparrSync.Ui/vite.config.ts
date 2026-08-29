import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";
import { createExtensionViteConfig } from "../../../../shared/ui-shared/vite/createExtensionViteConfig";

export default defineConfig({
  ...createExtensionViteConfig({ packageDir: __dirname, reactPlugin: react() }),
  test: {
    name: "whisparr-sync-ui",
    include: ["src/**/*.test.ts"],
    environment: "node",
  },
});
