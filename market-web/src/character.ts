import type { Me } from './api';

// The acting character: a small, non-secret cookie per account, so the server renders the right one. Shared by the server and the browser.

export const characterCookieName = (accountId: number) => `market-character-${accountId}`;

/** The saved choice if it is still one of the account's characters, otherwise the first character, or null when there are none */
export function chooseCharacter(me: Me, saved: string | null | undefined): number | null {
  const id = saved !== null && saved !== undefined && /^\d+$/.test(saved) ? Number(saved) : null;
  return me.characters.find(c => c.id === id)?.id ?? me.characters[0]?.id ?? null;
}

/** One cookie's value from a Cookie header (or document.cookie), or null */
export function readCookie(header: string | null | undefined, name: string): string | null {
  for (const part of (header ?? '').split(';')) {
    const separator = part.indexOf('=');
    if (separator > 0 && part.slice(0, separator).trim() === name) {
      try { return decodeURIComponent(part.slice(separator + 1).trim()); } catch { return null; }
    }
  }
  return null;
}

/** Saves the choice in the browser for a year */
export function writeCharacterCookie(accountId: number, characterId: number) {
  const secure = location.protocol === 'https:' ? '; Secure' : '';
  document.cookie = `${characterCookieName(accountId)}=${characterId}; Path=/; Max-Age=31536000; SameSite=Lax${secure}`;
}

/**
 * Moves a choice saved by the website before the BFF (localStorage, same key) into the cookie, once: the cookie wins if both exist, and the old
 * key is removed either way. Only a character still on the account is copied.
 */
export function migrateStoredCharacter(me: Me) {
  const key = characterCookieName(me.accountId);
  let stored: string | null;
  try { stored = localStorage.getItem(key); } catch { return; /* Storage can be disabled. */ }
  if (stored === null) return;
  if (readCookie(document.cookie, key) === null && /^\d+$/.test(stored) && me.characters.some(c => c.id === Number(stored)))
    writeCharacterCookie(me.accountId, Number(stored));
  try { localStorage.removeItem(key); } catch { /* nothing more to do */ }
}

/** The browser's acting character for the account: the migrated or saved cookie, checked against the account */
export function browserCharacter(me: Me): number | null {
  migrateStoredCharacter(me);
  return chooseCharacter(me, readCookie(document.cookie, characterCookieName(me.accountId)));
}
