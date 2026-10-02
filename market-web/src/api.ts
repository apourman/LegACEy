import createClient from 'openapi-fetch';
import type { components, paths } from './api-schema';

type NumericField = 'id' | 'itemGuid' | 'wcid' | 'accountId' | 'characterId' | 'listingId' | 'ticketId' | 'balance' | 'vaultCount' | 'vaultCap' | 'listingCount' | 'listingCap' | 'price' | 'fee' | 'sequence' | 'transferId' | 'balanceAfter' | 'head' | 'nextTransfersBefore' | 'nextSince' | 'nextItemsBefore' | 'amount' | 'count' | 'quantity' | 'workmanship' | 'level' | 'arcaneLore' | 'itemType' | 'stackSize' | 'tokenId';
type ApiShape<T> = T extends readonly (infer Item)[] ? ApiShape<Item>[]
  : T extends object ? { [K in keyof T]: K extends NumericField
    ? (null extends T[K] ? number | null : number)
    : ApiShape<T[K]> }
  : T;
type ApiSchema<Name extends keyof components['schemas']> = ApiShape<components['schemas'][Name]>;

export type Character = ApiSchema<'CharacterResponse'>;
export type Me = ApiSchema<'MeResponse'>;
export type Icon = ApiSchema<'IconResponse'>;
export type Listing = ApiSchema<'ListingResponse'>;
export type Spell = ApiSchema<'SpellResponse'>;
export type Detail = Listing & { lines: string[]; spells: Spell[] };
export type BrowseResult = ApiSchema<'BrowseResponse'>;
export type Facets = ApiSchema<'FacetsResponse'>;
export type PurchaseRequest = ApiSchema<'PurchaseRequest'>;
export type Receipt = ApiSchema<'PurchaseReceipt'>;
export type VaultItem = ApiSchema<'VaultItemResponse'>;
export type VaultResult = ApiSchema<'VaultResponse'>;
export type VaultAppraisal = ApiSchema<'AppraisalResponse'>;
export type HistoryTransfer = ApiSchema<'HistoryTransferResponse'>;
export type HistoryItem = ApiSchema<'HistoryItemResponse'>;
export type HistoryResult = ApiSchema<'HistoryResponse'>;
export type Ticket = ApiSchema<'TicketResponse'>;
export type InventorySnapshotItem = ApiSchema<'InventorySnapshotItemResponse'>;
export type InventorySnapshot = ApiSchema<'InventorySnapshotResponse'>;

export const ticketProgress = { awaitingConfirmation: 'awaiting_confirmation', channelling: 'channelling' } as const;

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
  paused: 'The market is paused. Purchases and MMD withdrawals are temporarily unavailable.',
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
  not_in_pack: 'That item is no longer in your pack.',
  worn: 'Take that item off before depositing it.',
  attuned: 'That item is attuned and cannot be deposited.',
  contains_attuned: 'That container holds an attuned item and cannot be deposited.',
  pet_out: 'Unsummon the pet before depositing this item.',
  container_not_empty: 'Empty this container before depositing it.',
  blocked_wcid: 'This item type cannot be deposited.',
  in_trade: 'Take that item out of the trade window before depositing it.',
  vault_full: 'Your Vault is full. List or withdraw something first.',
  recent_player_fight: 'You have been in a player fight too recently to use the Vault.',
  trading: 'Close the trade window before using the Vault.',
  channelling: 'You are already moving an item to or from your Vault.',
  not_available: 'The Vault is not available right now.',
  declined: 'You declined the deposit in game. Nothing was moved.',
  confirm_timeout: 'The deposit was not confirmed in time. Nothing was moved.',
  confirmation_busy: 'Another yes/no popup was already open. Nothing was moved.',
  not_held: 'Only held Vault items can be listed.',
  listing_limit: 'You have reached your active listing limit.',
  not_active: 'This listing is no longer active. It may have sold or expired.',
};

type ErrorBody = components['schemas']['ApiError'];
export class ApiError extends Error {
  constructor(public code: string, public status: number, public price?: number | string | null) {
    super(messages[code] ?? `The request failed (${code}).`);
  }
}

export const sessionEnded = new EventTarget();
type Client = ReturnType<typeof createClient<paths>>;
export function createMarketApiClient(baseUrl: string, fetcher: typeof fetch = fetch): Client {
  return createClient<paths>({ baseUrl, fetch: fetcher });
}

let client = createMarketApiClient('');
export function setMarketApiBaseUrl(baseUrl: string, fetcher?: typeof fetch): void {
  client = createMarketApiClient(baseUrl, fetcher);
}

async function result<T>(call: Promise<{ data?: unknown; error?: unknown; response: Response }>, login = false): Promise<T> {
  let response: Response;
  let data: unknown;
  let error: unknown;
  try {
    const answer = await call;
    ({ response, data, error } = answer);
  } catch {
    throw new ApiError('network', 0);
  }

  if (response.status === 401 && !login) sessionEnded.dispatchEvent(new Event('ended'));
  if (!response.ok) {
    const body = (error ?? data ?? {}) as ErrorBody;
    throw new ApiError(body.error ?? 'server', response.status, body.price);
  }
  if (data === undefined) throw new ApiError('server', response.status);
  return data as T;
}

const headers = { 'X-Market-Request': '1' };
type ListingQuery = NonNullable<paths['/api/listings']['get']['parameters']['query']>;
type HistoryQuery = NonNullable<paths['/api/history']['get']['parameters']['query']>;
export const getMe = () => result<Me>(client.GET('/api/me', { credentials: 'include', headers }));
export const signIn = (account: string, password: string) => result<components['schemas']['LoginResponse']>(client.POST('/api/auth/login', { body: { account, password }, credentials: 'include', headers }), true);
export const signOut = () => result<components['schemas']['OkResponse']>(client.POST('/api/auth/logout', { credentials: 'include', headers }));
export const browse = (query: URLSearchParams) => result<BrowseResult>(client.GET('/api/listings', { params: { query: Object.fromEntries(query) as ListingQuery }, credentials: 'include', headers }));
export const suggest = (q: string) => result<components['schemas']['SuggestionsResponse']>(client.GET('/api/listings/suggest', { params: { query: { q } }, credentials: 'include', headers }));
export const getFacets = () => result<Facets>(client.GET('/api/facets', { credentials: 'include', headers }));
export const getListing = (id: number) => result<Detail>(client.GET('/api/listings/{id}', { params: { path: { id } }, credentials: 'include', headers }));
export const purchase = (id: number, attempt: PurchaseRequest) => result<Receipt>(client.POST('/api/listings/{id}/purchase', { params: { path: { id } }, body: attempt, credentials: 'include', headers }));
export const getVault = () => result<VaultResult>(client.GET('/api/vault', { credentials: 'include', headers }));
export const getVaultAppraisal = (itemGuid: number) => result<VaultAppraisal>(client.GET('/api/vault/{itemGuid}', { params: { path: { itemGuid } }, credentials: 'include', headers }));
export const listVaultItem = (itemGuid: number, price: number, characterId: number) => result<components['schemas']['ListedResponse']>(client.POST('/api/listings', { body: { itemGuid, price, characterId }, credentials: 'include', headers }));
export const delistVaultItem = (listingId: number) => result<components['schemas']['DelistedResponse']>(client.POST('/api/listings/{id}/delist', { params: { path: { id: listingId } }, credentials: 'include', headers }));
export const getHistory = (query: URLSearchParams) => result<HistoryResult>(client.GET('/api/history', { params: { query: Object.fromEntries(query) as HistoryQuery }, credentials: 'include', headers }));
export const getTickets = () => result<Ticket[]>(client.GET('/api/tickets', { credentials: 'include', headers }));
export const getTicket = (id: number) => result<Ticket>(client.GET('/api/tickets/{id}', { params: { path: { id } }, credentials: 'include', headers }));
export const requestInventorySnapshot = (characterId: number, idempotencyKey: string) => result<Ticket>(client.POST('/api/inventory/snapshot', { body: { characterId, idempotencyKey }, credentials: 'include', headers }));
export const depositVaultItem = (characterId: number, itemGuid: number, idempotencyKey: string) => result<Ticket>(client.POST('/api/vault/deposit', { body: { characterId, itemGuid, idempotencyKey }, credentials: 'include', headers }));
export const withdrawVaultItem = (characterId: number, itemGuid: number, idempotencyKey: string) => result<Ticket>(client.POST('/api/vault/withdraw', { body: { characterId, itemGuid, idempotencyKey }, credentials: 'include', headers }));
export const withdrawMmd = (characterId: number, amount: number, idempotencyKey: string) => result<Ticket>(client.POST('/api/mmd/withdraw', { body: { characterId, amount, idempotencyKey }, credentials: 'include', headers }));
