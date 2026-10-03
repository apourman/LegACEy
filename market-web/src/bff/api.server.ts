import { data, type AppLoadContext } from 'react-router';
import { createMarketApiClient, type MarketApiClient } from '../api';
import { clientIp } from './client-ip.server';
import { clearedSessionCookieHeader, readSessionToken } from './cookies.server';
import type { BffSettings } from './settings.server';

/** The headers the private API reads: the service key, the player's address and, when signed in, the session token */
export function apiHeaders(settings: BffSettings, address: string, token: string | null): Record<string, string> {
  return {
    'X-Market-Service-Key': settings.serviceKey,
    'X-Market-Client-Ip': address,
    ...(token === null ? {} : { Authorization: `Bearer ${token}` }),
  };
}

/** 400 bad_request: the BFF can't tell who is asking, and won't guess (the API would count everyone as one address) */
export function noClientIp() {
  return Response.json({ error: 'bad_request' }, { status: 400 });
}

/**
 * One request's way to the private API, for loaders and actions: the generated client with the request's headers, and whether any answer said
 * the session has ended (401), in which case respond() clears the session cookie.
 */
export interface ApiAccess {
  client: MarketApiClient;
  token: string | null;
  /** The loader's or action's answer, with the session cookie cleared if the API answered any call with 401 while signed in */
  respond<T>(value: T, init?: ResponseInit): Promise<ReturnType<typeof data<T>>>;
}

/**
 * The API for this request, signed in with the visitor's session cookie if there is a valid one. Throws a 400 Response when the player's
 * address can't be told; a loader or action that throws a Response answers with it.
 */
export async function apiFor(request: Request, context: AppLoadContext, { signedIn = true } = {}): Promise<ApiAccess> {
  const settings = context.bff;
  const address = clientIp(request, context);
  if (address === null) throw noClientIp();

  const token = signedIn ? await readSessionToken(request, settings) : null;
  const client = createMarketApiClient({ baseUrl: settings.apiUrl, headers: apiHeaders(settings, address, token) });

  let ended = false;
  if (token !== null) client.use({ onResponse: ({ response }) => { if (response.status === 401) ended = true; } });

  return {
    client,
    token,
    async respond<T>(value: T, init: ResponseInit = {}) {
      const headers = new Headers(init.headers);
      if (ended) headers.append('Set-Cookie', await clearedSessionCookieHeader(settings));
      return data(value, { ...init, headers });
    },
  };
}
