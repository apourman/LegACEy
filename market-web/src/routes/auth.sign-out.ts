import type { Route } from './+types/auth.sign-out';
import { errorBody } from '../api';
import { apiFor } from '../bff/api.server';
import { clearedSessionCookieHeader } from '../bff/cookies.server';
import { postOnlyRefusal } from '../bff/csrf.server';

/**
 * POST /auth/sign-out: the API revokes the session, then the cookie is cleared. When the API has already ended it (401), the cookie is cleared
 * too. When the API can't be reached or fails, the cookie stays, so the player can try again rather than leave a live session behind.
 */
export async function action({ request, context }: Route.ActionArgs) {
  const refusal = postOnlyRefusal(request, context.bff);
  if (refusal) return refusal;

  const api = await apiFor(request, context);
  const cleared = { 'Set-Cookie': await clearedSessionCookieHeader(context.bff) };
  if (api.token === null) return Response.json({ ok: true }, { headers: cleared });

  let response: Response;
  let error: unknown;
  try {
    ({ response, error } = await api.client.DELETE('/api/auth/session'));
  } catch {
    return Response.json({ error: 'server' }, { status: 502 });
  }

  if (response.ok || response.status === 401) return Response.json({ ok: true }, { headers: cleared });
  return Response.json(errorBody(error), { status: response.status });
}
