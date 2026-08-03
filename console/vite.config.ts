import react from '@vitejs/plugin-react';
import type { Plugin } from 'vite';
import { defineConfig } from 'vite';

/**
 * Injects the runtime configuration script into the host page. Injecting it
 * rather than writing it into `index.html` keeps it out of the bundle graph:
 * `config.js` is deployment data — including the organization credential — and
 * must stay replaceable without a rebuild.
 */
function runtimeConfigScript(): Plugin {
  return {
    name: 'hive-console-runtime-config',
    transformIndexHtml() {
      return [{ tag: 'script', attrs: { src: '/config.js' }, injectTo: 'head' }];
    },
  };
}

// The console is a static bundle: it talks to the public HIVE API over the
// network and has no server of its own. `HIVE_CONSOLE_API_BASE_URL` only wires
// the dev-server proxy, so local development uses the same relative `/api/v1`
// paths as a deployed bundle.
const devApiTarget = process.env['HIVE_CONSOLE_API_BASE_URL'] ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react(), runtimeConfigScript()],
  server: {
    proxy: {
      '/api/v1': {
        target: devApiTarget,
        changeOrigin: true,
        ws: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
});
