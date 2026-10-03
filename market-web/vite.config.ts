import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// The Market API answers only requests that carry its service key (MARKET_SERVICE_KEY from docker.env, passed in by
// docker/docker-compose.local.yml). The dev proxy adds it to every /api request, and drops any X-Market-Client-Ip a browser sends:
// the API would trust that header alongside the key.
export default defineConfig(({ command, mode }) => {
  const serviceKey = loadEnv(mode, '.', 'MARKET_').MARKET_SERVICE_KEY ?? '';

  // 32: the API's minimum, ServiceGate.MinimumKeyLength in Source/ACE.MarketApi/ServiceGate.cs; keep them equal
  if (command === 'serve' && serviceKey.length < 32)
    console.warn('MARKET_SERVICE_KEY is missing or shorter than 32 characters: the Market API will refuse every /api request (401). Set it in docker.env.');

  return {
    plugins: [react()],
    server: {
      host: '0.0.0.0', port: 5173, strictPort: true,
      watch: { usePolling: true },
      proxy: {
        '/api': {
          target: 'http://market-api:8080',
          headers: { 'X-Market-Service-Key': serviceKey },
          // runs before each request is forwarded; returning nothing forwards it
          bypass: (request) => {
            delete request.headers['x-market-client-ip'];
          },
        },
      },
    },
  };
});
