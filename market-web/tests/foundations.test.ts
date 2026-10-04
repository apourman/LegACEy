import { describe, expect, it, vi } from 'vitest';
import { ApiError, messages, type Me } from '../src/api';
import { chooseCharacter } from '../src/character';
import { checkMmdAmount } from '../src/mmd';
import { makePurchaseAttempt } from '../src/purchase-attempt';
import { pollingDelay, shouldPollTickets } from '../src/ticket-polling';
import { countdown, isUnfinishedTicket, ticketDescription, ticketStatusText } from '../src/ticket-presentation';
import { clearTicketAttempt, ticketAttempt } from '../src/tickets';
import { withRestoredGlobalState, type GlobalStateSnapshot } from '../src/global-state';

const me: Me = {
  accountId: 3, accountName: 'Alpha', balance: 500, frozen: false, paused: false,
  characters: [{ id: 11, name: 'Main' }, { id: 12, name: 'Second' }],
  vaultCount: 0, vaultCap: 100, listingCount: 0, listingCap: 10,
};
const detail = {
  id: 10, itemGuid: 20, wcid: 30, name: 'Bone Slicer', itemType: 'MeleeWeapon', material: 'Steel', workmanship: 7,
  level: 100, arcaneLore: 200, summary: '34-52', quantity: 3, price: 120, seller: 'Bravo', listedTime: '2026-10-01T00:00:00Z',
  wield: 'Level 100', icon: { layers: [], glow: null }, lines: [], spells: [],
} as unknown as import('../src/api').Detail;

describe('purchase attempts', () => {
  const purchaseAnswerCodes = ['ok', 'price_changed', 'gone', 'not_found', 'insufficient_funds', 'own_listing', 'rate_limited', 'paused', 'busy', 'key_reused', 'invalid_price', 'invalid_count', 'invalid_character', 'invalid_fee', 'bad_request', 'unauthorized', 'csrf', 'network', 'server'] as const;

  it.each(purchaseAnswerCodes)('has a player-facing message for purchase answer %s', code => {
    expect(messages[code]).toBeTruthy();
    expect(new ApiError(code, 400).message).toBe(messages[code]);
  });

  it('snapshots character, whole stack, price, balance and key', () => {
    const attempt = makePurchaseAttempt(detail, me, 12, 'fixed-key');
    expect(attempt).toMatchObject({ characterId: 12, characterName: 'Second', count: 3, price: 120, balanceAfter: 380, idempotencyKey: 'fixed-key' });
    expect(attempt.request).toEqual({ count: 3, expectedPrice: 120, characterId: 12, idempotencyKey: 'fixed-key' });
  });

  it('rejects a character outside the account', () => {
    expect(() => makePurchaseAttempt(detail, me, 99, 'key')).toThrow('Choose a character');
  });

  it('creates a new key for a new price while retries can reuse the old key', () => {
    const first = makePurchaseAttempt(detail, me, 11, 'old-key');
    const retry = makePurchaseAttempt(detail, me, 11, first.idempotencyKey);
    const changed = makePurchaseAttempt({ ...detail, price: 150 }, me, 11, 'new-key');
    expect(retry.request).toEqual(first.request);
    expect(changed.request.expectedPrice).toBe(150);
    expect(changed.idempotencyKey).not.toBe(first.idempotencyKey);
  });
});

describe('ticket presentation and polling', () => {
  const base = { id: 4, kind: 'vault_deposit', characterId: 11, itemGuid: 20, amount: null, resultCode: null, resultMessage: null, progress: null, progressTime: null, progressUntil: null, result: null, createdTime: '', claimedTime: null, finishedTime: null };
  it('labels every status and progress', () => {
    expect(ticketStatusText({ ...base, status: 'WAITING' }, 0)).toBe('Waiting for the game server');
    expect(ticketStatusText({ ...base, status: 'CLAIMED', progress: 'awaiting_confirmation', progressUntil: '1970-01-01T00:00:10Z' }, 0)).toContain('Confirm in game · 0:10 left');
    expect(ticketStatusText({ ...base, status: 'CLAIMED', progress: 'channelling', progressUntil: '1970-01-01T00:00:00Z' }, 0)).toContain('Channelling · Finishing…');
    expect(ticketStatusText({ ...base, status: 'CLAIMED', progress: null }, 0)).toBe('Working');
    expect(ticketStatusText({ ...base, status: 'DONE', resultMessage: null }, 0)).toBe('Done');
    expect(ticketStatusText({ ...base, status: 'FAILED', resultCode: 'declined', resultMessage: 'Declined' }, 0)).toBe('Declined (declined)');
    expect(ticketDescription({ ...base, status: 'DONE', kind: 'vault_deposit', itemGuid: 20 })).toBe('Deposit item #20');
    expect(isUnfinishedTicket({ ...base, status: 'WAITING' })).toBe(true);
    expect(isUnfinishedTicket({ ...base, status: 'DONE' })).toBe(false);
  });

  it('handles countdown and the visible or hidden schedule', () => {
    expect(countdown(null, 0)).toBeNull();
    expect(countdown('1970-01-01T00:00:10Z', 0)).toBe('0:10 left');
    expect(countdown('1970-01-01T00:00:00Z', 0)).toBe('Finishing…');
    expect(pollingDelay('visible')).toBe(2_000);
    expect(pollingDelay('hidden')).toBe(10_000);
    expect(shouldPollTickets([{ ...base, status: 'CLAIMED' }])).toBe(true);
    expect(shouldPollTickets([{ ...base, status: 'FAILED' }])).toBe(false);
  });
});

describe('MMD and character choices', () => {
  it('accepts only positive whole amounts within the balance', () => {
    expect(checkMmdAmount('10', 10)).toEqual({ ok: true, amount: 10 });
    expect(checkMmdAmount('', 10)).toEqual({ ok: false, reason: 'whole' });
    expect(checkMmdAmount('1.5', 10)).toEqual({ ok: false, reason: 'whole' });
    expect(checkMmdAmount('0', 10)).toEqual({ ok: false, reason: 'positive' });
    expect(checkMmdAmount('11', 10)).toEqual({ ok: false, reason: 'balance' });
  });

  it('uses a valid saved character, then first character, then null', () => {
    expect(chooseCharacter(me, '12')).toBe(12);
    expect(chooseCharacter(me, '99')).toBe(11);
    expect(chooseCharacter({ ...me, characters: [] }, '12')).toBeNull();
  });
});

describe('stable ticket keys', () => {
  it('reuses the key only while account, action and payload are unchanged', () => {
    const first = ticketAttempt(77, 'mmd', { characterId: 4, amount: 10 });
    expect(ticketAttempt(77, 'mmd', { characterId: 4, amount: 10 })).toBe(first);
    expect(ticketAttempt(77, 'mmd', { characterId: 4, amount: 11 })).not.toBe(first);
    expect(ticketAttempt(78, 'mmd', { characterId: 4, amount: 10 })).not.toBe(first);
    clearTicketAttempt(77, 'mmd');
  });
});

describe('global-state restoration', () => {
  it('restores pause, bans and settings after a forced failure', async () => {
    const original = { paused: false, bannedAccounts: ['seed-bravo'], settings: { listingLimit: '200' } };
    let state: GlobalStateSnapshot = { paused: original.paused, bannedAccounts: [...original.bannedAccounts], settings: { ...original.settings } };
    const restore = vi.fn(async (snapshot: GlobalStateSnapshot) => { state = { paused: snapshot.paused, bannedAccounts: [...snapshot.bannedAccounts], settings: { ...snapshot.settings } }; });
    await expect(withRestoredGlobalState(original, async () => {
      state = { paused: true, bannedAccounts: [...state.bannedAccounts, 'seed-alpha'], settings: { ...state.settings, listingLimit: '1' } };
      throw new Error('forced midway failure');
    }, restore)).rejects.toThrow('forced midway failure');
    expect(state).toEqual(original);
    expect(restore).toHaveBeenCalledOnce();
  });
});
