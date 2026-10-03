import type { BffSettings } from './settings.server';

/** The header every state-changing /api/* request carries. A cross-site page can't send it without a CORS preflight, which nothing here answers. */
export const requestHeader = 'X-Market-Request';

const safeMethods = new Set(['GET', 'HEAD']);

export function csrfRefusal() {
  return Response.json({ error: 'csrf' }, { status: 403 });
}

/**
 * Null when the request may go on, otherwise the 403 { "error": "csrf" } answer. Reads (GET, HEAD) always go on. Anything else must come from the
 * site itself: its Origin is the site's origin, or, when it sends no Origin, Sec-Fetch-Site is same-origin. A request with neither is refused.
 * With requireRequestHeader (the /api/* proxy), it must also carry X-Market-Request: 1.
 *
 * Every action and the proxy call this themselves; the request handler (handler.server.ts) calls it first as well, for routes with no action.
 */
export function crossSiteRefusal(request: Request, settings: BffSettings, { requireRequestHeader = false } = {}): Response | null {
  if (safeMethods.has(request.method)) return null;

  if (requireRequestHeader && request.headers.get(requestHeader) !== '1') return csrfRefusal();

  const origin = request.headers.get('Origin');
  if (origin !== null) return origin === siteOrigin(request, settings) ? null : csrfRefusal();

  return request.headers.get('Sec-Fetch-Site') === 'same-origin' ? null : csrfRefusal();
}

/**
 * For the BFF's own POST-only actions (sign-in, sign-out): null when the request may go on, otherwise the cross-site refusal (403 csrf) or
 * 405 for any other method. Each action calls it itself.
 */
export function postOnlyRefusal(request: Request, settings: BffSettings): Response | null {
  const refusal = crossSiteRefusal(request, settings);
  if (refusal) return refusal;
  return request.method === 'POST' ? null : Response.json({ error: 'bad_request' }, { status: 405, headers: { Allow: 'POST' } });
}

/** The configured public origin, or the origin the request was addressed to */
function siteOrigin(request: Request, settings: BffSettings) {
  return settings.siteOrigin ?? new URL(request.url).origin;
}
