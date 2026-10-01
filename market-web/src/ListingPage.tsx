import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ApiError, getListing, type Detail } from './api';
import { Appraisal, Icon } from './Appraisal';
import { PurchaseDialog } from './Purchase';
import { useSession } from './session';

export function ListingPage() {
  const { id } = useParams();
  const [item, setItem] = useState<Detail | null>(null);
  const [error, setError] = useState('');
  const [confirm, setConfirm] = useState(false);
  const [gone, setGone] = useState(false);
  const session = useSession();
  const navigate = useNavigate();
  useEffect(() => {
    let active = true; setItem(null); setError(''); setGone(false); setConfirm(false);
    void getListing(Number(id)).then(value => { if (active) setItem(value); }, e => {
      if (active) { setError(e.message); setGone(e instanceof ApiError && [404, 410].includes(e.status)); }
    });
    return () => { active = false; };
  }, [id]);
  if (gone) return <div className="empty"><h1>No longer available</h1><p>This listing was sold, removed or expired.</p><Link to="/">Back to browse</Link></div>;
  if (error) return <p role="alert" className="notice">{error}</p>;
  if (!item) return <p role="status">Loading listing…</p>;
  function buy() {
    if (!session.me) { navigate('/signin', { state: { returnTo: `/listing/${id}` } }); return; }
    if (session.characterId !== null) setConfirm(true);
  }
  return <>
    <Link to="/">← Back to browse</Link>
    <div className="page-title listing-title"><Icon icon={item.icon} /><div><p className="eyebrow">{item.itemType} · {item.material}</p><h1>{item.name}</h1><p className="muted">Listed <time dateTime={item.listedTime}>{new Date(item.listedTime).toLocaleString()}</time></p></div></div>
    <div className="listing-detail"><Appraisal item={item} /><aside className="purchase-card"><p className="eyebrow">WHOLE STACK PRICE</p><p className="big-price">{item.price} <small>MMD</small></p>
      <dl><dt>Stack size</dt><dd>{item.quantity}</dd><dt>Seller</dt><dd><Link to={`/?seller=${encodeURIComponent(item.seller)}`}>{item.seller}</Link></dd><dt>Wield</dt><dd>{item.wield ?? 'None'}</dd></dl>
      <button disabled={session.loading || !!session.me?.paused || (!!session.me && session.characterId === null)} onClick={buy}>Buy</button>
      {session.me && session.characterId === null && <p>Create a character in game first.</p>}
      {session.me?.paused && <p>The market is paused. Purchases are temporarily unavailable.</p>}
    </aside></div>
    {confirm && session.me && <PurchaseDialog item={item} close={() => setConfirm(false)} />}
  </>;
}
