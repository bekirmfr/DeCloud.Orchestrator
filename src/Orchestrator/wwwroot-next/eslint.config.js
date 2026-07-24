import reactHooks from "eslint-plugin-react-hooks";
import tsParser from "@typescript-eslint/parser";

// DELIBERATELY NARROW: the hooks rules only, not a general lint setup.
//
// The motivating bug was a Rules-of-Hooks violation in DeployPage — usePriceEstimate
// placed after an early return, so the hook count differed between the loading and
// loaded renders and React threw #310. It reached production because neither gate
// catches it: tsc sees valid TypeScript (hook calls are just function calls), and
// vitest never renders that component. rules-of-hooks catches it statically, which
// is the entire reason this file exists.
//
// Adding the full recommended sets to a mature codebase would surface hundreds of
// style findings — including the deliberate `as any` casts at the AppKit boundary —
// and a report nobody reads is worse than no report. Broaden later, as its own
// decision, once the noise can be worked through.
export default [
  { ignores: ["dist", "node_modules", "**/*.d.ts", "coverage"] },
  {
    files: ["src/**/*.{ts,tsx}"],
    languageOptions: {
      parser: tsParser,
      parserOptions: {
        ecmaVersion: "latest",
        sourceType: "module",
        ecmaFeatures: { jsx: true },
      },
    },
    plugins: { "react-hooks": reactHooks },
    rules: {
      // ERROR: a violation is a runtime crash, not a style preference.
      "react-hooks/rules-of-hooks": "error",
      // WARN: exhaustive-deps has real false positives (stable refs, intentional
      // one-shot effects), so it informs rather than blocks. Several existing
      // effects will trip it — read them, don't silence them reflexively.
      "react-hooks/exhaustive-deps": "warn",
    },
  },
];
