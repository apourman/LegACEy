// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import type { Detail, Listing, Me, Ticket } from '../src/api';

const account: Me = { accountId: 1, accountName: 'Alpha', balance: 500, frozen: false, paused: false, characters: [{ id: 11, name: 'Alpha Main' }], vaultCount: 0, vaultCap: 100, listingCount: 0, listingCap: 20 };
const detail: Detail = { id: 1, itemGuid: 10, wcid: 20, name: 'Bone Slicer', itemType: 'MeleeWeapon', material: 'Steel', workmanship: 7, level: 100, arcaneLore: 200, summary: '34-52', quantity: 3, price: 120, seller: 'Bravo', listedTime: '2026-10-01T00:00:00Z', wield: 'Level 100', icon: { layers: [], glow: null }, lines: ['Damage: 34 - 52'], spells: [] };
const listing: Listing = { ...detail };
const ticket: Ticket = { id: 1, kind: 'vault_deposit', status: 'WAITING', characterId: 11, itemGuid: 10, amount: null, resultCode: null, resultMessage: null, progress: null, progressTime: null, progressUntil: null, result: null, createdTime: '', claimedTime: null, finishedTime: null };

vi.mock('../src/session', () => ({ useSession: () => ({ me: account, characterId: 11, loading: false, refresh: vi.fn(), selectCharacter: vi.fn() }) }));
vi.mock('../src/api', async () => {
  const actual = await vi.importActual<typeof import('../src/api')>('../src/api');
  return {
    ...actual,
    purchase: vi.fn(),
    getListing: vi.fn(async () => detail),
    getTickets: vi.fn(async () => [ticket]),
    getVault: vi.fn(async () => ({ items: [] })),
    withdrawMmd: vi.fn(async () => ticket),
  };
});

describe('foundation component wiring', () => {
  beforeEach(() => vi.clearAllMocks());

  it('wires the purchase dialog to one frozen attempt and shows success', async () => {
    HTMLDialogElement.prototype.showModal = function() { this.open = true; };
    const { PurchaseDialog } = await import('../src/Purchase');
    const { purchase } = await import('../src/api');
    vi.mocked(purchase).mockResolvedValue({ status: 'ok', listingId: 1, itemGuid: 10, price: 120, fee: 0, balance: 380 });
    render(<MemoryRouter><PurchaseDialog item={detail} close={vi.fn()} /></MemoryRouter>);
    expect(screen.getByText('Alpha Main')).toBeInTheDocument();
    expect(screen.getByText('380 MMD')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm purchase' }));
    await waitFor(() => expect(screen.getByText('Purchased. Your item is in your Vault.')).toBeInTheDocument());
    expect(purchase).toHaveBeenCalledWith(1, expect.objectContaining({ count: 3, expectedPrice: 120, characterId: 11, idempotencyKey: expect.any(String) }));
  });

  it('shows a ticket panel while unfinished, then hides it when finished', async () => {
    const { TicketStatusPanel } = await import('../src/TicketStatusPanel');
    const { getTickets } = await import('../src/api');
    render(<TicketStatusPanel />);
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Market requests' })).toBeInTheDocument());
    vi.mocked(getTickets).mockResolvedValue([{ ...ticket, status: 'DONE', resultMessage: 'Deposit complete' }]);
    document.dispatchEvent(new Event('visibilitychange'));
    await waitFor(() => expect(screen.getByText('Deposit complete')).toBeInTheDocument(), { timeout: 4_000 });
    fireEvent.click(screen.getByRole('button', { name: 'Clear finished' }));
    expect(screen.queryByRole('heading', { name: 'Market requests' })).not.toBeInTheDocument();
  });

  it('validates whole MMD amounts and calls the withdrawal action for a valid amount', async () => {
    const { Vault } = await import('../src/Vault');
    const { withdrawMmd } = await import('../src/api');
    render(<MemoryRouter><Vault /></MemoryRouter>);
    const input = screen.getByLabelText('Whole MMD amount');
    fireEvent.change(input, { target: { value: '0' } });
    fireEvent.submit(input.closest('form')!);
    expect(await screen.findByRole('alert')).toHaveTextContent('at least 1');
    fireEvent.change(input, { target: { value: '501' } });
    fireEvent.submit(input.closest('form')!);
    expect(await screen.findByRole('alert')).toHaveTextContent('cannot exceed');
    fireEvent.change(input, { target: { value: '25' } });
    fireEvent.click(screen.getByRole('button', { name: 'Withdraw MMD' }));
    await waitFor(() => expect(withdrawMmd).toHaveBeenCalledWith(11, 25, expect.any(String)));
  });

  it('opens appraisal on hover, tap and focus, and closes on Escape', async () => {
    const { AppraisalPopover } = await import('../src/Appraisal');
    render(<AppraisalPopover item={listing} />);
    const button = screen.getByRole('button', { name: 'Appraise Bone Slicer' });
    fireEvent.pointerEnter(button.parentElement!, { pointerType: 'mouse' });
    expect(await screen.findByRole('region', { name: 'Bone Slicer appraisal' })).toBeInTheDocument();
    fireEvent.keyDown(button.parentElement!, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Bone Slicer appraisal' })).not.toBeInTheDocument());
    fireEvent.focus(button);
    expect(await screen.findByRole('region', { name: 'Bone Slicer appraisal' })).toBeInTheDocument();
  });
});
