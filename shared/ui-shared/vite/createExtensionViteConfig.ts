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
   * The React Vite plugin (`react()` from `@vitejs/plugin-react`), supplied by the caller rather
   * than imported here: this shared package is consumed as raw source via a Vite alias and has no
   * own node_modules, so importing the plugin here left it unresolved at each UI's config load.
   */
  reactPlugin: PluginOption;
}

// The host import-map provides these; a second React bundled here would break hook identity.
// `@cove/extension-sdk` is intentionally absent — it is not in the host import-map, so it must ship
// bundled.
//
// The mixed spelling is deliberate, and each host module is named here exactly once. The seven bare
// names are host modules the import map also serves under a bare alias; neither `@cove/runtime/api`
// nor `@cove/runtime/components` carries such an alias, so the canonical name is the only spelling
// that reaches them. Do NOT collapse this to a `@cove/runtime/` prefix match: it matches none of the
// seven bare names, which would silently bundle a second React with no build failure.
const HOST_EXTERNALS = [
  "react",
  "react-dom",
  "react-dom/client",
  "react/jsx-runtime",
  "react/jsx-dev-runtime",
  "@tanstack/react-query",
  "lucide-react",
  "@cove/runtime/api",
  "@cove/runtime/components",
];

/**
 * The Vite library-mode config every extension UI bundle shares: React plugin, the shared-UI-module
 * source alias, the host import-map externals, and the `index.mjs` output the manifest's `jsBundle`
 * points at. The two per-extension configs were byte-identical, so they now call this factory.
 */
export function createExtensionViteConfig(options: ExtensionViteOptions): UserConfig {
  const { packageDir, entry = "src/index.ts", extraExternals = [], reactPlugin } = options;
  // Anchored on the factory's own location so no consumer restates the relative climb to shared/.
  const factoryDir = path.dirname(fileURLToPath(import.meta.url));
  const sharedSrcIndex = path.resolve(factoryDir, "../src/index.ts");
  const sharedSrcPostAction = path.resolve(factoryDir, "../src/postAction.ts");
  const sharedSrcExtensionRequest = path.resolve(factoryDir, "../src/extensionRequest.ts");
  const sharedSrcExtensionStore = path.resolve(factoryDir, "../src/extensionStore.ts");
  const sharedSrcOverlay = path.resolve(factoryDir, "../src/overlay.ts");
  // The SDK is vendored per-UI (this package has no node_modules), so its bare specifier will not
  // resolve from the aliased shared source unless it is pinned to the consuming UI's own copy.
  const sdkDir = path.resolve(packageDir, "node_modules/@cove/extension-sdk");

  return {
    plugins: [reactPlugin],
    // Raw TS source, not a node_modules install: Vite runs it through the same transform pipeline as
    // this bundle's own src/; its react/lucide imports stay externalized by the rollup list below.
    resolve: {
      // Vite string-alias matching is prefix-based, so every subpath must be listed before the bare
      // barrel entry or the barrel would match it first.
      alias: {
        "@cove-extensions/ui-shared/postAction": sharedSrcPostAction,
        "@cove-extensions/ui-shared/extensionRequest": sharedSrcExtensionRequest,
        "@cove-extensions/ui-shared/extensionStore": sharedSrcExtensionStore,
        "@cove-extensions/ui-shared/overlay": sharedSrcOverlay,
        "@cove-extensions/ui-shared": sharedSrcIndex,
        "@cove/extension-sdk": sdkDir,
      },
    },
    // Vite library mode does not auto-define `process`; without this a dep branching on
    // process.env.NODE_ENV leaves a live `process` reference that is undefined in the browser. The
    // bundle is always the production artifact.
    define: {
      "process.env.NODE_ENV": JSON.stringify("production"),
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
