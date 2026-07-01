// @ts-check
// CI-ONLY ESLint config (plan §4 option A). A bare GitHub runner cannot install the
// `@cove/extension-sdk` `file:` dependency, so the TypeScript program can't be built and the
// type-aware rule sets (`*TypeChecked` / `projectService`) would error out. This config drops all
// type-aware linting and runs the syntactic subset that needs no type information. The FULL,
// type-aware gate (eslint.config.js) is the authoritative LOCAL + pre-commit gate (the dev box has
// the SDK resolved) — consistent with "frontend verified on the dev box, CI ships the committed
// bundle."
import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import prettier from "eslint-config-prettier";

export default tseslint.config(
  { ignores: ["dist/**", "node_modules/**", "scripts/**", "*.config.*"] },

  js.configs.recommended,

  // Non-type-aware TS rules only (no program / no projectService needed).
  ...tseslint.configs.recommended,

  {
    files: ["src/**/*.{ts,tsx}"],
    languageOptions: {
      ecmaVersion: 2024,
      globals: { ...globals.browser },
    },
    plugins: {
      "react-hooks": reactHooks,
      "react-refresh": reactRefresh,
    },
    rules: {
      ...reactHooks.configs["recommended-latest"].rules,
      "react-refresh/only-export-components": ["warn", { allowConstantExport: true }],

      // Mirror the local config's relaxations so CI and local agree on the non-type-aware rules.
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_" },
      ],
    },
  },

  // MUST BE LAST: disable all formatting rules so Prettier is the sole formatter.
  prettier,
);
