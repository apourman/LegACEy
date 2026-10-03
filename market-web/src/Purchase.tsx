import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { ApiError, getListing, purchase, type Detail, type Me, type PurchaseRequest } from './api';
import { useSession } from './session';

interface Attempt { item: Detail; request: PurchaseRequest; characterName: string; balanceAfter: number }
function makeAttempt(item: Detail, account: Me, characterId: number): Attempt {
  const character = account.characters.find(c => c.id === characterId);
  if (!character) throw new ApiError('invalid_character', 400);
  return { item, characterName: character.name, balanceAfter: account.balance - item.price,
    request: { count: item.quantity, expectedPrice: item.price, characterId: character.id, idempotencyKey: crypto.randomUUID() } };
}
export function PurchaseDialog({ item, close }: { item: Detail; close: () => void }) {
  const session = useSession();
  const [attempt, setAttempt] = useState<Attempt>(() => makeAttempt(item, session.me!, session.characterId!));
  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [bought, setBought] = useState(false);
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => { dialog.current?.showModal(); }, []);
  async function confirm() {
    if (inFlight.current || bought || session.me?.paused) return;
    inFlight.current = true; setBusy(true); setError(null);
    try {
      await purchase(attempt.item.id, attempt.request);
      setBought(true); await session.refresh();
    } catch (e) {
      const problem = e instanceof ApiError ? e : new ApiError('network', 0);
      setError(problem);
      if (problem.code === 'paused') await session.refresh();
    } finally { inFlight.current = false; setBusy(false); }
  }
  async function newPriceAttempt() {
    if (inFlight.current || !session.me || session.characterId === null) return;
    inFlight.current = true; setBusy(true);
    try {
      const fresh = await getListing(item.id);
      setAttempt(makeAttempt(fresh, session.me, session.characterId));
      setError(null);
    } catch (e) { setError(e instanceof ApiError ? e : new ApiError('network', 0)); }
    finally { inFlight.current = false; setBusy(false); }
  }
  const retryable = error && ['network', 'busy', 'server', 'rate_limited'].includes(error.code);
  return <dialog ref={dialog} onKeyDown={e => {
    if (e.key !== 'Tab') return;
    const controls = Array.from(e.currentTarget.querySelectorAll<HTMLElement>('button:not(:disabled), a[href], input:not(:disabled), select:not(:disabled)'));
    const first = controls[0], last = controls[controls.length - 1];
    if (!first) { e.preventDefault(); return; }
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }} onCancel={e => { e.preventDefault(); if (!busy) close(); }} aria-labelledby="purchase-title">
    <h2 id="purchase-title">{bought ? 'Purchase complete' : 'Confirm purchase'}</h2>
    <p><strong>{attempt.item.name} × {attempt.request.count}</strong></p>
    <dl><dt>Buying as</dt><dd>{attempt.characterName}</dd><dt>Whole stack price</dt><dd>{attempt.request.expectedPrice} MMD</dd><dt>Balance after purchase</dt><dd>{attempt.balanceAfter} MMD</dd></dl>
    {session.me?.paused && !bought && error?.code !== 'paused' && <p role="status" className="notice">The market is paused. Purchases are temporarily unavailable.</p>}
    {error && <p role="alert" className="notice">{error.message}{error.code === 'price_changed' && ` New price: ${error.price} MMD.`}</p>}
    {bought ? <><p role="status">Purchased. Your item is in your Vault.</p><Link className="button" to="/vault" onClick={close}>Go to Vault</Link></> :
      error?.code === 'price_changed' ? <button disabled={busy} onClick={() => void newPriceAttempt()}>Review new price</button> :
      (!error || retryable) && <button disabled={busy || session.me?.paused} onClick={() => void confirm()}>{busy ? 'Purchasing…' : retryable ? 'Retry this attempt' : 'Confirm purchase'}</button>}
    <button className="secondary" disabled={busy} onClick={close}>{bought ? 'Close' : 'Cancel'}</button>
  </dialog>;
}
