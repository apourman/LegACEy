import { useEffect, useId, useState } from 'react';
import { ApiError, getListing, getVaultAppraisal, type Detail, type Icon as IconData, type Listing, type Spell, type VaultAppraisal, type VaultItem } from './api';

export function Icon({ icon }: { icon: IconData }) {
  return <span aria-hidden="true" className={`item-icon ${icon.glow ?? ''}`}>
    {icon.layers.map((layer, index) => <img key={`${layer.kind}-${index}`} src={layer.url} alt="" />)}
  </span>;
}
// Cantrips first, as the game lists them.
function SpellList({ spells }: { spells: Spell[] }) {
  if (spells.length === 0) return null;
  return <><h3>Spells</h3><ul>{[...spells.filter(s => s.cantrip), ...spells.filter(s => !s.cantrip)].map((spell, i) => <li key={i} className={spell.cantrip ? 'cantrip' : undefined}>{spell.name}</li>)}</ul></>;
}
export function Appraisal({ item }: { item: Detail }) {
  return <div className="appraisal">
    <p className="muted">{item.itemType} · {item.material ?? 'Unknown material'} · {item.wield ?? 'No wield requirement'}</p>
    <h2>Appraisal · as of listing</h2>
    <ul className="appraisal-lines">{item.lines.map((line, i) => <li key={i}>{line}</li>)}</ul>
    <SpellList spells={item.spells} />
  </div>;
}
// Only popovers use this cache. Listing pages read fresh data for purchase confirmation.
const details = new Map<number, Promise<Detail>>();
function cachedDetail(id: number) {
  let detail = details.get(id);
  if (!detail) {
    detail = getListing(id).catch(e => { details.delete(id); throw e; });
    details.set(id, detail);
  }
  return detail;
}
export function AppraisalPopover({ item }: { item: Listing }) {
  const [open, setOpen] = useState(false);
  const [detail, setDetail] = useState<Detail | null>(null);
  const [error, setError] = useState('');
  const id = useId();
  useEffect(() => {
    if (!open || detail) return;
    let active = true;
    void cachedDetail(item.id).then(value => { if (active) setDetail(value); }, e => { if (active) setError(e.message); });
    return () => { active = false; };
  }, [open, detail, item.id]);
  return <div className="popover-anchor" onPointerEnter={e => { if (e.pointerType === "mouse") setOpen(true); }} onPointerLeave={e => { if (e.pointerType === "mouse") setOpen(false); }}
    onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); } }}
    onFocus={() => setOpen(true)} onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget)) setOpen(false); }}>
    <button className="icon-button" aria-label={`Appraise ${item.name}`} aria-expanded={open} aria-controls={id}
      onClick={() => setOpen(true)}><Icon icon={item.icon} /></button>
    {open && <div id={id} role="region" aria-label={`${item.name} appraisal`} className="popover">
      <button className="close-popover" aria-label="Close appraisal" onClick={() => setOpen(false)}>×</button>
      <strong>{item.name} × {item.quantity}</strong><p>{item.price} MMD · {item.seller}</p>
      {detail ? <Appraisal item={detail} /> : <p role="status">{error || 'Loading appraisal…'}</p>}
    </div>}
  </div>;
}

export function VaultAppraisalPopover({ item }: { item: VaultItem }) {
  const [open, setOpen] = useState(false);
  const [appraisal, setAppraisal] = useState<VaultAppraisal | null>(null);
  const [error, setError] = useState('');
  const id = useId();
  useEffect(() => {
    if (!open || appraisal) return;
    let active = true;
    void getVaultAppraisal(item.itemGuid).then(value => { if (active) setAppraisal(value); },
      e => { if (active) setError(e instanceof ApiError && e.code === 'not_found' ? 'That item is no longer in your Vault.' : e.message); });
    return () => { active = false; };
  }, [open, appraisal, item.itemGuid]);
  return <div className="popover-anchor" onPointerEnter={e => { if (e.pointerType === 'mouse') setOpen(true); }} onPointerLeave={e => { if (e.pointerType === 'mouse') setOpen(false); }}
    onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); } }}
    onFocus={() => setOpen(true)} onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget)) setOpen(false); }}>
    <button className="icon-button" aria-label={`Appraise ${item.name}`} aria-expanded={open} aria-controls={id}
      onClick={() => setOpen(true)}><Icon icon={item.icon} /></button>
    {open && <div id={id} role="region" aria-label={`${item.name} appraisal`} className="popover">
      <button className="close-popover" aria-label="Close appraisal" onClick={() => setOpen(false)}>×</button>
      <strong>{item.name} × {item.stackSize}</strong>
      {appraisal ? <><ul className="appraisal-lines">{appraisal.lines.map((line, i) => <li key={i}>{line}</li>)}</ul><SpellList spells={appraisal.spells} /></>
        : <p role="status">{error || 'Loading appraisal…'}</p>}
    </div>}
  </div>;
}
