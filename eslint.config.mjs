import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import prettier from "eslint-config-prettier";

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

export default tseslint.config(
  {
    ignores: [
      "**/node_modules/**",
      "**/dist/**",
      "**/bin/**",
      "**/obj/**",
      "**/artifacts/**",
      "website/**",
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
    extends: [
      tseslint.configs.strictTypeChecked,
      tseslint.configs.stylisticTypeChecked,
    ],
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

  // MUST BE LAST: disable all formatting rules so Prettier is the sole formatter.
  prettier,
);
