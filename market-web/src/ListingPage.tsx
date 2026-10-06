import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { type Detail } from './api';
import { Appraisal, Icon } from './Appraisal';
import { LocalTime } from './LocalTime';
import { PurchaseDialog } from './Purchase';
import { useSession } from './session';

/** What the listing route's loader found: the listing, or that it is gone (sold, delisted, expired or missing), or an error to show */
export interface ListingView { item: Detail | null; gone: boolean; error: string }

export function ListingPage({ view }: { view: ListingView }) {
  const { item, gone, error } = view;
  const [confirm, setConfirm] = useState(false);
  const session = useSession();
  const navigate = useNavigate();
  if (gone) return <div className="empty"><h1>No longer available</h1><p>This listing was sold, removed or expired.</p><Link to="/">Back to browse</Link></div>;
  if (error) return <p role="alert" className="notice">{error}</p>;
  if (!item) return <p role="status">Loading listing…</p>;
  function buy() {
    if (!session.me) { navigate('/signin', { state: { returnTo: `/listing/${item!.id}` } }); return; }
    if (session.characterId !== null) setConfirm(true);
  }
  return <>
    <Link to="/">← Back to browse</Link>
    <div className="page-title listing-title"><Icon icon={item.icon} /><div><p className="eyebrow">{item.itemType} · {item.material}</p><h1>{item.name}</h1><p className="muted">Listed <LocalTime iso={item.listedTime} /></p></div></div>
    <div className="listing-detail"><Appraisal item={item} /><aside className="purchase-card"><p className="eyebrow">WHOLE STACK PRICE</p><p className="big-price">{item.price} <small>MMD</small></p>
      <dl><dt>Stack size</dt><dd>{item.quantity}</dd><dt>Seller</dt><dd><Link to={`/?seller=${encodeURIComponent(item.seller)}`}>{item.seller}</Link></dd><dt>Wield</dt><dd>{item.wield ?? 'None'}</dd></dl>
      <button disabled={session.loading || !!session.me?.paused || (!!session.me && session.characterId === null)} onClick={buy}>Buy</button>
      {session.me && session.characterId === null && <p>Create a character in game first.</p>}
      {session.me?.paused && <p>The market is paused. Purchases are temporarily unavailable.</p>}
    </aside></div>
    {confirm && session.me && <PurchaseDialog item={item} close={() => setConfirm(false)} />}
  </>;
}
