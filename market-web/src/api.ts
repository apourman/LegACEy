import createClient, { type Client, type ClientOptions } from 'openapi-fetch';
import type { components, paths } from './api-schema';

type Schemas = components['schemas'];
export type Character = Schemas['CharacterResponse'];
export type Me = Schemas['MeResponse'];
export type Icon = Schemas['IconResponse'];
export type Listing = Schemas['ListingResponse'];
export type Spell = Schemas['SpellResponse'];
export type Detail = Schemas['ListingDetailResponse'];
export type BrowseResult = Schemas['BrowseResponse'];
export type Facets = Schemas['FacetsResponse'];
export type PurchaseRequest = Schemas['PurchaseRequest'];
export type Receipt = Schemas['PurchaseReceipt'];
export type VaultItem = Schemas['VaultItemResponse'];
export type VaultResult = Schemas['VaultResponse'];
export type VaultAppraisal = Schemas['AppraisalResponse'];
export type HistoryTransfer = Schemas['HistoryTransferResponse'];
export type HistoryItem = Schemas['HistoryItemResponse'];
export type HistoryResult = Schemas['HistoryResponse'];
export type Ticket = Schemas['TicketResponse'];
export type InventorySnapshotItem = Schemas['InventorySnapshotItemResponse'];
export type InventorySnapshot = Schemas['InventorySnapshotResponse'];

export const ticketProgress = { awaitingConfirmation: 'awaiting_confirmation', channelling: 'channelling' } as const;

// keyed by the API's error codes, so a code the API doesn't declare is a type error
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
} satisfies Partial<Record<Schemas['ApiError']['error'], string>>;

export class ApiError extends Error {
  constructor(public code: string, public status: number, public price?: number) {
    super(messages[code] ?? `The request failed (${code}).`);
  }
}
// A signed-in view subscribes here (in the browser). Sign-in's 401 is a password answer, not session expiry.
export const sessionEnded = new EventTarget();
const inBrowser = typeof window !== 'undefined';

export type MarketApiClient = Client<paths>;
/** Thrown when no answer arrived at all, as opposed to an answer that couldn't be read */
class NoAnswer extends Error {}
/**
 * A client for the Market API's routes, generated from its OpenAPI document. The one client for both sides of the website:
 * - in the browser, browserClient below: the BFF's own /api/* proxy, same origin;
 * - on the BFF's server, one per request (bff/api.server.ts): the private API's address, with the service key, the player's address and their token.
 */
export function createMarketApiClient(options?: ClientOptions): MarketApiClient {
  const client = createClient<paths>(options);
  client.use({ onError: () => new NoAnswer() });
  return client;
}
/** The browser's client: the BFF's /api/* proxy with the session cookie, and the header the BFF's cross-site check asks for */
export const browserClient = createMarketApiClient({ credentials: 'same-origin', headers: { 'X-Market-Request': '1' } });

/** The message to show for anything thrown: an Error's own, otherwise the fallback */
export const errorMessage = (thrown: unknown, fallback: string) => thrown instanceof Error ? thrown.message : fallback;

/** An error answer's body as the API's error shape ({ "error": code, price? }) as it came, or { "error": fallback } when it isn't one */
export function errorBody(body: unknown, fallback = 'server'): { error: string; price?: number | null } {
  return typeof body === 'object' && body !== null && typeof (body as { error?: unknown }).error === 'string' ? body as { error: string } : { error: fallback };
}

function apiErrorFrom(error: unknown, status: number) {
  const body = errorBody(error);
  return new ApiError(body.error, status, body.price ?? undefined);
}

async function result<T>(call: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  let answer;
  try { answer = await call; }
  catch (e) { throw new ApiError(e instanceof NoAnswer ? 'network' : 'server', 0); }
  const { data, error, response } = answer;
  if (response.status === 401 && inBrowser) sessionEnded.dispatchEvent(new Event('ended'));
  if (!response.ok) throw apiErrorFrom(error, response.status);
  if (data === undefined) throw new ApiError('server', response.status);
  return data;
}

/** What the BFF's sign-in answers: the account, never the token (that stays in the HttpOnly cookie) */
export interface SignedIn { accountId: number; accountName: string }

/** A POST to one of the BFF's own routes (sign-in, sign-out), with the same error handling as the API's routes */
async function bffPost<T>(path: string, body: unknown, login: boolean): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-Market-Request': '1' }, body: JSON.stringify(body) });
  } catch { throw new ApiError('network', 0); }
  let parsed: unknown;
  try { parsed = await response.json(); } catch { parsed = undefined; }
  if (response.status === 401 && !login && inBrowser) sessionEnded.dispatchEvent(new Event('ended'));
  if (!response.ok) throw apiErrorFrom(parsed, response.status);
  if (parsed === undefined) throw new ApiError('server', response.status);
  return parsed as T;
}
export const signIn = (account: string, password: string) => bffPost<SignedIn>('/auth/sign-in', { account, password }, true);
export const signOut = () => bffPost<{ ok: boolean }>('/auth/sign-out', {}, false);

// The query passes the page's URL values through as text, and the API refuses a bad one (bad_price, bad_limit) with its own message
type ListingQuery = NonNullable<paths['/api/listings']['get']['parameters']['query']>;
type HistoryQuery = NonNullable<paths['/api/history']['get']['parameters']['query']>;
export const getMe = (client = browserClient) => result(client.GET('/api/me'));
export const browse = (query: URLSearchParams, client = browserClient) => result(client.GET('/api/listings', { params: { query: Object.fromEntries(query) as ListingQuery } }));
export const suggest = (q: string, client = browserClient) => result(client.GET('/api/listings/suggest', { params: { query: { q } } }));
export const getFacets = (client = browserClient) => result(client.GET('/api/facets'));
export const getListing = (id: number, client = browserClient) => result(client.GET('/api/listings/{id}', { params: { path: { id } } }));
export const purchase = (id: number, attempt: PurchaseRequest, client = browserClient) => result(client.POST('/api/listings/{id}/purchase', { params: { path: { id } }, body: attempt }));
export const getVault = (client = browserClient) => result(client.GET('/api/vault'));
export const getVaultAppraisal = (itemGuid: number, client = browserClient) => result(client.GET('/api/vault/{itemGuid}', { params: { path: { itemGuid } } }));
export const listVaultItem = (itemGuid: number, price: number, characterId: number, client = browserClient) => result(client.POST('/api/listings', { body: { itemGuid, price, characterId } }));
export const delistVaultItem = (listingId: number, client = browserClient) => result(client.POST('/api/listings/{id}/delist', { params: { path: { id: listingId } } }));
export const getHistory = (query: URLSearchParams, client = browserClient) => result(client.GET('/api/history', { params: { query: Object.fromEntries(query) as HistoryQuery } }));
export const getTickets = (client = browserClient) => result(client.GET('/api/tickets'));
export const getTicket = (id: number, client = browserClient) => result(client.GET('/api/tickets/{id}', { params: { path: { id } } }));
export const requestInventorySnapshot = (characterId: number, idempotencyKey: string, client = browserClient) => result(client.POST('/api/inventory/snapshot', { body: { characterId, idempotencyKey } }));
export const depositVaultItem = (characterId: number, itemGuid: number, idempotencyKey: string, client = browserClient) => result(client.POST('/api/vault/deposit', { body: { characterId, itemGuid, idempotencyKey } }));
export const withdrawVaultItem = (characterId: number, itemGuid: number, idempotencyKey: string, client = browserClient) => result(client.POST('/api/vault/withdraw', { body: { characterId, itemGuid, idempotencyKey } }));
export const withdrawMmd = (characterId: number, amount: number, idempotencyKey: string, client = browserClient) => result(client.POST('/api/mmd/withdraw', { body: { characterId, amount, idempotencyKey } }));
