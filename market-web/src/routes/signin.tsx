import { useState, type FormEvent } from 'react';
import { Link, useLocation, useNavigate } from 'react-router';
import { messages } from '../api';
import { useSession } from '../session';

export default function SignIn() {
  const session = useSession();
  const location = useLocation();
  const navigate = useNavigate();
  // a refusal from a form sent before the page's script ran (the sign-in action redirects back here with its code)
  const [error, setError] = useState(() => {
    const code = new URLSearchParams(location.search).get('error');
    return code ? messages[code] ?? messages.server : '';
  });
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
    {/* method and action: a form sent before the script runs posts to the BFF, never puts the password in the address */}
    <form method="post" action="/auth/sign-in" onSubmit={e => void submit(e)}><label>Account name<input name="account" autoComplete="username" required autoFocus /></label><label>Password<input name="password" type="password" autoComplete="current-password" required /></label>
      {error && <p role="alert" className="notice">{error}</p>}<button disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button></form>
    <Link to="/">Continue browsing</Link>
  </section>;
}
