// @ts-check
// The BFF's server: the website's pages, its sign-in routes and the /api/* proxy, all through React Router's request handler (src/bff/handler.server.ts).
// Development (default): Vite in middleware mode, with live reload. Production (NODE_ENV=production): the build in build/, after `npm run build`.
// Settings come from the environment only (src/bff/settings.server.ts); the server refuses to start without them.
// Port: --port <n>, or PORT, default 5173. Address: HOST, default 0.0.0.0.
import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequestListener } from '@mjackson/node-fetch-server';

const production = process.env.NODE_ENV === 'production';
const portArgument = process.argv.indexOf('--port');
const port = Number(portArgument >= 0 ? process.argv[portArgument + 1] : process.env.PORT ?? 5173);
const host = process.env.HOST ?? '0.0.0.0';
const root = path.dirname(fileURLToPath(import.meta.url));

const server = createServer();

/** @type {import('vite').ViteDevServer | undefined} */
let vite;
if (!production) {
  const { createServer: createViteServer } = await import('vite');
  vite = await createViteServer({ root, server: { middlewareMode: true, hmr: { server } }, appType: 'custom' });
}

/** @returns {Promise<import('react-router').ServerBuild>} */
async function loadBuild() {
  if (vite) return /** @type {any} */ (await vite.ssrLoadModule('virtual:react-router/server-build'));
  // exists after npm run build
  return await import(pathToFileURL(path.join(root, 'build', 'server', 'index.js')).href);
}

/** The extra exports of src/entry.server.tsx */
function bff(/** @type {import('react-router').ServerBuild} */ build) {
  return /** @type {any} */ (build.entry.module);
}

// fail at startup, saying what's missing, rather than on the first request
try {
  bff(await loadBuild()).bffSettings();
} catch (error) {
  console.error(error instanceof Error ? error.message : error);
  await vite?.close();
  process.exit(1);
}

/** @type {((request: Request, context: unknown) => Promise<Response>) | undefined} */
let productionHandler;
const listener = createRequestListener(async (request, client) => {
  const build = await loadBuild();
  const entry = bff(build);
  const handler = productionHandler ?? entry.createBffHandler(build, production ? 'production' : 'development');
  if (production) productionHandler = handler;
  return handler(request, entry.loadContext(request, client));
});

// production: the built browser files, named by content hash, so cacheable for good
const clientDirectory = path.join(root, 'build', 'client');
/** @param {import('node:http').IncomingMessage} request @param {import('node:http').ServerResponse} response */
async function serveAsset(request, response) {
  const pathname = decodeURIComponent(new URL(request.url ?? '/', 'http://localhost').pathname);
  if (!pathname.startsWith('/assets/') || pathname.includes('..') || (request.method !== 'GET' && request.method !== 'HEAD')) return false;
  const file = path.join(clientDirectory, pathname);
  const info = await stat(file).catch(() => null);
  if (!info?.isFile()) return false;
  const types = { '.js': 'text/javascript', '.css': 'text/css', '.map': 'application/json' };
  response.writeHead(200, {
    'Content-Type': types[/** @type {keyof typeof types} */ (path.extname(file))] ?? 'application/octet-stream',
    'Content-Length': info.size,
    'Cache-Control': 'public, max-age=31536000, immutable',
  });
  if (request.method === 'HEAD') response.end(); else createReadStream(file).pipe(response);
  return true;
}

server.on('request', (request, response) => {
  if (vite) vite.middlewares(request, response, () => listener(request, response));
  else void serveAsset(request, response).then(served => { if (!served) listener(request, response); });
});

server.listen(port, host, () => console.log(`Market BFF (${production ? 'production' : 'development'}) on http://${host}:${port}`));
