import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequestHandler, type AppLoadContext, type ServerBuild } from 'react-router';
import { createServer as createViteServer, type ViteDevServer } from 'vite';
import type { BffSettings } from '../src/bff/settings.server';
import type { FakeApi } from './fake-api';

export { startFakeApi, type FakeApi, type RecordedRequest } from './fake-api';

// The BFF over HTTP: React Router's server request handler, from the app's real server build, driven with Fetch requests and a load context,
// against a fake Market API (fake-api.ts: an in-process HTTP server that records every request and answers from a script).

export const site = 'http://market.test';
export const serviceKey = 'test-service-key-0123456789abcdef0123456789abcdef';
export const cookieSecret = 'test-cookie-secret-0123456789abcdef0123456789abcd';

// ---- the BFF

export interface Bff {
  /** The server's handler as server.js runs it (createBffHandler: the cross-site wall, then React Router) */
  handle(request: Request, context: AppLoadContext): Promise<Response>;
  /** React Router's handler alone, without the outer cross-site check: proves the routes check for themselves */
  handleRoutesOnly(request: Request, context: AppLoadContext): Promise<Response>;
  close(): Promise<void>;
}

/** The app's server build, compiled by Vite with the project's own config (the React Router plugin included) */
export async function loadBff(): Promise<Bff> {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const vite: ViteDevServer = await createViteServer({
    root, configFile: path.join(root, 'vite.config.ts'), logLevel: 'error', appType: 'custom',
    server: { middlewareMode: true, hmr: false, watch: null },
  });
  const build = await vite.ssrLoadModule('virtual:react-router/server-build') as ServerBuild;
  const entry = build.entry.module as unknown as { createBffHandler(build: ServerBuild, mode?: string): Bff['handle'] };
  const handle = entry.createBffHandler(build, 'production');
  const routesOnly = createRequestHandler(build, 'production');
  return { handle, handleRoutesOnly: (request, context) => routesOnly(request, context), close: () => vite.close() };
}

export function settings(api: FakeApi, overrides: Partial<BffSettings> = {}): BffSettings {
  return { apiUrl: api.url, serviceKey, cookieSecret, trustedProxy: null, siteOrigin: null, loopbackPeers: [], ...overrides };
}

/** A request to the BFF as a browser on this site would send it */
export function request(pathAndQuery: string, init: Omit<RequestInit, 'headers'> & { headers?: Record<string, string> } = {}): Request {
  const method = (init.method ?? 'GET').toUpperCase();
  const sameSite: Record<string, string> = method === 'GET' || method === 'HEAD' ? {} : { Origin: site };
  return new Request(site + pathAndQuery, { ...init, headers: { ...sameSite, ...init.headers } });
}

/** The cookie a Set-Cookie sets, as a browser sends it back ("name=value") */
export function cookieFrom(response: Response, name = 'market_session'): string {
  const cookie = response.headers.getSetCookie().find(c => c.startsWith(name + '='));
  if (!cookie) throw new Error(`no ${name} cookie in ${JSON.stringify(response.headers.getSetCookie())}`);
  return cookie.split(';')[0];
}

/** The account the fake API knows */
export const me = {
  accountId: 1, accountName: 'Alpha', balance: 500, frozen: false, paused: false,
  characters: [{ id: 11, name: 'Alpha Main' }, { id: 12, name: 'Alpha Second' }],
  vaultCount: 2, vaultCap: 1000, listingCount: 1, listingCap: 200,
};
export const listing = {
  id: 1, itemGuid: 10, wcid: 100, name: 'Bone Slicer', itemType: 'MeleeWeapon', material: 'Steel',
  workmanship: 7, level: 100, arcaneLore: 200, summary: '34-52 (Slashing)',
  quantity: 3, price: 120, seller: 'Bravo Main', listedTime: '2026-10-01T12:00:00Z',
  wield: 'Level 100', icon: { layers: [], glow: null },
};
export const detail = { ...listing, lines: ['Value: 500', 'Damage: 34 - 52'], spells: [{ name: 'Blood Drinker', cantrip: false }] };
export const facets = {
  itemTypes: [{ value: 'MeleeWeapon', label: 'Melee Weapon', count: 1 }],
  sorts: [{ value: 'newest', label: 'Newest', defaultDir: 'desc' }, { value: 'price', label: 'Price', defaultDir: 'asc' }],
};
