import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { delistVaultItem, getVault, listVaultItem, withdrawMmd, withdrawVaultItem, type VaultItem } from './api';
import { Icon, VaultAppraisalPopover } from './Appraisal';
import { useSession } from './session';

const fallbackTicketAttempts = new Map<string, { payload: string; key: string }>();

function ticketAttempt(accountId: number, storageKey: string, payload: object): string {
  const serializedPayload = JSON.stringify(payload);
  const key = `market-ticket-attempt:${accountId}:${storageKey}`;
  try {
    const saved = sessionStorage.getItem(key);
    if (saved) {
      const attempt = JSON.parse(saved) as { payload: string; key: string };
      if (attempt.payload === serializedPayload) return attempt.key;
    }
    const idempotencyKey = crypto.randomUUID();
    const attempt = { payload: serializedPayload, key: idempotencyKey };
    sessionStorage.setItem(key, JSON.stringify(attempt));
    fallbackTicketAttempts.set(key, attempt);
    return idempotencyKey;
  } catch {
    const previous = fallbackTicketAttempts.get(key);
    if (previous?.payload === serializedPayload) return previous.key;
    const idempotencyKey = crypto.randomUUID();
    fallbackTicketAttempts.set(key, { payload: serializedPayload, key: idempotencyKey });
    return idempotencyKey;
  }
}

function clearTicketAttempt(accountId: number, storageKey: string) {
  const key = `market-ticket-attempt:${accountId}:${storageKey}`;
  fallbackTicketAttempts.delete(key);
  try { sessionStorage.removeItem(key); }
  catch { /* Storage can be unavailable in restricted browser contexts. */ }
}

export function Vault() {
  const session = useSession();
  const [items, setItems] = useState<VaultItem[]>([]);
  const [prices, setPrices] = useState<Record<number, string>>({});
  const [loading, setLoading] = useState(false);
  const [busyItem, setBusyItem] = useState<number | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [mmdAmount, setMmdAmount] = useState('');
  const [busyMmd, setBusyMmd] = useState(false);
  const generation = useRef(0);

  async function refresh() {
    const version = ++generation.current;
    setLoading(true);
    setError('');
    try {
      const result = await getVault();
      if (version === generation.current) setItems(result.items);
    } catch (e) {
      if (version === generation.current) setError(e instanceof Error ? e.message : 'Could not load your Vault.');
    } finally {
      if (version === generation.current) setLoading(false);
    }
  }

  useEffect(() => {
    if (!session.me) return;
    void refresh();
    const visible = () => {
      if (document.visibilityState === 'visible') {
        void refresh();
        void session.refresh();
      }
    };
    document.addEventListener('visibilitychange', visible);
    return () => { generation.current++; document.removeEventListener('visibilitychange', visible); };
  }, [session.me?.accountId]);

  useEffect(() => {
    const finished = () => { if (session.me) void refresh(); };
    window.addEventListener('market-ticket-finished', finished);
    return () => window.removeEventListener('market-ticket-finished', finished);
  }, [session.me?.accountId]);

  async function list(item: VaultItem) {
    const characterId = session.characterId;
    if (busyItem !== null || characterId === null) return;
    const text = prices[item.itemGuid] ?? '';
    const price = Number(text);
    if (!/^\d+$/.test(text) || !Number.isSafeInteger(price) || price < 1) {
      setError('The price must be a positive whole number of MMD.');
      return;
    }
    await act(item, () => listVaultItem(item.itemGuid, price, characterId), `${item.name} is listed for ${price} MMD.`, 'Could not list this item.');
  }

  async function delist(item: VaultItem) {
    if (busyItem !== null || item.listingId === null) return;
    const listingId = item.listingId;
    await act(item, () => delistVaultItem(listingId), `${item.name} has been delisted.`, 'Could not delist this item.');
  }

  async function withdraw(item: VaultItem) {
    const characterId = session.characterId;
    if (busyItem !== null || characterId === null || item.state !== 'held' || !session.me) return;
    const attemptKey = `vault:${item.itemGuid}`;
    const key = ticketAttempt(session.me.accountId, attemptKey, { characterId, itemGuid: item.itemGuid });
    const succeeded = await act(item, () => withdrawVaultItem(characterId, item.itemGuid, key),
      `Withdrawal requested for ${item.name}. Keep ${session.me?.characters.find(c => c.id === characterId)?.name ?? 'your character'} online.`,
      'Could not request this withdrawal.');
    if (succeeded) clearTicketAttempt(session.me.accountId, attemptKey);
  }

  async function withdrawNotes(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (busyMmd || session.characterId === null || !session.me) return;
    if (!/^\d+$/.test(mmdAmount)) { setError('Enter a whole number of MMD.'); return; }
    const amount = Number(mmdAmount);
    if (!Number.isSafeInteger(amount) || amount < 1) { setError('Enter at least 1 whole MMD.'); return; }
    if (amount > session.me.balance) { setError('The amount cannot exceed your MMD balance.'); return; }
    setBusyMmd(true); setError(''); setNotice('');
    const accountId = session.me.accountId;
    const attemptKey = 'mmd';
    const key = ticketAttempt(accountId, attemptKey, { characterId: session.characterId, amount });
    try {
      await withdrawMmd(session.characterId, amount, key);
      clearTicketAttempt(accountId, attemptKey);
      setNotice(`Withdrawal requested. Keep ${session.me.characters.find(c => c.id === session.characterId)?.name ?? 'your character'} online.`);
    } catch (e) { setError(e instanceof Error ? e.message : 'Could not request an MMD withdrawal.'); }
    finally { setBusyMmd(false); }
  }

  // Refreshes whether or not the action succeeds: a refusal such as not_held means the Vault changed under the page.
  async function act(item: VaultItem, action: () => Promise<unknown>, success: string, fallback: string): Promise<boolean> {
    setBusyItem(item.itemGuid); setError(''); setNotice('');
    let failure = '';
    try {
      await action();
    } catch (e) { failure = e instanceof Error ? e.message : fallback; }
    try {
      await Promise.all([refresh(), session.refresh()]);
    } finally {
      // set after the refresh, which clears the error it starts with
      if (failure) setError(failure); else setNotice(success);
      setBusyItem(null);
    }
    return failure === '';
  }

  if (session.loading) return <p role="status">Loading your account…</p>;
  if (!session.me) return <section className="empty"><h1>Your Vault</h1><p>Sign in to see and manage your Vault.</p><Link to="/signin" state={{ returnTo: '/vault' }}>Sign in</Link></section>;

  return <>
    <div className="page-title"><p className="eyebrow">YOUR ITEMS</p><h1>Vault</h1>
      <p className="muted">{session.me.vaultCount} / {session.me.vaultCap} items · {session.me.listingCount} / {session.me.listingCap} active listings</p>
      <p className="muted">Choose a held item to list it as your acting character. Only held items can be listed.</p>
      <p className="muted">For item or MMD withdrawals, your acting character must be online in game.</p>
      {session.characterId === null && <p>Create a character in game first.</p>}
    </div>
    {error && <p role="alert" className="notice">{error}</p>}
    {notice && <p role="status" className="notice">{notice}</p>}
    <section className="withdraw-panel" aria-labelledby="withdraw-mmd-heading">
      <div><h2 id="withdraw-mmd-heading">Withdraw MMD</h2><p className="muted">Trade notes go to your acting character. If their pack is full, the notes arrive at next login.</p></div>
      <form onSubmit={e => void withdrawNotes(e)}>
        <label>Whole MMD amount<input type="number" min="1" step="1" inputMode="numeric" value={mmdAmount} onChange={e => setMmdAmount(e.target.value)} /></label>
        <button disabled={busyMmd || session.characterId === null || session.me.paused}>{busyMmd ? 'Requesting…' : 'Withdraw MMD'}</button>
      </form>
    </section>
    {loading && <p role="status">Refreshing Vault…</p>}
    {!loading && items.length === 0 && <div className="empty"><h2>Your Vault is empty</h2><p>Items bought from the market and deposited in game appear here.</p></div>}
    {items.length > 0 && <div className="table-scroll"><table className="listing-table vault-table"><caption className="sr-only">Your Vault items</caption><thead><tr>
      <th><span className="sr-only">Appraisal</span></th><th>Item</th><th>State</th><th>Deposited by</th><th>Listed price</th><th>Action</th>
    </tr></thead><tbody>{items.map(item => {
      const character = session.me?.characters.find(c => c.id === item.characterId)?.name ?? 'Unknown character';
      return <tr key={item.itemGuid}>
        <td><VaultAppraisalPopover item={item} /></td>
        <td><span className="vault-item-name"><Icon icon={item.icon} />{item.name}<span className="stack">× {item.stackSize}</span></span></td>
        <td><span className={`vault-state vault-state--${item.state}`}>{item.state}</span>{item.ticketId !== null && <small className="muted">Ticket #{item.ticketId}</small>}</td>
        <td>{character}</td>
        <td>{item.price === null ? '—' : <><span className="price">{item.price} MMD</span>{item.expiresTime && <small className="muted">Expires {new Date(item.expiresTime).toLocaleDateString()}</small>}</>}</td>
        <td>{item.state === 'held' ? <div className="vault-actions"><div className="vault-action"><label>Whole MMD price<input type="number" min="1" step="1" inputMode="numeric" value={prices[item.itemGuid] ?? ''}
          onChange={e => setPrices(old => ({ ...old, [item.itemGuid]: e.target.value }))} /></label><button disabled={busyItem !== null || session.characterId === null} onClick={() => void list(item)}>{busyItem === item.itemGuid ? 'Listing…' : 'List item'}</button></div>
          <button className="secondary" disabled={busyItem !== null || session.characterId === null} onClick={() => void withdraw(item)}>{busyItem === item.itemGuid ? 'Requesting…' : 'Withdraw'}</button></div>
          : item.state === 'listed' ? <button className="secondary" disabled={busyItem !== null || item.listingId === null} onClick={() => void delist(item)}>{busyItem === item.itemGuid ? 'Delisting…' : 'Delist'}</button>
          : <span className="muted">Withdrawal in progress</span>}</td>
      </tr>;
    })}</tbody></table></div>}
  </>;
}
