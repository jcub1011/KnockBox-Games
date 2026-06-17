// ESLint flat config for the Phaser client (ESLint 10 — flat config only).
//
// Neither @eslint/js nor the `globals` package is installed (the lockfile carries only ESLint's own
// runtime deps), so rather than pull in new dependencies we declare a focused, self-contained
// correctness rule set and the host globals the sources actually use. There are two file shapes:
//   • UMD sources (kb-core/knockbox-plugin/knockbox-local/kb-authority) — classic scripts that
//     feature-detect module/define and read browser/Node globals; linted as `script`.
//   • Vitest suites under __tests__ — ES modules; linted as `module`.
// The hand-written knockbox-phaser.d.ts is ignored: linting TypeScript needs a parser we don't ship.

'use strict';

// Host globals referenced across the sources (ECMAScript built-ins like Promise/Map/globalThis are
// provided automatically for the configured ecmaVersion, so only host/module globals are listed).
const hostGlobals = {
  // Browser
  window: 'readonly',
  document: 'readonly',
  self: 'readonly',
  location: 'readonly',
  history: 'readonly',
  navigator: 'readonly',
  console: 'readonly',
  addEventListener: 'readonly',
  removeEventListener: 'readonly',
  BroadcastChannel: 'readonly',
  WebSocket: 'readonly',
  URL: 'readonly',
  URLSearchParams: 'readonly',
  Image: 'readonly',
  setTimeout: 'readonly',
  clearTimeout: 'readonly',
  setInterval: 'readonly',
  clearInterval: 'readonly',
  queueMicrotask: 'readonly',
  // Node (tests + the UMD CommonJS branch)
  process: 'readonly',
  require: 'readonly',
  // Module systems the UMD wrapper feature-detects
  module: 'writable',
  exports: 'writable',
  define: 'readonly',
  // Provided by the host game at runtime
  Phaser: 'readonly',
};

// Correctness-oriented subset mirroring the spirit of eslint:recommended (which lives in the
// uninstalled @eslint/js). args/caughtErrors are not flagged: event-callback and UMD-factory
// signatures intentionally keep unused parameters.
const rules = {
  'no-undef': 'error',
  'no-unused-vars': ['error', { args: 'none', caughtErrors: 'none' }],
  'no-redeclare': 'error',
  'no-dupe-keys': 'error',
  'no-dupe-args': 'error',
  'no-duplicate-case': 'error',
  'no-dupe-else-if': 'error',
  'no-unreachable': 'error',
  'no-cond-assign': ['error', 'except-parens'],
  'no-constant-condition': ['error', { checkLoops: false }],
  'no-empty': ['error', { allowEmptyCatch: true }],
  'no-ex-assign': 'error',
  'no-fallthrough': 'error',
  'no-func-assign': 'error',
  'no-invalid-regexp': 'error',
  'no-irregular-whitespace': 'error',
  'no-self-assign': 'error',
  'no-self-compare': 'error',
  'no-sparse-arrays': 'error',
  'no-unsafe-negation': 'error',
  'no-unsafe-finally': 'error',
  'no-useless-catch': 'error',
  'no-useless-escape': 'error',
  'use-isnan': 'error',
  'valid-typeof': 'error',
  'no-global-assign': 'error',
  'no-shadow-restricted-names': 'error',
  'no-const-assign': 'error',
  'no-this-before-super': 'error',
};

module.exports = [
  { ignores: ['node_modules/**', 'package-lock.json', '*.d.ts'] },
  {
    files: ['**/*.js'],
    ignores: ['__tests__/**', 'eslint.config.js'],
    languageOptions: { ecmaVersion: 'latest', sourceType: 'script', globals: hostGlobals },
    rules,
  },
  {
    files: ['__tests__/**/*.js'],
    languageOptions: { ecmaVersion: 'latest', sourceType: 'module', globals: hostGlobals },
    rules,
  },
];
