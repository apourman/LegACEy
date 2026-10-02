import type { Ticket } from './api';

// Idempotency keys for ticket-creating requests: one key per action, kept until the request succeeds, so a double click or a retry after a lost
// response makes one ticket. Keys live in sessionStorage, with an in-memory fallback where storage is unavailable.
type Attempt = { payload: string; key: string };
const fallbackAttempts = new Map<string, Attempt>();

const attemptStorageKey = (accountId: number, action: string) => `market-ticket-attempt:${accountId}:${action}`;

function readAttempt(storageKey: string): Attempt | undefined {
  try {
    const saved = sessionStorage.getItem(storageKey);
    if (saved) return JSON.parse(saved) as Attempt;
  } catch { /* Storage can be unavailable in restricted browser contexts. */ }
  return fallbackAttempts.get(storageKey);
}

/** The key for this action: the saved one while the payload is unchanged, otherwise a new one. */
export function ticketAttempt(accountId: number, action: string, payload: object): string {
  const storageKey = attemptStorageKey(accountId, action);
  const serializedPayload = JSON.stringify(payload);
  const saved = readAttempt(storageKey);
  if (saved?.payload === serializedPayload) return saved.key;

  const attempt = { payload: serializedPayload, key: crypto.randomUUID() };
  fallbackAttempts.set(storageKey, attempt);
  try { sessionStorage.setItem(storageKey, JSON.stringify(attempt)); }
  catch { /* Storage can be unavailable in restricted browser contexts. */ }
  return attempt.key;
}

export function clearTicketAttempt(accountId: number, action: string) {
  const storageKey = attemptStorageKey(accountId, action);
  fallbackAttempts.delete(storageKey);
  try { sessionStorage.removeItem(storageKey); }
  catch { /* Storage can be unavailable in restricted browser contexts. */ }
}

// Between the pages that create tickets and the status panel that watches them
const ticketCreatedEvent = 'market-ticket-created';
const ticketFinishedEvent = 'market-ticket-finished';

export const announceTicketCreated = (ticket: Ticket) => window.dispatchEvent(new CustomEvent<Ticket>(ticketCreatedEvent, { detail: ticket }));
export const announceTicketFinished = () => window.dispatchEvent(new Event(ticketFinishedEvent));

/** Subscribes to tickets the website creates; returns the unsubscribe function. */
export function onTicketCreated(handler: (ticket: Ticket) => void) {
  const listener = (e: Event) => handler((e as CustomEvent<Ticket>).detail);
  window.addEventListener(ticketCreatedEvent, listener);
  return () => window.removeEventListener(ticketCreatedEvent, listener);
}

/** Subscribes to watched tickets finishing; returns the unsubscribe function. */
export function onTicketFinished(handler: () => void) {
  window.addEventListener(ticketFinishedEvent, handler);
  return () => window.removeEventListener(ticketFinishedEvent, handler);
}
