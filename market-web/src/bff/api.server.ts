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
 * What every call from the BFF to the API shares, the /api/* proxy's and the loaders' and actions' alike: the visitor's session token (if any and
 * wanted), the headers that carry it with the service key and the player's address, and the rule that a 401 to a call that carried the token
 * means the session has ended, so the session cookie is cleared.
 */
export interface SessionCall {
  token: string | null;
  headers: Record<string, string>;
  /** True when an API answer with this status ends the visitor's session: a 401 to a call that carried the token */
  endsSession(status: number): boolean;
  /** Set-Cookie that removes the session cookie */
  clearedSessionCookie(): Promise<string>;
}

/**
 * The session call for this request. Throws the 400 Response when the player's address can't be told; a loader, action or the proxy that throws
 * a Response answers with it.
 * @param signedIn false: send no token even if the visitor has a session (sign-in, and the public icon files)
 */
export async function sessionCall(request: Request, context: AppLoadContext, { signedIn = true } = {}): Promise<SessionCall> {
  const settings = context.bff;
  const address = clientIp(request, context);
  if (address === null) throw noClientIp();

  const token = signedIn ? await readSessionToken(request, settings) : null;
  return {
    token,
    headers: apiHeaders(settings, address, token),
    endsSession: status => token !== null && status === 401,
    clearedSessionCookie: () => clearedSessionCookieHeader(settings),
  };
}

/**
 * One request's way to the private API, for loaders and actions: the generated client with the request's headers, and whether any answer said
 * the session has ended (401).
 */
export interface ApiAccess {
  client: MarketApiClient;
  token: string | null;
  /** Whether the API answered a call that carried the token with 401 */
  readonly sessionEnded: boolean;
  /**
   * The loader's answer, with sessionEnded added (the page shows "Your session has ended" from it) and, when it is true, the session cookie
   * cleared
   */
  respondClearingEndedSession<T extends object>(value: T, init?: ResponseInit): Promise<ReturnType<typeof data<T & { sessionEnded: boolean }>>>;
}

/** The API for this request, signed in with the visitor's session cookie if there is a valid one (see sessionCall) */
export async function apiFor(request: Request, context: AppLoadContext, options: { signedIn?: boolean } = {}): Promise<ApiAccess> {
  const call = await sessionCall(request, context, options);
  const client = createMarketApiClient({ baseUrl: context.bff.apiUrl, headers: call.headers });

  let sessionEnded = false;
  client.use({ onResponse: ({ response }) => { if (call.endsSession(response.status)) sessionEnded = true; } });

  return {
    client,
    token: call.token,
    get sessionEnded() { return sessionEnded; },
    async respondClearingEndedSession<T extends object>(value: T, init: ResponseInit = {}) {
      const headers = new Headers(init.headers);
      if (sessionEnded) headers.append('Set-Cookie', await call.clearedSessionCookie());
      return data({ ...value, sessionEnded }, { ...init, headers });
    },
  };
}
