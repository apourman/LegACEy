import { useEffect, type ReactNode } from 'react';
import { isRouteErrorResponse, Link, Links, Meta, Outlet, Scripts, ScrollRestoration, useMatches } from 'react-router';
import type { Route } from './+types/root';
import { pageAccount } from './bff/account.server';
import { Header } from './Header';
import { SessionProvider } from './session';
import { TicketStatusPanel } from './TicketStatusPanel';
import styles from './style.css?url';

export const links: Route.LinksFunction = () => [
  { rel: 'stylesheet', href: styles },
];

export const meta: Route.MetaFunction = () => [{ title: 'LegACEy Market' }];

/** The account, character, balance and pause for the header, so the first render is already right */
export function loader({ request, context }: Route.LoaderArgs) {
  return pageAccount(request, context);
}

// Once the page is running, the browser keeps the account current itself (/api/me, through the proxy); navigating doesn't ask the server again.
export const shouldRevalidate = () => false;

export function Layout({ children }: { children: ReactNode }) {
  return <html lang="en">
    <head>
      <meta charSet="UTF-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1.0" />
      <Meta />
      <Links />
    </head>
    <body>{children}<ScrollRestoration /><Scripts /></body>
  </html>;
}

export default function App({ loaderData }: Route.ComponentProps) {
  // the page is interactive from here (every child's effects have run): browser checks wait for this before acting
  useEffect(() => { document.documentElement.dataset.hydrated = 'true'; }, []);
  // any loader of this page (the account's, or Browse's and Listing's) that got a 401 for the visitor's session ended it, and cleared the cookie
  const pageSessionEnded = useMatches().some(match => match.id !== 'root' && endedSession(match.data));
  const initial = loaderData.sessionEnded || pageSessionEnded ? { me: null, characterId: null, error: '', sessionEnded: true } : loaderData;
  return <SessionProvider initial={initial} pageSessionEnded={pageSessionEnded}><a className="skip-link" href="#main">Skip to content</a><Header /><TicketStatusPanel /><main id="main"><Outlet /></main>
    <footer>LegACEy · Player marketplace</footer></SessionProvider>;
}

export function ErrorBoundary({ error }: Route.ErrorBoundaryProps) {
  const notFound = isRouteErrorResponse(error) && error.status === 404;
  return <main id="main"><section><h1>{notFound ? 'Page not found' : 'Something went wrong'}</h1>
    <p>{notFound ? 'There is nothing at this address.' : 'The page could not be shown. Reload and try again.'}</p><Link to="/">Back to browse</Link></section></main>;
}

/** True for a loader's answer that says the visitor's session ended while it ran (respondClearingEndedSession) */
function endedSession(data: unknown) {
  return typeof data === 'object' && data !== null && (data as { sessionEnded?: unknown }).sessionEnded === true;
}
