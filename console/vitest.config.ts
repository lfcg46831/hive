import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    globals: true,
    // Component tests are `.tsx` and opt into jsdom per file, so the logic suite
    // keeps running in the cheaper node environment.
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
    exclude: ['src/**/*.contract.test.ts', 'node_modules/**'],
  },
});
