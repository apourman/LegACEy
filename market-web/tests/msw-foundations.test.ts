import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { createMarketApiClient, getListing } from '../src/api';

const server = setupServer(http.get('http://market.test/api/listings/:id', ({ params }) => HttpResponse.json({
  id: Number(params.id), itemGuid: 10, wcid: 20, name: 'Bone Slicer', itemType: 'MeleeWeapon', material: 'Steel', workmanship: 7,
  level: 100, arcaneLore: 200, summary: '34-52', quantity: 3, price: 120, seller: 'Bravo', listedTime: '2026-10-01T00:00:00Z',
  wield: 'Level 100', icon: { layers: [], glow: null }, lines: ['Damage: 34 - 52'], spells: [],
})));

beforeAll(() => server.listen());
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

describe('MSW API seam', () => {
  it('mocks the listing network request at the browser client boundary', async () => {
    const detail = await getListing(1, createMarketApiClient({ baseUrl: 'http://market.test' }));
    expect(detail.name).toBe('Bone Slicer');
    expect(detail.price).toBe(120);
  });
});
