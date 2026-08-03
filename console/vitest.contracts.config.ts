import { defineConfig } from 'vitest/config';

// Contract parity runs separately from the unit suite: it requires the OpenAPI
// document exported from the running API host (see console/openapi/README.md),
// which a frontend-only checkout does not have.
export default defineConfig({
  test: {
    globals: true,
    include: ['src/**/*.contract.test.ts'],
  },
});
