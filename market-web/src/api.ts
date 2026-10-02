export interface Character { id: number; name: string }
export interface Me {
  accountId: number; accountName: string; characters: Character[]; balance: number;
  frozen: boolean; paused: boolean; vaultCount: number; vaultCap: number;
  listingCount: number; listingCap: number;
}
export interface Icon { layers: { kind: string; url: string }[]; glow: string | null }
export interface Listing {
  id: number; itemGuid: number; name: string; itemType: string; material: string | null;
  workmanship: number | null; level: number | null; arcaneLore: number | null;
  summary: string; quantity: number; price: number; seller: string; listedTime: string;
  wield: string | null; icon: Icon;
}
export interface Spell { name: string; cantrip: boolean }
export interface Detail extends Listing { lines: string[]; spells: Spell[] }
export interface BrowseResult { listings: Listing[]; nextCursor: string | null }
export interface Facets {
  itemTypes: { value: string; label: string; count: number }[];
  sorts: { value: string; label: string; defaultDir: string }[];
}
export interface PurchaseRequest { count: number; expectedPrice: number; characterId: number; idempotencyKey: string }
export interface Receipt { status: 'ok'; listingId: number; itemGuid: number; price: number; fee: number; balance: number }
export interface VaultItem {
  itemGuid: number; wcid: number; name: string; itemType: number; stackSize: number; state: 'held' | 'listed' | 'withdrawing';
  characterId: number; depositedTime: string; icon: Icon; listingId: number | null; price: number | null;
  expiresTime: string | null; ticketId: number | null;
}
export interface VaultResult { items: VaultItem[] }
export interface VaultAppraisal { lines: string[]; spells: Spell[] }
export interface HistoryTransfer {
  sequence: number; transferId: number; kind: string; amount: number; balanceAfter: number;
  time: string; text: string; memo: string | null;
}
export interface HistoryItem { id: number; itemGuid: number; kind: string; name: string; listingId: number | null; time: string; text: string }
export interface HistoryResult {
  balance: number; head: number; transfers: HistoryTransfer[]; items: HistoryItem[];
  nextTransfersBefore: number | null; nextSince: number | null; more: boolean; nextItemsBefore: number | null;
}

export const messages: Record<string, string> = {
  invalid_credentials: 'The account name or password is incorrect.',
  account_locked: 'Too many failed sign-ins. Your account is temporarily locked. Try again later.',
  ip_blocked: 'Too many failed sign-ins from this connection. Try again later.',
  banned: 'Your account is banned.',
  unauthorized: 'Your session has ended. Please sign in again.',
  ok: 'Purchased. Your item is in your Vault.',
  price_changed: 'The price has changed. Review the new price before trying again.',
  gone: 'This listing is no longer available.', not_found: 'This listing is no longer available.',
  insufficient_funds: 'You do not have enough MMD for this purchase.',
  own_listing: 'You cannot buy your own listing.',
  rate_limited: 'Too many purchase attempts. Wait a minute and try again.',
  paused: 'The market is paused. Purchases are temporarily unavailable.',
  busy: 'The server is busy. Try this purchase again.',
  key_reused: 'This request key was already used. Close this dialog and start a new purchase.',
  invalid_price: 'The price must be a positive whole number of MMD.',
  invalid_count: 'The whole stack must be purchased. Reload the listing.',
  invalid_character: 'Choose a character belonging to your account.',
  invalid_fee: 'The server could not calculate the fee. Try again later.',
  bad_request: 'The request could not be read. Reload and try again.',
  csrf: 'The request could not be verified. Reload and try again.',
  bad_sort: 'That sort order is not available.', bad_limit: 'That page size is not valid.',
  bad_price: 'Enter whole MMD prices.', bad_type: 'That item type is not available.',
  bad_cursor: 'This page has changed. Reload the search.',
  network: 'The response was lost. Retry this attempt to find out whether it succeeded.',
  server: 'The server is unavailable. Try again later.',
  not_in_vault: 'That item is not in your Vault.',
  not_held: 'Only held Vault items can be listed.',
  listing_limit: 'You have reached your active listing limit.',
  not_active: 'This listing is no longer active. It may have sold or expired.',
};
export class ApiError extends Error {
  constructor(public code: string, public status: number, public price?: number) {
    super(messages[code] ?? `The request failed (${code}).`);
  }
}
// A signed-in view subscribes here. Login's 401 is a password answer, not session expiry.
export const sessionEnded = new EventTarget();
async function request<T>(path: string, body?: object, login = false): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`/api${path}`, {
      method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin',
      headers: { 'X-Market-Request': '1', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch { throw new ApiError('network', 0); }
  if (response.status === 401 && !login) sessionEnded.dispatchEvent(new Event('ended'));
  let result;
  try { result = await response.json(); }
  catch { throw new ApiError('server', response.status); }
  if (!response.ok) throw new ApiError(result.error ?? 'server', response.status, result.price);
  return result as T;
}
export const getMe = () => request<Me>('/me');
export const signIn = (account: string, password: string) => request<{ ok: boolean }>('/auth/login', { account, password }, true);
export const signOut = () => request<{ ok: boolean }>('/auth/logout', {});
export const browse = (query: URLSearchParams) => request<BrowseResult>(`/listings?${query}`);
export const suggest = (q: string) => request<{ suggestions: string[] }>(`/listings/suggest?q=${encodeURIComponent(q)}`);
export const getFacets = () => request<Facets>('/facets');
export const getListing = (id: number) => request<Detail>(`/listings/${id}`);
export const purchase = (id: number, attempt: PurchaseRequest) => request<Receipt>(`/listings/${id}/purchase`, attempt);
export const getVault = () => request<VaultResult>('/vault');
export const getVaultAppraisal = (itemGuid: number) => request<VaultAppraisal>(`/vault/${itemGuid}`);
export const listVaultItem = (itemGuid: number, price: number, characterId: number) =>
  request<{ id: number; itemGuid: number; price: number; status: string; listedTime: string }>('/listings', { itemGuid, price, characterId });
export const delistVaultItem = (listingId: number) => request<{ id: number; itemGuid: number; status: string }>(`/listings/${listingId}/delist`, {});
export const getHistory = (query: URLSearchParams) => request<HistoryResult>(`/history?${query}`);
