import path from "node:path";
import { fileURLToPath } from "node:url";
import type { PluginOption, UserConfig } from "vite";

export interface ExtensionViteOptions {
  /** `__dirname` of the consuming `vite.config.ts` (anchors the lib entry). */
  packageDir: string;
  /** Lib entry relative to `packageDir`. Default `"src/index.ts"`. */
  entry?: string;
  /** Appended to the host-import-map externals below. */
  extraExternals?: string[];
  /**
   * `react()` from `@vitejs/plugin-react`, supplied by the caller: this package is consumed as raw
   * source through a Vite alias and has no node_modules, so an import here resolves nowhere.
   */
  reactPlugin: PluginOption;
}

// The host import-map provides these; a second React bundled here would break hook identity.
//
// Both spellings of every module the import map serves: the canonical `@cove/runtime/*` name and the
// bare alias where one exists. A specifier missing from the list is bundled rather than resolved
// against the host, and an external is a name match, so the build reports nothing. The set is
// therefore the host's whole import map, not the spellings this repo imports today.
//
// It sits in JSON so `scripts/check-host-externals.mjs` reads the same bytes this config does.
import HOST_EXTERNALS from "./host-externals.json";

/**
 * The Vite library-mode config every extension UI bundle shares: React plugin, the shared-UI-module
 * source alias, the host import-map externals, and the `index.mjs` output the manifest's `jsBundle`
 * points at.
 */
export function createExtensionViteConfig(options: ExtensionViteOptions): UserConfig {
  const { packageDir, entry = "src/index.ts", extraExternals = [], reactPlugin } = options;
  // Anchored on the factory's own location so no consumer restates the relative climb to shared/.
  const factoryDir = path.dirname(fileURLToPath(import.meta.url));
  const sharedSrcIndex = path.resolve(factoryDir, "../src/index.ts");
  const sharedSrcExtensionRequest = path.resolve(factoryDir, "../src/extensionRequest.ts");

  return {
    plugins: [
      reactPlugin,
      {
        name: "extension-production-env",
        // Build only. Applied in every mode it also reaches the test run, where it hands vitest
        // React's production build: that build ships no `act`, so a suite can flush a render only by
        // waiting a fixed span, and a slow enough runner then reads a render that has not committed.
        apply: "build",
        config: () => ({
          // Library bundles run in the browser, where `process` is unavailable.
          define: { "process.env.NODE_ENV": JSON.stringify("production") },
        }),
      },
    ],
    // Raw TS source, not a node_modules install, so Vite runs it through the same transform pipeline
    // as this bundle's own src/.
    resolve: {
      // Vite string-alias matching is prefix-based, so every subpath must be listed before the bare
      // barrel entry or the barrel would match it first.
      alias: {
        "@cove-extensions/ui-shared/extensionRequest": sharedSrcExtensionRequest,
        "@cove-extensions/ui-shared": sharedSrcIndex,
      },
    },
    build: {
      lib: {
        entry: path.resolve(packageDir, entry),
        formats: ["es"],
        fileName: () => "index.mjs",
      },
      rollupOptions: {
        external: [...HOST_EXTERNALS, ...extraExternals],
      },
      // The panel styles via the host's already-emitted Tailwind utilities; it ships no stylesheet.
      cssCodeSplit: false,
    },
  };
}
