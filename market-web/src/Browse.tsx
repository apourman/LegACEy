import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { browse, getFacets, suggest, type BrowseResult, type Facets } from './api';
import { AppraisalPopover } from './Appraisal';

const columns = [
  ['Name', 'name'], ['Type', ''], ['Workmanship', 'workmanship'], ['Wield level', 'level'],
  ['Arcane lore', 'arcane'], ['Price', 'price'], ['Seller', 'seller'], ['Listed time', 'newest'],
];
export function Browse() {
  const [params, setParams] = useSearchParams();
  const query = params.toString();
  const [facets, setFacets] = useState<Facets | null>(null);
  const [result, setResult] = useState<BrowseResult>({ listings: [], nextCursor: null });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [name, setName] = useState(params.get('q') ?? '');
  const [suggestions, setSuggestions] = useState<string[]>([]);
  const generation = useRef(0);
  const loadingMore = useRef(false);
  useEffect(() => { let active = true; void getFacets().then(f => { if (active) setFacets(f); }, e => { if (active) setError(e.message); }); return () => { active = false; }; }, []);
  useEffect(() => {
    const version = ++generation.current;
    loadingMore.current = false;
    setName(params.get('q') ?? ''); setResult({ listings: [], nextCursor: null }); setLoading(true); setError('');
    // The cursor is owned by this page, never by a shared filter URL.
    const filters = new URLSearchParams(query); filters.delete('cursor');
    void browse(filters).then(r => { if (version === generation.current) setResult(r); }, e => { if (version === generation.current) setError(e.message); })
      .finally(() => { if (version === generation.current) setLoading(false); });
    return () => { generation.current++; };
  }, [query]);
  useEffect(() => {
    let active = true; setSuggestions([]);
    if (!name.trim()) return;
    const timer = setTimeout(() => { void suggest(name).then(r => { if (active) setSuggestions(r.suggestions); }, () => {}); }, 250);
    return () => { active = false; clearTimeout(timer); };
  }, [name]);
  function filter(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const next = new URLSearchParams(params); next.delete('cursor');
    for (const [key, value] of new FormData(event.currentTarget)) {
      const text = String(value).trim(); if (text) next.set(key, text); else next.delete(key);
    }
    setParams(next);
  }
  function seller(name: string) { const next = new URLSearchParams(params); next.set('seller', name); next.delete('cursor'); setParams(next); }
  function sort(key: string) {
    const next = new URLSearchParams(params);
    const current = params.get('sort') ?? 'newest';
    const currentDir = params.get('dir') ?? (current === 'newest' ? 'desc' : 'asc');
    next.set('sort', key); next.set('dir', current === key ? (currentDir === 'asc' ? 'desc' : 'asc') : (facets?.sorts.find(s => s.value === key)?.defaultDir ?? 'asc'));
    next.delete('cursor'); setParams(next);
  }
  async function more() {
    if (!result.nextCursor || loadingMore.current) return;
    const version = generation.current; loadingMore.current = true; setLoading(true); setError('');
    const next = new URLSearchParams(query); next.set('cursor', result.nextCursor);
    try {
      const page = await browse(next);
      if (version === generation.current) setResult(old => ({ listings: [...old.listings, ...page.listings.filter(row => !old.listings.some(existing => existing.id === row.id))], nextCursor: page.nextCursor }));
    } catch (e) { if (version === generation.current) setError(e instanceof Error ? e.message : 'Could not load listings.'); }
    finally { if (version === generation.current) { setLoading(false); loadingMore.current = false; } }
  }
  const sortKey = params.get('sort') ?? 'newest';
  const direction = params.get('dir') ?? (sortKey === 'newest' ? 'desc' : 'asc');
  return <>
    <div className="page-title"><p className="eyebrow">PLAYER MARKETPLACE</p><h1>Browse the market</h1><p className="muted">Find your next treasure. Prices are for the whole stack, in MMD.</p></div>
    <form key={query} className="filters" onSubmit={filter}>
      <label>Item name<input name="q" value={name} onChange={e => setName(e.target.value)} list="name-suggestions" placeholder="Search items…" /></label>
      <datalist id="name-suggestions">{suggestions.map(n => <option key={n} value={n} />)}</datalist>
      <label>Type<select name="type" defaultValue={params.get('type') ?? ''}><option value="">All types</option>{facets?.itemTypes.map(t => <option key={t.value} value={t.value}>{t.label} ({t.count})</option>)}</select></label>
      <label>Minimum price<input name="minPrice" type="number" step="1" min="0" defaultValue={params.get('minPrice') ?? ''} /></label>
      <label>Maximum price<input name="maxPrice" type="number" step="1" min="0" defaultValue={params.get('maxPrice') ?? ''} /></label>
      <label>Seller character<input name="seller" defaultValue={params.get('seller') ?? ''} /></label>
      <label>Sort<select name="sort" defaultValue={sortKey}>{facets?.sorts.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}</select></label>
      <label>Direction<select name="dir" defaultValue={direction}><option value="asc">Ascending</option><option value="desc">Descending</option></select></label>
      <button type="submit">Apply filters</button><button type="button" className="secondary" onClick={() => setParams({})}>Clear</button>
    </form>
    {error && <p role="alert" className="notice">{error}</p>}
    {loading && <p role="status">Loading listings…</p>}
    {!loading && !error && result.listings.length === 0 && <div className="empty"><h2>No listings match</h2><p>Try a broader name, type or price range.</p></div>}
    {result.listings.length > 0 && <table className="listing-table"><caption className="sr-only">Active market listings</caption><thead><tr><th><span className="sr-only">Appraisal</span></th>
      {columns.map(([label, key], i) => <th key={label} className={[1, 2, 3, 4, 7].includes(i) ? 'desktop-column' : undefined} aria-sort={key === sortKey ? (direction === 'asc' ? 'ascending' : 'descending') : undefined}>
        {key ? <button className="text-button" onClick={() => sort(key)}>{label}{key === sortKey ? (direction === 'asc' ? ' ↑' : ' ↓') : ''}</button> : label}</th>)}
    </tr></thead><tbody>{result.listings.map(item => <tr key={item.id}>
      <td><AppraisalPopover item={item} /></td>
      <td><Link to={`/listing/${item.id}`}>{item.name}</Link><span className="stack">× {item.quantity}</span><small className="muted">{item.summary}</small></td>
      <td className="desktop-column">{item.itemType}</td><td className="desktop-column">{item.workmanship ?? '—'}</td><td className="desktop-column">{item.level ?? '—'}</td><td className="desktop-column">{item.arcaneLore ?? '—'}</td>
      <td className="price">{item.price} <small>MMD</small></td><td><button className="text-button seller" onClick={() => seller(item.seller)}>{item.seller}</button></td>
      <td className="desktop-column"><time dateTime={item.listedTime}>{new Date(item.listedTime).toLocaleString()}</time></td>
    </tr>)}</tbody></table>}
    {result.nextCursor && <button className="load-more" disabled={loading} onClick={() => void more()}>Load more</button>}
  </>;
}
