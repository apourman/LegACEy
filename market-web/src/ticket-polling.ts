import type { Ticket } from './api';
import { isUnfinishedTicket } from './ticket-presentation';

export const VISIBLE_POLL_MS = 2_000;
export const HIDDEN_POLL_MS = 10_000;

export function pollingDelay(visibility: DocumentVisibilityState | 'visible' | 'hidden'): number {
  return visibility === 'hidden' ? HIDDEN_POLL_MS : VISIBLE_POLL_MS;
}

export const shouldPollTickets = (tickets: readonly Ticket[]) => tickets.some(isUnfinishedTicket);

/** Keep finished tickets already watched, while allowing unfinished tickets from a fresh response in. */
export function mergeTicketResults(previous: ReadonlyMap<number, Ticket>, latest: readonly Ticket[], cleared = new Set<number>()) {
  const anyUnfinished = latest.some(isUnfinishedTicket);
  const merged = new Map(previous);
  for (const ticket of latest) {
    const shown = previous.get(ticket.id);
    if (shown || (anyUnfinished && !cleared.has(ticket.id))) merged.set(ticket.id, ticket);
  }
  return merged;
}
