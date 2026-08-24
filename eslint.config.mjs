import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import prettier from "eslint-config-prettier";
import importX from "eslint-plugin-import-x";
import boundaries from "eslint-plugin-boundaries";

// The single ESLint config for the whole monorepo — every extension's React/TS UI bundle AND every
// first-party .mjs/.cjs helper/build/test script. There is intentionally NO per-extension ESLint
// config: a new extension's src/ and scripts are linted here automatically by path, so the ruleset
// never drifts between extensions. Formatting is Prettier's job (the `prettier` config last disables
// every stylistic rule).

// Dead-code rules shared by every file type; `_`-prefixed names are the opt-out convention for
// deliberately-unused bindings.
const noUnusedVars = [
  "error",
  { argsIgnorePattern: "^_", varsIgnorePattern: "^_", caughtErrorsIgnorePattern: "^_" },
];

// e2e specs pass browser-context callbacks to page.evaluate(...), so those files legitimately name
// browser globals (document/window) alongside the Node globals every script uses.
const scriptGlobals = { ...globals.node, ...globals.browser };
const scriptRules = {
  ...js.configs.recommended.rules,
  "no-unused-vars": noUnusedVars,
  "no-duplicate-imports": "error",
  // Playwright fixtures use the empty-pattern `async ({}, use) => …` signature for a fixture with no
  // dependencies; that is required idiom, not a mistake.
  "no-empty-pattern": "off",
};

// The raw-HTML React prop, banned on both UI surfaces. Hoisted so its three selectors share one wording.
const noRawHtml =
  "Raw-HTML rendering is banned: filenames, diffs and flags must render as escaped text nodes, never as parsed HTML.";

export default tseslint.config(
  {
    ignores: [
      // Generated from the committed wire document; a program input, not lint's subject.
      "**/src/wire/**",
      "**/node_modules/**",
      "**/dist/**",
      "**/bin/**",
      "**/obj/**",
      "**/artifacts/**",
      "website/**",
      // Gitignored planning scratch, not shipped source.
      ".planning/**",
    ],
  },

  // --- Node helper / build / test scripts (.mjs/.cjs) across the whole monorepo ---
  {
    files: ["**/*.mjs"],
    languageOptions: { ecmaVersion: "latest", sourceType: "module", globals: scriptGlobals },
    rules: scriptRules,
  },
  {
    files: ["**/*.cjs"],
    languageOptions: { ecmaVersion: "latest", sourceType: "commonjs", globals: scriptGlobals },
    rules: scriptRules,
  },

  // --- Every extension's React/TS UI bundle (type-aware) ---
  {
    files: ["extensions/*/src/**/*.{ts,tsx}"],
    extends: [tseslint.configs.strictTypeChecked, tseslint.configs.stylisticTypeChecked],
    languageOptions: {
      ecmaVersion: 2024,
      globals: { ...globals.browser },
      parserOptions: {
        projectService: true, // type-aware; finds each extension's tsconfig automatically
        tsconfigRootDir: import.meta.dirname,
      },
    },
    plugins: {
      "react-hooks": reactHooks,
      "react-refresh": reactRefresh,
    },
    rules: {
      ...reactHooks.configs["recommended-latest"].rules,
      "react-refresh/only-export-components": ["warn", { allowConstantExport: true }],
      "@typescript-eslint/no-unused-vars": noUnusedVars,
      // The UIs deliberately build strings from template tokens like `$title`; allow numbers/booleans
      // in template expressions rather than forcing String(...) everywhere.
      "@typescript-eslint/restrict-template-expressions": [
        "error",
        { allowNumber: true, allowBoolean: true },
      ],
    },
  },
  {
    files: ["extensions/*/src/**/*.test.{ts,tsx}"],
    rules: { "@typescript-eslint/no-non-null-assertion": "off" },
  },

  // --- The shared UI package (aliased-raw as @cove-extensions/ui-shared, not a workspace) ---
  // Without this block its TS matched no config and was silently un-linted ("File ignored"). This is
  // NON-type-aware on purpose: the package is deliberately dependency-less (its React/Vite/Node types
  // resolve only inside each consuming extension's bundle, never here), so `projectService`
  // type-aware linting produces only spurious no-unsafe-* noise from unresolved types, not real
  // findings. Syntactic linting (recommended + dead-code + the MF-44 import rules) is the correct
  // scope for code that is type-checked downstream where its types actually resolve.
  {
    files: ["shared/ui-shared/**/*.{ts,tsx}"],
    extends: [tseslint.configs.recommended],
    languageOptions: {
      ecmaVersion: 2024,
      sourceType: "module",
      globals: { ...globals.browser, ...globals.node },
    },
    plugins: {
      "react-hooks": reactHooks,
      "react-refresh": reactRefresh,
    },
    rules: {
      ...reactHooks.configs["recommended-latest"].rules,
      "react-refresh/only-export-components": ["warn", { allowConstantExport: true }],
      "@typescript-eslint/no-unused-vars": noUnusedVars,
      // overlay.ts uses the intentional "latest ref" pattern (writing optsRef.current during render
      // to keep the newest options without forcing a re-render); react-hooks/refs flags it. Whether
      // to rework it is a product-code call, not a lint-config one — advisory here (U-35), since this
      // plan is behavior-neutral and only closes the shared-TS lint-scope gap.
      "react-hooks/refs": "warn",
    },
  },

  // --- MF-44 import-enforcement layer (both UI surfaces: extensions + the shared package) ---
  // R8 named-exports + R13 no-internal-barrels. Uses eslint-plugin-import-x (the maintained fork;
  // eslint-plugin-import is peer-disqualified at ESLint 10).
  {
    files: ["extensions/*/src/**/*.{ts,tsx}", "shared/ui-shared/**/*.{ts,tsx}"],
    plugins: { "import-x": importX },
    rules: {
      // Named exports only, so every import follows one uniform, greppable pattern. The two extension
      // entries and the two vite configs legitimately default-export (defineExtension / Vite's config
      // contract) and are exempted in the override block below.
      "import-x/no-default-export": "error",
      // No internal barrels: import the concrete module, not an index re-export. The pattern matches
      // ONLY index-file names — deliberately NOT ".", "./", ".." (a group containing those degenerates
      // via minimatch into match-everything, 127 false positives; CITATION-RECHECK §2). The alias
      // `@cove-extensions/ui-shared` is unaffected — that specifier does not end in `index`, and its
      // src/index.ts is the one sanctioned barrel (the package's public entry).
      "no-restricted-imports": [
        "error",
        {
          patterns: [
            {
              group: ["**/index", "**/index.js", "**/index.ts", "**/index.mjs"],
              message:
                "No internal barrels: import the concrete module, not an index re-export (Wave-1 slice architecture).",
            },
          ],
        },
      ],
    },
  },

  // The only files that may default-export: each extension's entry (defineExtension) and each Vite
  // config (Vite requires a default export). Everything else is named-export-only above.
  {
    files: ["extensions/*/src/**/index.ts", "extensions/*/src/**/vite.config.ts"],
    rules: { "import-x/no-default-export": "off" },
  },

  // --- The raw-HTML ban (both UI surfaces) ---
  {
    files: ["extensions/*/src/**/*.{ts,tsx}", "shared/ui-shared/**/*.{ts,tsx}"],
    rules: {
      // The three selectors are the three ways the prop can reach React — a JSX attribute, and an
      // object property under either an identifier or a string-literal key. The last two cover the
      // spread form (`<div {...{ dangerouslySetInnerHTML: … }} />`) and createElement props, which a
      // JSX-attribute-only check walks straight past.
      "no-restricted-syntax": [
        "error",
        {
          selector: 'JSXAttribute[name.name="dangerouslySetInnerHTML"]',
          message: noRawHtml,
        },
        {
          selector: 'Property[key.name="dangerouslySetInnerHTML"]',
          message: noRawHtml,
        },
        {
          selector: 'Property[key.value="dangerouslySetInnerHTML"]',
          message: noRawHtml,
        },
      ],
    },
  },

  // --- Pure-logic modules: relative imports only ---
  // These modules are the L0 tier — pure and testable with no environment. Nothing here may reach for
  // react, the SDK, the shared barrel, or a node: builtin; a logic module needing one of those is doing
  // I/O and belongs in an INFRA or FEAT module.
  //
  // The no-internal-barrels group is restated rather than inherited: a later `no-restricted-imports`
  // entry REPLACES the earlier one for a matching file, so omitting it would switch the barrels ban off
  // for exactly the modules this block covers. It still bites after the `^[^.]` regex, which stops only
  // non-relative specifiers — the relative barrel hop (`./foo/index`) is the group's half.
  {
    files: [
      "extensions/*/src/**/*Logic.ts",
      "shared/ui-shared/**/*Logic.ts",
      // Pure modules whose names predate the *Logic.ts suffix.
      "extensions/*/src/**/options.ts",
      "extensions/*/src/**/preview.ts",
      "shared/ui-shared/**/actions.ts",
    ],
    rules: {
      "no-restricted-imports": [
        "error",
        {
          patterns: [
            {
              group: ["**/index", "**/index.js", "**/index.ts", "**/index.mjs"],
              message:
                "No internal barrels: import the concrete module, not an index re-export (Wave-1 slice architecture).",
            },
            {
              regex: "^[^.]",
              message:
                "A pure-logic module stays pure (L0): relative imports only — no react, no SDK, no shared barrel, no node: builtin. Move the I/O to an INFRA module and pass its result in.",
            },
          ],
        },
      ],
    },
  },

  // --- MF-44 architectural boundaries (eslint-plugin-boundaries v7, `boundaries/dependencies`) ---
  // Encodes the Wave-1 slice isolation over both UIs: each feature slice folder is an element, its
  // sibling slices are off-limits (route through common/ or the entry), and common/ is importable by
  // any slice. The src-root index.ts (each extension's defineExtension entry, the one legitimate
  // barrel) is intentionally left unclassified — the rule does not constrain an unknown source, which
  // is exactly the entry's role: it may import any slice. The TS resolver classifies the
  // `@cove-extensions/ui-shared` alias (via each extension's tsconfig paths) so shared imports land as
  // the `shared` element, not an unclassified external. Authored against the v7 `policies`/object-
  // selector syntax (not the deprecated `element-types`/`rules` shape). BLOCKING (error) per U-35:
  // the sibling-slice imports it once flagged are resolved, so a cross-slice import now fails lint.
  {
    files: ["extensions/*/src/**/*.{ts,tsx}", "shared/ui-shared/**/*.{ts,tsx}"],
    plugins: { boundaries },
    settings: {
      "import/resolver": {
        typescript: {
          noWarnOnMultipleProjects: true,
          project: ["extensions/Renamer/src/Renamer.Ui/tsconfig.json"],
        },
      },
      "boundaries/elements": [
        // common/ is one element (incl. its ui/ and lib/); stopMatching keeps the broader slice glob
        // below from re-classifying it as a slice named "common".
        {
          type: "common",
          pattern: "extensions/*/src/*.Ui/src/common",
          capture: ["extension", "ui"],
          stopMatching: true,
        },
        {
          type: "slice",
          pattern: "extensions/*/src/*.Ui/src/*",
          capture: ["extension", "ui", "slice"],
        },
        { type: "shared", pattern: "shared/ui-shared/src" },
      ],
    },
    rules: {
      "boundaries/dependencies": [
        "error",
        {
          // External npm packages (react, @cove/extension-sdk, …) are governed by boundaries/external,
          // not this rule; only import between local elements is constrained here.
          default: "disallow",
          policies: [
            {
              from: { element: { type: "common" } },
              allow: { to: { element: { types: { anyOf: ["common", "shared"] } } } },
            },
            // A feature slice may reach common/ and the shared package, but NOT a sibling slice.
            {
              from: { element: { type: "slice" } },
              allow: { to: { element: { types: { anyOf: ["common", "shared"] } } } },
            },
            {
              from: { element: { type: "shared" } },
              allow: { to: { element: { type: "shared" } } },
            },
          ],
        },
      ],
    },
  },

  // MUST BE LAST: disable all formatting rules so Prettier is the sole formatter.
  prettier,
);
