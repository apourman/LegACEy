import type { Ticket } from './api';

export const isUnfinishedTicket = (ticket: Ticket) => ticket.status === 'WAITING' || ticket.status === 'CLAIMED';

export function countdown(until: string | null, now: number): string | null {
  if (!until) return null;
  const seconds = Math.ceil((Date.parse(until) - now) / 1000);
  if (seconds <= 0) return 'Finishing…';
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')} left`;
}

export function ticketStatusText(ticket: Ticket, now: number): string {
  if (ticket.status === 'WAITING') return 'Waiting for the game server';
  if (ticket.status === 'CLAIMED') {
    if (ticket.progress === 'awaiting_confirmation') return `Confirm in game · ${countdown(ticket.progressUntil, now) ?? 'waiting for you'}`;
    if (ticket.progress === 'channelling') return `Channelling · ${countdown(ticket.progressUntil, now) ?? 'in progress'}`;
    return 'Working';
  }
  if (ticket.status === 'DONE') return ticket.resultMessage || 'Done';
  return [ticket.resultMessage || 'Failed', ticket.resultCode && `(${ticket.resultCode})`].filter(Boolean).join(' ');
}

export function ticketDescription(ticket: Ticket): string {
  if (ticket.kind === 'mmd_withdraw') return `Withdraw ${ticket.amount ?? ''} MMD`;
  if (ticket.kind === 'vault_withdraw') return ticket.itemGuid === null ? 'Withdraw a Vault item' : `Withdraw Vault item #${ticket.itemGuid}`;
  if (ticket.kind === 'vault_deposit') return ticket.itemGuid === null ? 'Deposit a pack item' : `Deposit item #${ticket.itemGuid}`;
  if (ticket.kind === 'inventory_snapshot') return 'Read in-game inventory';
  return `Market request #${ticket.id}`;
}
