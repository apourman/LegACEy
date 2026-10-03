import type { Route } from './+types/api';
import { allowedRoute } from '../bff/allowlist.server';
import { apiHeaders, noClientIp } from '../bff/api.server';
import { clientIp } from '../bff/client-ip.server';
import { clearedSessionCookieHeader, readSessionToken } from '../bff/cookies.server';
import { crossSiteRefusal } from '../bff/csrf.server';

// /api/*: the browser's only way to the private Market API. Reads arrive at the loader, everything else at the action; both take the same path.
export const loader = ({ request, context }: Route.LoaderArgs) => proxy(request, context);
export const action = ({ request, context }: Route.ActionArgs) => proxy(request, context);

/** Request headers the browser may pass on; everything else (cookies, Authorization, X-Market-*, X-Forwarded-*) stays behind */
const forwardedRequestHeaders = ['Accept', 'Content-Type', 'If-None-Match', 'If-Modified-Since'];
/** Response headers passed back; hop-by-hop headers, Set-Cookie, WWW-Authenticate, Server and the like never reach the browser */
const returnedResponseHeaders = ['Content-Type', 'Cache-Control', 'ETag', 'Last-Modified', 'Expires', 'Location', 'Retry-After'];

/**
 * 1. the method and path must be on the allowlist, or 404 not_found, with nothing forwarded;
 * 2. the cross-site checks (a change needs X-Market-Request: 1 and the site's own Origin, or Sec-Fetch-Site: same-origin);
 * 3. the session cookie gives the token (not for the public icon files);
 * 4. forwarded with the service key, the bearer token and X-Market-Client-Ip;
 * 5. the API's status and body come back with only the headers above; an API 401 to a signed-in request clears the session cookie.
 */
async function proxy(request: Request, context: Route.LoaderArgs['context']): Promise<Response> {
  const url = new URL(request.url);
  const route = allowedRoute(request.method, url.pathname);
  if (!route) return Response.json({ error: 'not_found' }, { status: 404 });

  const refusal = crossSiteRefusal(request, context.bff, { requireRequestHeader: true });
  if (refusal) return refusal;

  const settings = context.bff;
  const token = 'withoutToken' in route ? null : await readSessionToken(request, settings);
  const address = clientIp(request, context);
  if (address === null) return noClientIp();

  const headers = new Headers(apiHeaders(settings, address, token));
  for (const name of forwardedRequestHeaders) {
    const value = request.headers.get(name);
    if (value !== null) headers.set(name, value);
  }

  let answer: Response;
  try {
    answer = await fetch(settings.apiUrl + url.pathname + url.search, {
      method: request.method,
      headers,
      body: request.method === 'GET' ? undefined : await request.arrayBuffer(),
      redirect: 'manual',
    });
  } catch {
    // the request may or may not have reached the API: the browser's "response lost, retry this attempt" case
    return Response.json({ error: 'network' }, { status: 502 });
  }

  const returned = new Headers();
  for (const name of returnedResponseHeaders) {
    const value = answer.headers.get(name);
    if (value !== null) returned.set(name, value);
  }
  if (answer.status === 401 && token !== null) returned.append('Set-Cookie', await clearedSessionCookieHeader(settings));

  const empty = answer.status === 204 || answer.status === 304;
  return new Response(empty ? null : answer.body, { status: answer.status, headers: returned });
}
