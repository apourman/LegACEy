import { useEffect, useRef, useState } from 'react';
import { getTickets, ticketProgress, type Ticket } from './api';
import { useSession } from './session';
import { announceTicketFinished, onTicketCreated } from './tickets';

const isUnfinished = (ticket: Ticket) => ticket.status === 'WAITING' || ticket.status === 'CLAIMED';

function countdown(until: string | null, now: number) {
  if (!until) return null;
  const seconds = Math.ceil((Date.parse(until) - now) / 1000);
  if (seconds <= 0) return 'Finishing…';
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')} left`;
}

function statusText(ticket: Ticket, now: number) {
  if (ticket.status === 'WAITING') return 'Waiting for the game server';
  if (ticket.status === 'CLAIMED') {
    if (ticket.progress === ticketProgress.awaitingConfirmation) return `Confirm in game · ${countdown(ticket.progressUntil, now) ?? 'waiting for you'}`;
    if (ticket.progress === ticketProgress.channelling) return `Channelling · ${countdown(ticket.progressUntil, now) ?? 'in progress'}`;
    return 'Working';
  }
  if (ticket.status === 'DONE') return ticket.resultMessage || 'Done';
  return [ticket.resultMessage || 'Failed', ticket.resultCode && `(${ticket.resultCode})`].filter(Boolean).join(' ');
}

function description(ticket: Ticket) {
  if (ticket.kind === 'mmd_withdraw') return `Withdraw ${ticket.amount ?? ''} MMD`;
  if (ticket.kind === 'vault_withdraw') return ticket.itemGuid === null ? 'Withdraw a Vault item' : `Withdraw Vault item #${ticket.itemGuid}`;
  if (ticket.kind === 'vault_deposit') return ticket.itemGuid === null ? 'Deposit a pack item' : `Deposit item #${ticket.itemGuid}`;
  if (ticket.kind === 'inventory_snapshot') return 'Read in-game inventory';
  return `Market request #${ticket.id}`;
}

/**
 * The account's market requests, on every page while any is unfinished: then it shows every ticket GET /api/tickets returns, so it rebuilds
 * after a reload. It polls only while something is unfinished (every 2 seconds, 10 when the tab is hidden). A ticket that finishes while
 * shown refreshes the balance and the Vault, and stays shown with its result until cleared.
 */
export function TicketStatusPanel() {
  const session = useSession();
  const [tickets, setTickets] = useState<Ticket[]>([]);
  const [error, setError] = useState('');
  const [now, setNow] = useState(Date.now());
  const watched = useRef<Map<number, Ticket>>(new Map());
  const cleared = useRef<Set<number>>(new Set());

  const publish = () => setTickets([...watched.current.values()].sort((a, b) => b.id - a.id));

  useEffect(() => {
    watched.current = new Map();
    cleared.current = new Set();
    publish();
    setError('');
    if (!session.me) return;

    let active = true;
    let inFlight = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const schedule = () => {
      clearTimeout(timer);
      if (active && [...watched.current.values()].some(isUnfinished))
        timer = setTimeout(() => void poll(), document.visibilityState === 'hidden' ? 10_000 : 2_000);
    };

    const poll = async () => {
      if (inFlight) return;
      inFlight = true;
      clearTimeout(timer);
      try {
        const latest = await getTickets();
        if (!active) return;
        let finished = false;
        const anyUnfinished = latest.some(isUnfinished);
        for (const ticket of latest) {
          const shown = watched.current.get(ticket.id);
          if (shown && isUnfinished(shown) && !isUnfinished(ticket)) finished = true;
          // with nothing unfinished, only tickets already shown stay (with their results); nothing new appears
          if (shown || (anyUnfinished && !cleared.current.has(ticket.id))) watched.current.set(ticket.id, ticket);
        }
        publish();
        setError('');
        if (finished) {
          await session.refresh();
          announceTicketFinished();
        }
      } catch (e) {
        if (active) setError(e instanceof Error ? e.message : 'Could not refresh request status.');
      } finally {
        inFlight = false;
        schedule();
      }
    };

    const stopWatchingCreated = onTicketCreated(ticket => {
      watched.current.set(ticket.id, ticket);
      publish();
      void poll();
    });
    void poll();
    document.addEventListener('visibilitychange', schedule);
    const clock = setInterval(() => setNow(Date.now()), 1000);
    return () => {
      active = false;
      clearTimeout(timer);
      clearInterval(clock);
      stopWatchingCreated();
      document.removeEventListener('visibilitychange', schedule);
    };
  }, [session.me?.accountId]);

  function clearFinished() {
    for (const ticket of [...watched.current.values()]) {
      if (isUnfinished(ticket)) continue;
      watched.current.delete(ticket.id);
      cleared.current.add(ticket.id);
    }
    publish();
  }

  if (!session.me || (tickets.length === 0 && !error)) return null;
  return <section className="ticket-panel" aria-label="Market requests" aria-live="polite">
    <h2>Market requests</h2>
    {tickets.some(ticket => !isUnfinished(ticket)) && <button className="secondary" onClick={clearFinished}>Clear finished</button>}
    {error && <p role="alert" className="ticket-error">{error}</p>}
    <ul>{tickets.map(ticket => <li key={ticket.id}>
      <span><strong>{description(ticket)}</strong><small>Request #{ticket.id}</small></span>
      <span className={`ticket-status ticket-status--${ticket.status.toLowerCase()}`}>{statusText(ticket, now)}</span>
    </li>)}</ul>
  </section>;
}
