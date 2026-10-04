import { useEffect, useRef, useState } from "react";
import { getTickets, type Ticket } from "./api";
import { useSession } from "./session";
import { announceTicketFinished, onTicketCreated } from "./tickets";
import {
    isUnfinishedTicket,
    ticketDescription,
    ticketStatusText,
} from "./ticket-presentation";
import {
    mergeTicketResults,
    pollingDelay,
    shouldPollTickets,
} from "./ticket-polling";

/**
 * The account's market requests, on every page while any is unfinished: then it shows every ticket GET /api/tickets returns, so it rebuilds
 * after a reload. It polls only while something is unfinished (every 2 seconds, 10 when the tab is hidden). A ticket that finishes while
 * shown refreshes the balance and the Vault, and stays shown with its result until cleared.
 */
export function TicketStatusPanel() {
    const session = useSession();
    const [tickets, setTickets] = useState<Ticket[]>([]);
    const [error, setError] = useState("");
    const [now, setNow] = useState(Date.now());
    const watched = useRef<Map<number, Ticket>>(new Map());
    const cleared = useRef<Set<number>>(new Set());

    const publish = () =>
        setTickets([...watched.current.values()].sort((a, b) => b.id - a.id));

    useEffect(() => {
        watched.current = new Map();
        cleared.current = new Set();
        publish();
        setError("");
        if (!session.me) return;

        let active = true;
        let inFlight = false;
        let timer: ReturnType<typeof setTimeout> | undefined;

        const schedule = () => {
            clearTimeout(timer);
            if (active && shouldPollTickets([...watched.current.values()]))
                timer = setTimeout(
                    () => void poll(),
                    pollingDelay(document.visibilityState),
                );
        };

        const poll = async () => {
            if (inFlight) return;
            inFlight = true;
            clearTimeout(timer);
            try {
                const latest = await getTickets();
                if (!active) return;
                const previous = watched.current;
                const finishedTickets = latest.filter((ticket) => {
                    const shown = previous.get(ticket.id);
                    return (
                        shown &&
                        isUnfinishedTicket(shown) &&
                        !isUnfinishedTicket(ticket)
                    );
                });
                watched.current = mergeTicketResults(
                    previous,
                    latest,
                    cleared.current,
                );
                publish();
                setError("");
                if (finishedTickets.length > 0) {
                    await session.refresh();
                    for (const ticket of finishedTickets)
                        announceTicketFinished(ticket);
                }
            } catch (e) {
                if (active)
                    setError(
                        e instanceof Error
                            ? e.message
                            : "Could not refresh request status.",
                    );
            } finally {
                inFlight = false;
                schedule();
            }
        };

        const stopWatchingCreated = onTicketCreated((ticket) => {
            watched.current.set(ticket.id, ticket);
            publish();
            void poll();
        });
        void poll();
        document.addEventListener("visibilitychange", schedule);
        const clock = setInterval(() => setNow(Date.now()), 1000);
        return () => {
            active = false;
            clearTimeout(timer);
            clearInterval(clock);
            stopWatchingCreated();
            document.removeEventListener("visibilitychange", schedule);
        };
    }, [session.me?.accountId]);

    function clearFinished() {
        for (const ticket of [...watched.current.values()]) {
            if (isUnfinishedTicket(ticket)) continue;
            watched.current.delete(ticket.id);
            cleared.current.add(ticket.id);
        }
        publish();
    }

    if (!session.me || (tickets.length === 0 && !error)) return null;
    return (
        <section
            className="ticket-panel"
            aria-label="Market requests"
            aria-live="polite"
        >
            <h2>Market requests</h2>
            {tickets.some((ticket) => !isUnfinishedTicket(ticket)) && (
                <button className="secondary" onClick={clearFinished}>
                    Clear finished
                </button>
            )}
            {error && (
                <p role="alert" className="ticket-error">
                    {error}
                </p>
            )}
            <ul>
                {tickets.map((ticket) => (
                    <li key={ticket.id}>
                        <span>
                            <strong>{ticketDescription(ticket)}</strong>
                            <small>Request #{ticket.id}</small>
                        </span>
                        <span
                            className={`ticket-status ticket-status--${ticket.status.toLowerCase()}`}
                        >
                            {ticketStatusText(ticket, now)}
                        </span>
                    </li>
                ))}
            </ul>
        </section>
    );
}
