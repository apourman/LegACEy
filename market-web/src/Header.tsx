import { useState } from 'react';
import { Link } from 'react-router';
import { errorMessage, messages } from './api';
import { useSession } from './session';

export function Header() {
  const session = useSession();
  const [error, setError] = useState('');
  async function logout() { try { await session.logout(); } catch (e) { setError(errorMessage(e, 'Sign-out failed.')); } }
  return <><header><Link to="/" className="brand">LegACEy <span>MARKET</span></Link><nav aria-label="Main"><Link to="/">Browse</Link>
    {session.me ? <><Link to="/vault">Vault</Link><Link to="/history">History</Link><span>{session.me.accountName}</span><strong className="balance">{session.me.balance} MMD</strong>
      <label className="character-picker">Acting character<select value={session.characterId ?? ''} onChange={e => session.selectCharacter(Number(e.target.value))} disabled={session.characterId === null}>
        {session.me.characters.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}{session.me.characters.length === 0 && <option value="">No characters</option>}</select></label>
      <button className="secondary" onClick={() => void logout()}>Sign out</button></> : <Link to="/signin">Sign in</Link>}
  </nav></header>{session.me?.paused && <p role="status" className="pause-banner">The market is paused. Purchases and MMD withdrawals are temporarily unavailable.</p>}
  {session.ended && <p role="status" className="notice">{messages.unauthorized}</p>}
  {(error || session.error) && <p role="alert" className="notice">{error || session.error}</p>}</>;
}
