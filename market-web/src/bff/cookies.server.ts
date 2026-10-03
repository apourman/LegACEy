import { createCookie, type Cookie } from 'react-router';
import type { BffSettings } from './settings.server';

/** The session cookie: the API's web session token, signed, readable by no script */
export const sessionCookieName = 'market_session';

const cookies = new Map<string, Cookie>();

function sessionCookie(settings: BffSettings): Cookie {
  let cookie = cookies.get(settings.cookieSecret);
  if (!cookie) {
    // Secure: browsers keep it on http://localhost as well
    cookie = createCookie(sessionCookieName, { secrets: [settings.cookieSecret], httpOnly: true, secure: true, sameSite: 'lax', path: '/' });
    cookies.set(settings.cookieSecret, cookie);
  }
  return cookie;
}

/** The web session token from a valid, signed session cookie, or null */
export async function readSessionToken(request: Request, settings: BffSettings): Promise<string | null> {
  const value: unknown = await sessionCookie(settings).parse(request.headers.get('Cookie'));
  if (typeof value !== 'object' || value === null) return null;
  const token = (value as { token?: unknown }).token;
  return typeof token === 'string' && token.length > 0 ? token : null;
}

/** Set-Cookie for a new session: persistent, ending when the session's absolute lifetime does */
export function sessionCookieHeader(token: string, expires: Date, settings: BffSettings): Promise<string> {
  return sessionCookie(settings).serialize({ token }, { expires });
}

/** Set-Cookie that removes the session cookie */
export function clearedSessionCookieHeader(settings: BffSettings): Promise<string> {
  return sessionCookie(settings).serialize('', { expires: new Date(0), maxAge: 0 });
}
