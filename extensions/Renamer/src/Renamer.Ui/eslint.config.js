// @ts-check
import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import prettier from "eslint-config-prettier";

export default tseslint.config(
  // Never lint build output / config emitters / the custom guard script.
  { ignores: ["dist/**", "node_modules/**", "scripts/**", "*.config.*"] },

  js.configs.recommended,

  // Type-aware TS rules (strict + stylistic). strictTypeChecked is the high-signal set.
  ...tseslint.configs.strictTypeChecked,
  ...tseslint.configs.stylisticTypeChecked,

  {
    files: ["src/**/*.{ts,tsx}"],
    languageOptions: {
      ecmaVersion: 2024,
      globals: { ...globals.browser },
      parserOptions: {
        projectService: true, // type-aware, no explicit `project` path needed
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

      // --- Strict-but-not-pedantic deltas (keeps "fix everything" achievable) ---
      // Allow intentionally-unused args/vars prefixed with _.
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_" },
      ],
      // The codebase deliberately uses template tokens like `$title`; restrict-template-expressions
      // is noisy for string building. Relax to allow numbers/booleans in templates.
      "@typescript-eslint/restrict-template-expressions": [
        "error",
        { allowNumber: true, allowBoolean: true },
      ],
    },
  },

  // Vitest/config-style files (if any test files land here later) — loosen type-aware noise.
  {
    files: ["**/*.test.{ts,tsx}"],
    rules: { "@typescript-eslint/no-non-null-assertion": "off" },
  },

  // MUST BE LAST: disable all formatting rules so Prettier is the sole formatter.
  prettier,
);
