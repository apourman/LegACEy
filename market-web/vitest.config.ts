import { defineConfig } from 'vitest/config';

// The BFF's request tests (npm run test:bff). They load the app through Vite themselves (bff-tests/harness.ts), so the React Router plugin isn't
// used here. Playwright's browser checks live in tests/ and are not Vitest's.
export default defineConfig({
  test: {
    include: ['bff-tests/**/*.test.ts', 'tests/**/*.test.ts', 'tests/**/*.test.tsx'],
    environment: 'node',
    testTimeout: 20_000,
    hookTimeout: 60_000,
  },
});
