import { readFileSync } from "node:fs";
import path from "node:path";
import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import prettier from "eslint-config-prettier";
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

// The raw-HTML React prop, banned on both UI surfaces. Hoisted so the three selectors below share
// one wording.
const noRawHtml =
  "Raw-HTML rendering is banned: filenames, diffs and flags must render as escaped text nodes, never as parsed HTML.";

// Every catalog UI bundle's tsconfig, for the boundaries block's TypeScript resolver below. Read
// from `catalog.json` rather than written down here: naming one extension's tsconfig inside config
// whose whole point is being extension-generic is what got the boundary rule deleted once already,
// and a glob would fix only the drift while leaving the registry unread. A catalog entry without a
// UI declares no `uiPath` and contributes nothing.
const uiTsconfigProjects = JSON.parse(
  readFileSync(path.join(import.meta.dirname, "extensions/catalog.json"), "utf8"),
)
  .extensions.filter((entry) => entry.uiPath)
  .map((entry) => `${entry.uiPath}/tsconfig.json`);

const scriptRules = {
  ...js.configs.recommended.rules,
  "no-unused-vars": noUnusedVars,
  "no-duplicate-imports": "error",
  // Playwright fixtures use the empty-pattern `async ({}, use) => …` signature for a fixture with no
  // dependencies; that is required idiom, not a mistake.
  "no-empty-pattern": "off",
};

export default tseslint.config(
  {
    ignores: [
      "**/node_modules/**",
      "**/dist/**",
      // Generated from the committed wire document; still a program input, just not lint's subject.
      "**/src/wire/**",
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
    },
    rules: {
      ...reactHooks.configs["recommended-latest"].rules,
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
    },
    rules: {
      ...reactHooks.configs["recommended-latest"].rules,
      "@typescript-eslint/no-unused-vars": noUnusedVars,
      // overlay.ts uses the intentional "latest ref" pattern (writing optsRef.current during render
      // to keep the newest options without forcing a re-render); react-hooks/refs flags it. Whether
      // to rework it is a product-code call, not a lint-config one — advisory here, since widening
      // the lint scope to shared TS was meant to be behavior-neutral.
      "react-hooks/refs": "warn",
    },
  },

  // --- Cross-surface rules (both UI surfaces: extensions + the shared package) ---
  // The raw-HTML ban — the one rule both UI surfaces share.
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

  // --- `*Logic.ts` purity: relative imports only ---
  // These modules are the L0 tier — pure, mock-free, deterministic, and unit-testable with no
  // environment. Nothing here may reach for react, a host runtime module, the shared barrel, or even
  // node: builtins; a logic module that needs one of those is doing I/O and belongs in an INFRA or
  // FEAT module instead. Until this rule existed the constraint was enforced only as a side effect of
  // the old offline logic gate, which compiled each module in an isolated temp dir where a runtime
  // import simply failed to resolve. The gate is gone (its suite runs under vitest, which resolves
  // everything), so the constraint is now stated directly rather than emerging from a sandbox.
  //
  // This block is the no-internal-barrels ban's only home, so the ban reaches `*Logic.ts` and
  // nothing else: weakening the group below drops it outright rather than falling back on some
  // other statement of it. The group matches ONLY index-file names — deliberately NOT ".", "./",
  // ".." (a group containing those degenerates via minimatch into match-everything, 127 false
  // positives; CITATION-RECHECK §2). It is the half that still bites after the `^[^.]` regex below:
  // that regex already stops every non-relative specifier, so what the group adds is the RELATIVE
  // barrel hop (`./foo/index`) that regex would otherwise let past.
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
                "A *Logic.ts module stays pure (L0): relative imports only — no react, no host runtime, no shared barrel, no node: builtin. Move the I/O to an INFRA module and pass its result in.",
            },
          ],
        },
      ],
    },
  },

  // The shared barrel carries a triple-slash reference to the ambient host-runtime declarations, and
  // the rule's suggested `import` is not a substitute: that file declares modules and emits nothing,
  // so importing it would add a runtime import of a module that does not exist. TypeScript honors a
  // reference directive only above the first statement, which also leaves no room for an inline
  // disable comment — hence the override here rather than at the call site.
  {
    files: ["shared/ui-shared/src/index.ts"],
    rules: { "@typescript-eslint/triple-slash-reference": "off" },
  },

  // --- Architectural boundaries (eslint-plugin-boundaries, `boundaries/dependencies`) ---
  // The taxonomy's one dependency rule, stated as a lint rather than left to review: a feature slice
  // may depend downward onto `wire`, sideways onto `common/` and the shared package, and never
  // across onto a sibling slice. Routing between two features goes through `common/` or the entry.
  //
  // This block was deleted once and is restored deliberately. The deletion's reasoning was
  // sound about the defect and wrong about the remedy: the resolver named ONE extension's tsconfig
  // by path, inside config that claims to be extension-generic, so a second extension would have
  // silently gone unclassified. That is fixed at its source above — `uiTsconfigProjects` reads the
  // catalog — rather than by removing the rule. Everything else here was already generic.
  //
  // The src-root index.ts (each extension's defineExtension entry, the one sanctioned barrel) is
  // intentionally left unclassified: the rule does not constrain an unknown source, which is exactly
  // the entry's role — it may import any slice.
  {
    files: ["extensions/*/src/**/*.{ts,tsx}", "shared/ui-shared/**/*.{ts,tsx}"],
    plugins: { boundaries },
    settings: {
      // Classifies the `@cove-extensions/ui-shared` alias through each UI's tsconfig paths, so a
      // shared import lands as the `shared` element instead of an unclassified external.
      "import/resolver": {
        typescript: { noWarnOnMultipleProjects: true, project: uiTsconfigProjects },
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
        // The generated wire module is a data shape, not a feature: derived from the extension's own
        // OpenAPI document, type-only, importing nothing. Every layer may depend downward onto it, so
        // classifying it as a sibling slice would forbid the one import it exists for.
        {
          type: "wire",
          pattern: "extensions/*/src/*.Ui/src/wire",
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
          // External npm packages (react, @cove/extension-sdk, …) are governed by
          // boundaries/external, not this rule; only imports between local elements are constrained.
          default: "disallow",
          policies: [
            {
              from: { element: { type: "common" } },
              allow: { to: { element: { types: { anyOf: ["common", "shared", "wire"] } } } },
            },
            // A feature slice may reach common/, the shared package and wire — but NOT a sibling.
            {
              from: { element: { type: "slice" } },
              allow: { to: { element: { types: { anyOf: ["common", "shared", "wire"] } } } },
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
