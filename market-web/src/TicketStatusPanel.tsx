import { useEffect, useRef, useState } from 'react';
import { getTickets, type Ticket } from './api';
import { useSession } from './session';

function countdown(until: string | null, now: number) {
  if (!until) return null;
  const seconds = Math.ceil((Date.parse(until) - now) / 1000);
  if (seconds <= 0) return 'Finishing…';
  return `Finishing in ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

function title(ticket: Ticket, now: number) {
  if (ticket.status === 'WAITING') return 'Waiting for the game server';
  if (ticket.status === 'CLAIMED') {
    if (ticket.progress === 'awaiting_confirmation') return `Confirm in game · ${countdown(ticket.progressUntil, now) ?? '30 seconds'}`;
    if (ticket.progress === 'channelling') return `Channelling · ${countdown(ticket.progressUntil, now) ?? 'in progress'}`;
    return 'Working';
  }
  if (ticket.status === 'DONE') return ticket.resultMessage || 'Done';
  return ticket.resultMessage || `Failed${ticket.resultCode ? ` · ${ticket.resultCode}` : ''}`;
}

function description(ticket: Ticket) {
  if (ticket.kind === 'mmd_withdraw') return `Withdraw ${ticket.amount ?? 'MMD'} MMD`;
  if (ticket.kind === 'vault_withdraw') return `Withdraw Vault item #${ticket.itemGuid ?? 'unknown'}`;
  return `Market request #${ticket.id}`;
}

export function TicketStatusPanel() {
  const session = useSession();
  const [tickets, setTickets] = useState<Ticket[]>([]);
  const [error, setError] = useState('');
  const [now, setNow] = useState(Date.now());
  const previous = useRef<Map<number, Ticket['status']>>(new Map());
  const polling = useRef(false);

  useEffect(() => {
    setTickets([]);
    previous.current.clear();
    if (!session.me) return;

    let interval: ReturnType<typeof setInterval>;
    let active = true;
    const poll = async () => {
      if (polling.current) return;
      polling.current = true;
      try {
        const next = await getTickets();
        if (!active) return;
        const was = previous.current;
        const justFinished = next.some(ticket => {
          const old = was.get(ticket.id);
          const finished = ticket.status === 'DONE' || ticket.status === 'FAILED';
          const transitioned = old !== undefined && old !== 'DONE' && old !== 'FAILED' && finished;
          const completedDuringFirstPoll = old === undefined && finished && Date.now() - Date.parse(ticket.createdTime) < 10_000;
          return transitioned || completedDuringFirstPoll;
        });
        previous.current = new Map(next.map(ticket => [ticket.id, ticket.status]));
        setTickets(next);
        setError('');
        if (justFinished) {
          await session.refresh();
          window.dispatchEvent(new Event('market-ticket-finished'));
        }
      } catch (e) {
        if (active) setError(e instanceof Error ? e.message : 'Could not refresh request status.');
      } finally { polling.current = false; }
    };
    const schedule = () => {
      clearInterval(interval);
      interval = setInterval(() => void poll(), document.visibilityState === 'hidden' ? 10_000 : 2_000);
    };
    void poll();
    schedule();
    document.addEventListener('visibilitychange', schedule);
    const countdownTimer = setInterval(() => setNow(Date.now()), 1000);
    return () => {
      active = false;
      clearInterval(interval);
      clearInterval(countdownTimer);
      document.removeEventListener('visibilitychange', schedule);
      polling.current = false;
    };
  }, [session.me?.accountId]);

  if (!session.me) return null;
  return <section className="ticket-panel" aria-label="Market requests" aria-live="polite">
    <h2>Market requests</h2>
    {error && <p role="alert" className="ticket-error">{error}</p>}
    {tickets.length === 0 && !error && <p className="muted">No recent requests.</p>}
    <ul>{tickets.map(ticket => <li key={ticket.id}>
      <span><strong>{description(ticket)}</strong><small>Request #{ticket.id}</small></span>
      <span className={`ticket-status ticket-status--${ticket.status.toLowerCase()}`}>{title(ticket, now)}</span>
    </li>)}</ul>
  </section>;
}
