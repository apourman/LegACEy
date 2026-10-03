import type { AppLoadContext } from 'react-router';
import { ApiError, errorMessage, getMe, type Me } from '../api';
import { characterCookieName, chooseCharacter, readCookie } from '../character';
import { apiFor } from './api.server';

/** What the header needs on the first render: the account (null when signed out), its acting character, and an error to show */
export interface PageAccount { me: Me | null; characterId: number | null; error: string }

/**
 * The visitor's account for the root layout. Signed out without a valid session cookie. A 401 from the API ends the session: the cookie is
 * cleared, the page renders signed out, and sessionEnded tells it to say so. The acting character comes from market-character-<accountId>,
 * checked against the account, falling back to the first character.
 */
export async function pageAccount(request: Request, context: AppLoadContext) {
  const api = await apiFor(request, context);
  const account: PageAccount = { me: null, characterId: null, error: '' };
  if (api.token === null) return api.respondClearingEndedSession(account);

  try {
    const me = await getMe(api.client);
    account.me = me;
    account.characterId = chooseCharacter(me, readCookie(request.headers.get('Cookie'), characterCookieName(me.accountId)));
  } catch (e) {
    if (!(e instanceof ApiError && e.status === 401)) account.error = errorMessage(e, 'Could not load your account.');
  }
  return api.respondClearingEndedSession(account);
}
