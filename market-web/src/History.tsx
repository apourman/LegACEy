import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { getHistory, type HistoryItem, type HistoryTransfer } from './api';
import { useSession } from './session';

const PageSize = 50;

export function History() {
  const session = useSession();
  const [transfers, setTransfers] = useState<HistoryTransfer[]>([]);
  const [items, setItems] = useState<HistoryItem[]>([]);
  const [balance, setBalance] = useState(0);
  const [transferCursor, setTransferCursor] = useState<number | null>(null);
  const [itemCursor, setItemCursor] = useState<number | null>(null);
  // signed in, the page that arrives from the server says it is loading, not that there is no history: it loads in the browser
  const [loading, setLoading] = useState(session.me !== null);
  const [loadingTransfers, setLoadingTransfers] = useState(false);
  const [loadingItems, setLoadingItems] = useState(false);
  const [error, setError] = useState('');
  const generation = useRef(0);

  useEffect(() => {
    if (!session.me) return;
    const version = ++generation.current;
    setTransfers([]); setItems([]); setTransferCursor(null); setItemCursor(null); setLoading(true); setError('');
    const query = new URLSearchParams({ transfersLimit: String(PageSize), itemsLimit: String(PageSize) });
    void getHistory(query).then(result => {
      if (version !== generation.current) return;
      setBalance(result.balance);
      setTransfers(result.transfers);
      setItems(result.items);
      setTransferCursor(result.nextTransfersBefore);
      setItemCursor(result.nextItemsBefore);
    }, e => { if (version === generation.current) setError(e instanceof Error ? e.message : 'Could not load your history.'); })
      .finally(() => { if (version === generation.current) setLoading(false); });
    return () => { generation.current++; };
  }, [session.me?.accountId]);

  async function loadOlderTransfers() {
    if (transferCursor === null || loadingTransfers) return;
    const version = generation.current;
    setLoadingTransfers(true); setError('');
    const query = new URLSearchParams({ transfersLimit: String(PageSize), transfersBefore: String(transferCursor), itemsLimit: '1' });
    try {
      const result = await getHistory(query);
      if (version === generation.current) {
        setTransfers(old => [...old, ...result.transfers.filter(row => !old.some(existing => existing.sequence === row.sequence))]);
        setTransferCursor(result.nextTransfersBefore);
        setBalance(result.balance);
      }
    } catch (e) { if (version === generation.current) setError(e instanceof Error ? e.message : 'Could not load older transfers.'); }
    finally { if (version === generation.current) setLoadingTransfers(false); }
  }

  async function loadOlderItems() {
    if (itemCursor === null || loadingItems) return;
    const version = generation.current;
    setLoadingItems(true); setError('');
    const query = new URLSearchParams({ transfersLimit: '1', itemsLimit: String(PageSize), itemsBefore: String(itemCursor) });
    try {
      const result = await getHistory(query);
      if (version === generation.current) {
        setItems(old => [...old, ...result.items.filter(row => !old.some(existing => existing.id === row.id))]);
        setItemCursor(result.nextItemsBefore);
      }
    } catch (e) { if (version === generation.current) setError(e instanceof Error ? e.message : 'Could not load older item events.'); }
    finally { if (version === generation.current) setLoadingItems(false); }
  }

  if (session.loading) return <p role="status">Loading your account…</p>;
  if (!session.me) return <section className="empty"><h1>History</h1><p>Sign in to read your market history.</p><Link to="/signin" state={{ returnTo: '/history' }}>Sign in</Link></section>;

  return <>
    <div className="page-title"><p className="eyebrow">ACCOUNT RECORD</p><h1>History</h1><p className="muted">Current balance: <strong className="balance">{balance} MMD</strong></p></div>
    {error && <p role="alert" className="notice">{error}</p>}
    {loading && <p role="status">Loading history…</p>}
    {!loading && <div className="history-grid">
      <section aria-labelledby="transfer-history-title"><h2 id="transfer-history-title">MMD transfers</h2>
        {transfers.length === 0 ? <p className="empty">No transfers yet.</p> : <ol className="history-list">{transfers.map(transfer => <li key={transfer.sequence}>
          <time dateTime={transfer.time}>{new Date(transfer.time).toLocaleString()}</time><p>{transfer.text}</p>
          <small>Balance after: {transfer.balanceAfter} MMD</small>
        </li>)}</ol>}
        {transferCursor !== null && <button className="load-more" disabled={loadingTransfers} onClick={() => void loadOlderTransfers()}>{loadingTransfers ? 'Loading…' : 'Load older transfers'}</button>}
      </section>
      <section aria-labelledby="item-history-title"><h2 id="item-history-title">Item events</h2>
        {items.length === 0 ? <p className="empty">No item events yet.</p> : <ol className="history-list">{items.map(item => <li key={item.id}>
          <time dateTime={item.time}>{new Date(item.time).toLocaleString()}</time><p>{item.text}</p>
        </li>)}</ol>}
        {itemCursor !== null && <button className="load-more" disabled={loadingItems} onClick={() => void loadOlderItems()}>{loadingItems ? 'Loading…' : 'Load older item events'}</button>}
      </section>
    </div>}
  </>;
}
