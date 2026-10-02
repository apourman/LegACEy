import { useState, type FormEvent } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Link, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { messages } from './api';
import { Browse } from './Browse';
import { ListingPage } from './ListingPage';
import { Vault } from './Vault';
import { History } from './History';
import { TicketStatusPanel } from './TicketStatusPanel';
import { SessionProvider, useSession } from './session';
import './style.css';

function Header() {
  const session = useSession();
  const [error, setError] = useState('');
  async function logout() { try { await session.logout(); } catch (e) { setError(e instanceof Error ? e.message : 'Sign-out failed.'); } }
  return <><header><Link to="/" className="brand">LegACEy <span>MARKET</span></Link><nav aria-label="Main"><Link to="/">Browse</Link>
    {session.me ? <><Link to="/vault">Vault</Link><Link to="/history">History</Link><span>{session.me.accountName}</span><strong className="balance">{session.me.balance} MMD</strong>
      <label className="character-picker">Acting character<select value={session.characterId ?? ''} onChange={e => session.selectCharacter(Number(e.target.value))} disabled={session.characterId === null}>
        {session.me.characters.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}{session.me.characters.length === 0 && <option value="">No characters</option>}</select></label>
      <button className="secondary" onClick={() => void logout()}>Sign out</button></> : <Link to="/signin">Sign in</Link>}
  </nav></header>{session.me?.paused && <p role="status" className="pause-banner">The market is paused. Purchases and MMD withdrawals are temporarily unavailable.</p>}
  {(error || session.error) && <p role="alert" className="notice">{error || session.error}</p>}</>;
}
function SignIn() {
  const session = useSession();
  const location = useLocation();
  const navigate = useNavigate();
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault(); if (busy) return; setBusy(true); setError('');
    const data = new FormData(e.currentTarget);
    try {
      await session.login(String(data.get('account')), String(data.get('password')));
      const returnTo = location.state?.returnTo;
      navigate(typeof returnTo === 'string' && returnTo.startsWith('/') && !returnTo.startsWith('//') && !returnTo.startsWith('/signin') ? returnTo : '/', { replace: true });
    } catch (e) { setError(e instanceof Error ? e.message : 'Sign-in failed.'); }
    finally { setBusy(false); }
  }
  return <section className="signin"><p className="eyebrow">YOUR GAME ACCOUNT</p><h1>Sign in</h1>
    {location.state?.ended && <p role="status" className="notice">{messages.unauthorized}</p>}
    <form onSubmit={e => void submit(e)}><label>Account name<input name="account" autoComplete="username" required autoFocus /></label><label>Password<input name="password" type="password" autoComplete="current-password" required /></label>
      {error && <p role="alert" className="notice">{error}</p>}<button disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button></form>
    <Link to="/">Continue browsing</Link>
  </section>;
}
function App() { return <SessionProvider><a className="skip-link" href="#main">Skip to content</a><Header /><TicketStatusPanel /><main id="main"><Routes>
  <Route path="/" element={<Browse />} /><Route path="/listing/:id" element={<ListingPage />} /><Route path="/signin" element={<SignIn />} /><Route path="/vault" element={<Vault />} /><Route path="/history" element={<History />} />
  <Route path="*" element={<section><h1>Page not found</h1><Link to="/">Back to browse</Link></section>} />
</Routes></main><footer>LegACEy · Player marketplace</footer></SessionProvider>; }
createRoot(document.getElementById('root')!).render(<BrowserRouter><App /></BrowserRouter>);
