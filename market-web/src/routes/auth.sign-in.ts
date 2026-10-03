import { redirect } from 'react-router';
import type { Route } from './+types/auth.sign-in';
import { apiFor } from '../bff/api.server';
import { sessionCookieHeader } from '../bff/cookies.server';
import { crossSiteRefusal } from '../bff/csrf.server';

/**
 * POST /auth/sign-in { account, password }: the API's session sign-in with the player's address, then the session cookie. The answer names the
 * account; the token goes only into the signed HttpOnly cookie. The API's refusals (invalid_credentials, account_locked, ip_blocked, banned,
 * bad_request) come back as they are.
 *
 * The website's script sends JSON. A plain form post (the sign-in form sent before the page's script ran) gets a redirect instead: home when
 * signed in, back to /signin?error=<code> when refused.
 */
export async function action({ request, context }: Route.ActionArgs) {
  const refusal = crossSiteRefusal(request, context.bff);
  if (refusal) return refusal;
  if (request.method !== 'POST') return Response.json({ error: 'bad_request' }, { status: 405, headers: { Allow: 'POST' } });

  const form = !(request.headers.get('Content-Type') ?? '').includes('application/json');
  const { account, password } = await credentials(request, form);

  const answer = await signIn(request, context, account, password);
  if (form) return answer.ok ? redirect('/', { headers: answer.headers }) : redirect(`/signin?error=${encodeURIComponent(answer.code)}`);
  return answer.ok
    ? Response.json({ accountId: answer.accountId, accountName: answer.accountName }, { headers: answer.headers })
    : Response.json(answer.body, { status: answer.status });
}

async function credentials(request: Request, form: boolean): Promise<{ account: unknown; password: unknown }> {
  try {
    if (form) {
      const data = await request.formData();
      return { account: data.get('account'), password: data.get('password') };
    }
    const body: unknown = await request.json();
    return typeof body === 'object' && body !== null ? body as { account: unknown; password: unknown } : { account: null, password: null };
  } catch {
    return { account: null, password: null };
  }
}

type SignInAnswer =
  | { ok: true; accountId: number; accountName: string; headers: HeadersInit }
  | { ok: false; status: number; code: string; body: unknown };

async function signIn(request: Request, context: Route.ActionArgs['context'], account: unknown, password: unknown): Promise<SignInAnswer> {
  const refused = (status: number, body: unknown): SignInAnswer => {
    const code = typeof body === 'object' && body !== null && typeof (body as { error?: unknown }).error === 'string' ? (body as { error: string }).error : 'server';
    return { ok: false, status, code, body: typeof body === 'object' && body !== null ? body : { error: code } };
  };
  if (typeof account !== 'string' || typeof password !== 'string') return refused(400, { error: 'bad_request' });

  // never with the visitor's old token: sign-in starts a new session
  const api = await apiFor(request, context, { signedIn: false });
  let answer;
  try {
    answer = await api.client.POST('/api/auth/session', { body: { account, password } });
  } catch {
    return refused(502, { error: 'server' });
  }

  const { data: session, error, response } = answer;
  if (!session) return refused(response.ok ? 502 : response.status, error);

  return {
    ok: true, accountId: session.accountId, accountName: session.accountName,
    headers: { 'Set-Cookie': await sessionCookieHeader(session.token, new Date(session.absoluteExpiresTime), context.bff) },
  };
}
