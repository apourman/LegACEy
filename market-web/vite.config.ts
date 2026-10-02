import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', 'MARKET_');
  const runtimeEnv = (globalThis as typeof globalThis & { process?: { env: Record<string, string | undefined> } }).process?.env ?? {};
  return {
    plugins: [react()],
    server: {
      host: '0.0.0.0', port: Number(env.MARKET_WEB_PORT ?? runtimeEnv.MARKET_WEB_PORT) || 5173, strictPort: true,
      watch: { usePolling: true },
      proxy: { '/api': env.MARKET_API_PROXY_TARGET ?? runtimeEnv.MARKET_API_PROXY_TARGET ?? 'http://market-api:8080' },
    },
  };
});
