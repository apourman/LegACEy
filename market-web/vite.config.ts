import { reactRouter } from '@react-router/dev/vite';
import { defineConfig } from 'vite';

// The BFF's build. server.js runs it: in development as Vite middleware (live reload), in production from build/.
// There is no Vite proxy: the browser reaches the Market API only through the BFF's /api/* route (src/routes/api.ts).
export default defineConfig({
  plugins: [reactRouter()],
  // bundled for the browser at startup: found only on the first page load, it would make Vite reload that page mid-hydration
  optimizeDeps: { include: ['openapi-fetch'] },
  server: {
    // no CORS anywhere: Vite's dev server would otherwise answer other localhost origins' preflights and reads
    cors: false,
    // the source is bind-mounted into the dev container from WSL, where file events don't arrive
    watch: { usePolling: true },
  },
});
