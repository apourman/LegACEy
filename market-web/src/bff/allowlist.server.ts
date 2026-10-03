import type { paths } from '../api-schema';

type HttpMethod = 'get' | 'put' | 'post' | 'delete' | 'patch';
/** The methods the API document gives a path */
type MethodsOf<P extends keyof paths> = { [M in HttpMethod]: [NonNullable<paths[P][M]>] extends [never] ? never : M }[HttpMethod];

/** One route the browser may reach through /api/*: a path and method the generated API document has, so a typo or a removed route is a type error */
type AllowedRoute = { [P in keyof paths]: { method: Uppercase<MethodsOf<P>>; path: P; withoutToken?: true } }[keyof paths];

/**
 * Every API route the website's browser code uses, and nothing else. Sign-in and sign-out are the BFF's own routes (/auth/sign-in, /auth/sign-out);
 * the plugin's routes, token management and the OpenAPI document are not here, so the BFF never forwards them.
 * A page that moves to loaders and actions takes its routes off this list.
 */
export const allowlist = [
  { method: 'GET', path: '/api/me' },
  { method: 'GET', path: '/api/facets' },
  { method: 'GET', path: '/api/listings' },
  { method: 'GET', path: '/api/listings/suggest' },
  { method: 'GET', path: '/api/listings/{id}' },
  // public files: forwarded without the token, with their cache headers
  { method: 'GET', path: '/api/icons/glow.css', withoutToken: true },
  { method: 'GET', path: '/api/icons/{file}', withoutToken: true },
  { method: 'GET', path: '/api/vault' },
  { method: 'GET', path: '/api/vault/{itemGuid}' },
  { method: 'POST', path: '/api/listings' },
  { method: 'POST', path: '/api/listings/{id}/delist' },
  { method: 'POST', path: '/api/listings/{id}/purchase' },
  { method: 'POST', path: '/api/vault/withdraw' },
  { method: 'POST', path: '/api/vault/deposit' },
  { method: 'POST', path: '/api/mmd/withdraw' },
  { method: 'POST', path: '/api/inventory/snapshot' },
  { method: 'GET', path: '/api/tickets' },
  { method: 'GET', path: '/api/tickets/{id}' },
  { method: 'GET', path: '/api/history' },
] as const satisfies readonly AllowedRoute[];

export type AllowlistEntry = (typeof allowlist)[number];

// A path parameter is one segment of plain characters: no slash, no percent-encoding (so no encoded slash or dot-segment), never "." or ".."
const segment = '(?!\\.{1,2}(?:/|$))[A-Za-z0-9._~-]+';
const patterns = allowlist.map(route => ({
  route,
  pattern: new RegExp('^' + route.path.split(/\{[^}]+\}/).map(part => part.replace(/[.*+?^$()|[\]\\]/g, '\\$&')).join(segment) + '$'),
}));

/** The allowlisted route a request's method and path name exactly, or undefined */
export function allowedRoute(method: string, pathname: string): AllowlistEntry | undefined {
  return patterns.find(({ route, pattern }) => route.method === method && pattern.test(pathname))?.route;
}
