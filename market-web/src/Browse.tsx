import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, useNavigation, useSearchParams } from 'react-router';
import { browse, errorMessage, suggest, type BrowseResult, type Facets } from './api';
import { AppraisalPopover } from './Appraisal';
import { LocalTime } from './LocalTime';

const columns = [
  ['Name', 'name'], ['Type', ''], ['Workmanship', 'workmanship'], ['Wield level', 'level'],
  ['Arcane lore', 'arcane'], ['Price', 'price'], ['Seller', 'seller'], ['Listed time', 'newest'],
];
/**
 * The listings for the address's filters and sort. The first page and the facets come from the route's loader (rendered on the server);
 * "Load more" adds pages from the browser. A new address replaces everything with the loader's new first page.
 */
export function Browse({ first, facets, loadError }: { first: BrowseResult; facets: Facets | null; loadError: string }) {
  const [params, setParams] = useSearchParams();
  const query = params.toString();
  const navigation = useNavigation();
  // a new search is on its way: its old rows go at once, as they did when the page fetched them itself
  const searching = navigation.state === 'loading' && navigation.location.pathname === '/';
  const [pages, setPages] = useState({ first, result: first });
  const [moreLoading, setMoreLoading] = useState(false);
  const [moreError, setMoreError] = useState('');
  const [name, setName] = useState(params.get('q') ?? '');
  const [suggestions, setSuggestions] = useState<string[]>([]);
  const generation = useRef(0);
  const loadingMore = useRef(false);
  if (pages.first !== first) {
    setPages({ first, result: first }); setMoreLoading(false); setMoreError('');
  }
  useEffect(() => {
    generation.current++;
    loadingMore.current = false;
    setName(params.get('q') ?? '');
  }, [first]);
  useEffect(() => {
    let active = true; setSuggestions([]);
    if (!name.trim()) return;
    const timer = setTimeout(() => { void suggest(name).then(r => { if (active) setSuggestions(r.suggestions); }, () => {}); }, 250);
    return () => { active = false; clearTimeout(timer); };
  }, [name]);
  const result: BrowseResult = searching ? { listings: [], nextCursor: null } : pages.result;
  const loading = searching || moreLoading;
  const error = searching ? '' : moreError || loadError;
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
    const version = generation.current; loadingMore.current = true; setMoreLoading(true); setMoreError('');
    const next = new URLSearchParams(query); next.set('cursor', result.nextCursor);
    try {
      const page = await browse(next);
      if (version === generation.current) setPages(old => ({ ...old, result: { listings: [...old.result.listings, ...page.listings.filter(row => !old.result.listings.some(existing => existing.id === row.id))], nextCursor: page.nextCursor } }));
    } catch (e) { if (version === generation.current) setMoreError(errorMessage(e, 'Could not load listings.')); }
    finally { if (version === generation.current) { setMoreLoading(false); loadingMore.current = false; } }
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
      <td className="desktop-column"><LocalTime iso={item.listedTime} /></td>
    </tr>)}</tbody></table>}
    {result.nextCursor && <button className="load-more" disabled={loading} onClick={() => void more()}>Load more</button>}
  </>;
}
