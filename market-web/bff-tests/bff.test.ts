import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import type { AppLoadContext } from 'react-router';
import type { BffSettings } from '../src/bff/settings.server';
import {
  cookieFrom, detail, facets, listing, loadBff, me, request, sendRaw, serviceKey, settings, site, startFakeApi,
  type Bff, type FakeApi, type RecordedRequest,
} from './harness';

// Criteria 1-7 of ticket 03 (.specs/marketplace-bff/tickets/03-the-bff.md), against the BFF's request handler and a fake Market API.

let api: FakeApi;
let bff: Bff;
const token = 'ws.c2Vzc2lvbi10b2tlbi1mb3ItdGhlLWJmZi10ZXN0cy0wMTIzNDU2';
const absoluteExpires = '2026-11-02T10:00:00.000Z';

beforeAll(async () => {
  api = await startFakeApi();
  bff = await loadBff();
});
afterAll(async () => {
  await bff?.close();
  await api?.close();
});
beforeEach(() => {
  api.reset();
  api.on('POST', '/api/auth/session', { json: { token, accountId: 1, accountName: 'Alpha', idleExpiresTime: '2026-10-17T10:00:00Z', absoluteExpiresTime: absoluteExpires } });
  api.on('DELETE', '/api/auth/session', { json: { ok: true } });
  api.on('GET', '/api/me', request => request.headers.authorization === `Bearer ${token}` ? { json: me } : { status: 401, json: { error: 'unauthorized' } });
  api.on('GET', '/api/facets', { json: facets });
  api.on('GET', '/api/listings', { json: { listings: [listing], nextCursor: 'next-page' } });
  api.on('GET', '/api/listings/1', { json: detail });
  api.on('GET', '/api/tickets', { json: [] });
});

function context(overrides: Partial<BffSettings> = {}, connectionAddress: string | null = '203.0.113.10'): AppLoadContext {
  return { bff: settings(api, overrides), connectionAddress };
}
const send = (req: Request, ctx = context()) => bff.handle(req, ctx);

/** Signs in through the BFF and returns the session cookie the browser keeps */
async function signedIn(ctx = context()): Promise<string> {
  const response = await send(request('/auth/sign-in', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ account: 'Alpha', password: 'pass' }) }), ctx);
  expect(response.status).toBe(200);
  const cookie = cookieFrom(response);
  api.requests.length = 0;
  return cookie;
}
const forwarded = (path: string): RecordedRequest[] => api.requests.filter(r => r.path === path);
/** A page's HTML as a reader sees the text: without the comments React puts between adjacent text parts */
const visible = (html: string) => html.replace(/<!-- -->/g, '');

// ---- 1. the allowlist

describe('1. the allowlist', () => {
  const unlisted: [string, string][] = [
    ['GET', '/api/tokens'], ['POST', '/api/tokens/1/revoke'], ['POST', '/api/auth/plugin-token'],
    ['POST', '/api/auth/session'], ['DELETE', '/api/auth/session'], ['POST', '/api/auth/login'],
    ['GET', '/api/openapi.json'], ['GET', '/api/openapi/v1.json'], ['GET', '/openapi/v1.json'], ['GET', '/api'], ['GET', '/api/'],
    ['GET', '/api/me/'], ['GET', '/api/no-such-route'],
    ['GET', '/api/listings/1/../../tokens'], ['GET', '/api/icons/..%2Ftokens'], ['GET', '/api/listings/%2e%2e'], ['GET', '/api/vault/1%2F..%2F..%2Ftokens'],
  ];
  it.each(unlisted)('an unlisted path, %s %s, gets 404 and never reaches the API', async (method, path) => {
    const response = await send(request(path, { method, headers: { 'X-Market-Request': '1', 'Content-Type': 'application/json' }, body: method === 'GET' ? undefined : '{}' }));
    if (path.startsWith('/api')) {
      expect(response.status).toBe(404);
      expect(await response.json()).toEqual({ error: 'not_found' });
    } else {
      expect(response.status).toBe(404);
    }
    expect(api.requests).toEqual([]);
  });

  const wrongMethod: [string, string][] = [
    ['DELETE', '/api/listings/1'], ['PUT', '/api/me'], ['POST', '/api/me'], ['PATCH', '/api/vault'], ['GET', '/api/listings/1/purchase'],
    ['GET', '/api/mmd/withdraw'], ['POST', '/api/tickets'], ['DELETE', '/api/tickets/1'], ['HEAD', '/api/me'], ['OPTIONS', '/api/listings'],
  ];
  it.each(wrongMethod)('an unlisted method, %s %s, gets 404 and never reaches the API', async (method, path) => {
    const response = await send(request(path, { method, headers: { 'X-Market-Request': '1', 'Content-Type': 'application/json' }, body: ['GET', 'HEAD'].includes(method) ? undefined : '{}' }));
    expect(response.status).toBe(404);
    expect(api.requests).toEqual([]);
  });

  // A Request built here normalises dot-segments before the BFF sees it; a socket doesn't. These go over a real connection, as written.
  const raw: [string, string][] = [
    ['GET', '/health/../api/tokens'], ['GET', '/api/listings/1/../../tokens'], ['GET', '/api/./tokens'], ['GET', '/api/me/../tokens'],
    ['GET', '/api/icons/../tokens'], ['POST', '/api/listings/1/../../tokens/1/revoke'], ['GET', '/api/icons/..%2F..%2Ftokens'],
    ['GET', '//api/tokens'], ['GET', '/api//tokens'], ['GET', '/api/../api/tokens'], ['GET', '/api/tokens?redirect=/api/me'],
  ];
  it.each(raw)('a raw, un-normalised path over a socket, %s %s, gets 404 and never reaches the API', async (method, rawPath) => {
    expect(await sendRaw(bff.handle, context(), method, rawPath)).toBe(404);
    expect(api.requests).toEqual([]);
  });

  it('the raw-socket path does reach the BFF: a listed route sent the same way is forwarded', async () => {
    expect(await sendRaw(bff.handle, context(), 'GET', '/api/facets')).toBe(200);
    expect(forwarded('/api/facets')).toHaveLength(1);
  });

  const listed: [string, string][] = [
    ['GET', '/api/me'], ['GET', '/api/facets'], ['GET', '/api/listings?q=Bone&sort=price'], ['GET', '/api/listings/suggest?q=Bo'], ['GET', '/api/listings/7'],
    ['GET', '/api/icons/glow.css'], ['GET', '/api/icons/0x06003237_p19.png'], ['GET', '/api/vault'], ['GET', '/api/vault/10'],
    ['POST', '/api/listings'], ['POST', '/api/listings/7/delist'], ['POST', '/api/listings/7/purchase'], ['POST', '/api/vault/withdraw'],
    ['POST', '/api/vault/deposit'], ['POST', '/api/mmd/withdraw'], ['POST', '/api/inventory/snapshot'], ['GET', '/api/tickets'], ['GET', '/api/tickets/9'],
    ['GET', '/api/history?transfersLimit=50'],
  ];
  it.each(listed)('a listed route, %s %s, is forwarded as it is', async (method, pathAndQuery) => {
    await send(request(pathAndQuery, { method, headers: { 'X-Market-Request': '1', 'Content-Type': 'application/json' }, body: method === 'GET' ? undefined : '{"a":1}' }));
    expect(api.requests).toHaveLength(1);
    const [sent] = api.requests;
    expect(sent.method).toBe(method);
    expect(sent.path + sent.search).toBe(pathAndQuery);
    if (method === 'POST') expect(sent.body).toBe('{"a":1}');
  });
});

// ---- 2. CSRF and origin

describe('2. cross-site requests', () => {
  const purchase = (headers: Record<string, string>) => new Request(site + '/api/listings/1/purchase', {
    method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify({ count: 3, expectedPrice: 120, idempotencyKey: 'k' }),
  });
  const signIn = (headers: Record<string, string>) => new Request(site + '/auth/sign-in', {
    method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify({ account: 'Alpha', password: 'pass' }),
  });
  const signOut = (headers: Record<string, string>) => new Request(site + '/auth/sign-out', { method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: '{}' });

  const refusals: [string, Record<string, string>][] = [
    ['a foreign Origin', { Origin: 'https://evil.example', 'Sec-Fetch-Site': 'cross-site' }],
    ['a foreign Origin claiming same-origin', { Origin: 'https://evil.example', 'Sec-Fetch-Site': 'same-origin' }],
    ['the site on another scheme', { Origin: 'https://market.test' }],
    ['Origin null', { Origin: 'null' }],
    ['Sec-Fetch-Site: cross-site and no Origin', { 'Sec-Fetch-Site': 'cross-site' }],
    ['Sec-Fetch-Site: same-site and no Origin', { 'Sec-Fetch-Site': 'same-site' }],
    ['neither Origin nor Sec-Fetch-Site', {}],
  ];

  for (const [handler, handle] of [['the server handler', () => bff.handle], ['the routes alone', () => bff.handleRoutesOnly]] as const) {
    describe(`through ${handler}`, () => {
      it('the proxy refuses a change without X-Market-Request, even from the site itself', async () => {
        const response = await handle()(purchase({ Origin: site }), context());
        expect(response.status).toBe(403);
        expect(await response.json()).toEqual({ error: 'csrf' });
        const wrongValue = await handle()(purchase({ Origin: site, 'X-Market-Request': 'yes' }), context());
        expect(wrongValue.status).toBe(403);
        expect(api.requests).toEqual([]);
      });

      it.each(refusals)('the proxy refuses %s, and forwards nothing', async (_label, headers) => {
        const response = await handle()(purchase({ 'X-Market-Request': '1', ...headers }), context());
        expect(response.status).toBe(403);
        expect(await response.json()).toEqual({ error: 'csrf' });
        expect(api.requests).toEqual([]);
      });

      it.each(refusals)('sign-in refuses %s, and calls nothing', async (_label, headers) => {
        const response = await handle()(signIn(headers), context());
        expect(response.status).toBe(403);
        expect(await response.json()).toEqual({ error: 'csrf' });
        expect(response.headers.getSetCookie()).toEqual([]);
        expect(api.requests).toEqual([]);
      });

      it.each(refusals)('sign-out refuses %s, and the session goes on', async (_label, headers) => {
        const cookie = await signedIn();
        const response = await handle()(signOut({ ...headers, Cookie: cookie }), context());
        expect(response.status).toBe(403);
        expect(response.headers.getSetCookie()).toEqual([]);
        expect(api.requests).toEqual([]);
      });

      it('same-origin passes: by Origin, or by Sec-Fetch-Site without Origin', async () => {
        api.on('POST', '/api/listings/1/purchase', { json: { status: 'ok', listingId: 1, itemGuid: 10, price: 120, fee: 0, balance: 380 } });
        expect((await handle()(purchase({ 'X-Market-Request': '1', Origin: site }), context())).status).toBe(200);
        expect((await handle()(purchase({ 'X-Market-Request': '1', 'Sec-Fetch-Site': 'same-origin' }), context())).status).toBe(200);
        expect(forwarded('/api/listings/1/purchase')).toHaveLength(2);

        expect((await handle()(signIn({ Origin: site }), context())).status).toBe(200);
        expect((await handle()(signIn({ 'Sec-Fetch-Site': 'same-origin' }), context())).status).toBe(200);
        expect(forwarded('/api/auth/session')).toHaveLength(2);
      });
    });
  }

  it('a configured site origin is the one that counts', async () => {
    const ctx = context({ siteOrigin: 'https://market.example' });
    expect((await send(purchase({ 'X-Market-Request': '1', Origin: site }), ctx)).status).toBe(403);
    api.on('POST', '/api/listings/1/purchase', { json: { status: 'ok' } });
    expect((await send(purchase({ 'X-Market-Request': '1', Origin: 'https://market.example' }), ctx)).status).toBe(200);
  });

  it('every other non-GET request to the BFF is refused cross-site too, even where there is no action', async () => {
    for (const path of ['/', '/vault', '/listing/1', '/signin', '/health', '/no-such-page']) {
      const response = await send(new Request(site + path, { method: 'POST', headers: { Origin: 'https://evil.example' }, body: 'x' }));
      expect(response.status, path).toBe(403);
      expect(await response.json()).toEqual({ error: 'csrf' });
    }
    expect(api.requests).toEqual([]);
  });

  it('reads need neither header, and no answer grants CORS', async () => {
    const response = await send(new Request(site + '/api/facets', { headers: { Origin: 'https://evil.example' } }));
    expect(response.status).toBe(200);
    expect(response.headers.get('Access-Control-Allow-Origin')).toBeNull();
    const preflight = await send(new Request(site + '/api/listings', { method: 'OPTIONS', headers: { Origin: 'https://evil.example', 'Access-Control-Request-Method': 'POST' } }));
    expect(preflight.headers.get('Access-Control-Allow-Origin')).toBeNull();
    expect(preflight.headers.get('Access-Control-Allow-Headers')).toBeNull();
  });
});

// ---- 3. forwarding: the service key, the bearer token and the client IP

describe('3. forwarding', () => {
  it('a signed-in request carries the service key, the bearer token and the client IP, and nothing the browser sent of its own', async () => {
    const cookie = await signedIn();
    await send(request('/api/vault', {
      headers: {
        Cookie: `${cookie}; other=1`, Authorization: 'Bearer browser-chosen', 'X-Market-Service-Key': 'browser-chosen', 'X-Market-Client-Ip': '198.51.100.1',
        'X-Forwarded-For': '198.51.100.2', 'X-Market-Request': '1', Accept: 'application/json',
      },
    }));
    const [sent] = forwarded('/api/vault');
    expect(sent.headers['x-market-service-key']).toBe(serviceKey);
    expect(sent.headers.authorization).toBe(`Bearer ${token}`);
    expect(sent.headers['x-market-client-ip']).toBe('203.0.113.10');
    expect(sent.headers.cookie).toBeUndefined();
    expect(sent.headers['x-forwarded-for']).toBeUndefined();
    expect(sent.headers['x-market-request']).toBeUndefined();
    expect(sent.headers.accept).toBe('application/json');
  });

  it('a signed-out request carries the key and the client IP but no token; icons and the stylesheet never carry the token', async () => {
    await send(request('/api/listings'));
    expect(forwarded('/api/listings')[0].headers.authorization).toBeUndefined();
    expect(forwarded('/api/listings')[0].headers['x-market-service-key']).toBe(serviceKey);

    const cookie = await signedIn();
    api.on('GET', '/api/icons/glow.css', { body: '.glow{}', headers: { 'Content-Type': 'text/css' } });
    await send(request('/api/icons/glow.css', { headers: { Cookie: cookie } }));
    await send(request('/api/icons/0x06003237.png', { headers: { Cookie: cookie } }));
    for (const sent of api.requests) {
      expect(sent.headers.authorization).toBeUndefined();
      expect(sent.headers['x-market-service-key']).toBe(serviceKey);
      expect(sent.headers['x-market-client-ip']).toBe('203.0.113.10');
    }
  });

  it('the server-rendered pages call the API the same way', async () => {
    const cookie = await signedIn();
    await send(request('/listing/1', { headers: { Cookie: cookie } }));
    for (const path of ['/api/me', '/api/listings/1']) {
      const [sent] = forwarded(path);
      expect(sent.headers['x-market-service-key'], path).toBe(serviceKey);
      expect(sent.headers.authorization, path).toBe(`Bearer ${token}`);
      expect(sent.headers['x-market-client-ip'], path).toBe('203.0.113.10');
    }
  });

  it('sign-in sends the account, the password and the client IP, and no old token', async () => {
    const cookie = await signedIn();
    await send(request('/auth/sign-in', { method: 'POST', headers: { Cookie: cookie, 'Content-Type': 'application/json' }, body: JSON.stringify({ account: 'Alpha', password: 'pass' }) }), context({}, '198.51.100.77'));
    const [sent] = forwarded('/api/auth/session');
    expect(JSON.parse(sent.body)).toEqual({ account: 'Alpha', password: 'pass' });
    expect(sent.headers['x-market-client-ip']).toBe('198.51.100.77');
    expect(sent.headers['x-market-service-key']).toBe(serviceKey);
    expect(sent.headers.authorization).toBeUndefined();
  });

  const clientIp = async (ctx: AppLoadContext, headers: Record<string, string> = {}) => {
    api.requests.length = 0;
    const response = await send(request('/api/facets', { headers }), ctx);
    return { status: response.status, ip: api.requests[0]?.headers['x-market-client-ip'] };
  };

  it('without a trusted proxy, the client IP is the connection address, and X-Forwarded-For is ignored', async () => {
    expect(await clientIp(context({}, '203.0.113.10'), { 'X-Forwarded-For': '198.51.100.9' })).toEqual({ status: 200, ip: '203.0.113.10' });
    expect(await clientIp(context({}, '::ffff:203.0.113.11'))).toEqual({ status: 200, ip: '203.0.113.11' });
    expect(await clientIp(context({}, '2001:0db8:0000::0001'))).toEqual({ status: 200, ip: '2001:db8::1' });
    expect(await clientIp(context({}, 'fe80::1%eth0'))).toEqual({ status: 200, ip: 'fe80::1' });
  });

  it('with a trusted proxy, a connection from it gives the right-most X-Forwarded-For address', async () => {
    const proxied = (address: string) => context({ trustedProxy: '10.0.0.5' }, address);
    expect(await clientIp(proxied('10.0.0.5'), { 'X-Forwarded-For': '198.51.100.1, 203.0.113.20' })).toEqual({ status: 200, ip: '203.0.113.20' });
    expect(await clientIp(proxied('::ffff:10.0.0.5'), { 'X-Forwarded-For': '203.0.113.21' })).toEqual({ status: 200, ip: '203.0.113.21' });
    // a player can only add entries to the left of the one the proxy appended
    expect(await clientIp(proxied('10.0.0.5'), { 'X-Forwarded-For': '127.0.0.1, 10.0.0.5, 203.0.113.22' })).toEqual({ status: 200, ip: '203.0.113.22' });
    // without one, the proxy's own connection
    expect(await clientIp(proxied('10.0.0.5'))).toEqual({ status: 200, ip: '10.0.0.5' });
    // never anything but one plain address: the API would answer 400
    expect(await clientIp(proxied('10.0.0.5'), { 'X-Forwarded-For': '203.0.113.23:4431' })).toEqual({ status: 400, ip: undefined });
  });

  it('with a trusted proxy, X-Forwarded-For from any other connection is ignored (forged)', async () => {
    const ctx = context({ trustedProxy: '10.0.0.5' }, '198.51.100.66');
    expect(await clientIp(ctx, { 'X-Forwarded-For': '203.0.113.99' })).toEqual({ status: 200, ip: '198.51.100.66' });
  });

  it('locally, a connection from the Docker gateway counts as loopback', async () => {
    const local = (address: string) => context({ loopbackPeers: ['172.17.0.1'] }, address);
    expect(await clientIp(local('172.17.0.1'))).toEqual({ status: 200, ip: '127.0.0.1' });
    expect(await clientIp(local('::ffff:172.17.0.1'), { 'X-Forwarded-For': '203.0.113.1' })).toEqual({ status: 200, ip: '127.0.0.1' });
    expect(await clientIp(local('172.17.0.9'))).toEqual({ status: 200, ip: '172.17.0.9' });
  });

  it('a request whose address is unknown is refused, not guessed', async () => {
    expect(await clientIp(context({}, null))).toEqual({ status: 400, ip: undefined });
    expect(await clientIp(context({}, 'not-an-address'))).toEqual({ status: 400, ip: undefined });
  });

  it('an API that cannot be reached answers 502 network: the change may or may not have happened', async () => {
    api.on('POST', '/api/listings/1/purchase', () => 'abort');
    const response = await send(request('/api/listings/1/purchase', { method: 'POST', headers: { 'X-Market-Request': '1', 'Content-Type': 'application/json' }, body: '{}' }));
    expect(response.status).toBe(502);
    expect(await response.json()).toEqual({ error: 'network' });
  });
});

// ---- 4. the token stays in the cookie; hop headers are stripped

describe('4. the token leaves the BFF only inside the session cookie', () => {
  const leaks = (text: string) => text.includes(token) || text.includes(token.slice(3)) || text.includes(Buffer.from(token).toString('base64'));
  async function expectNoToken(response: Response, where: string) {
    for (const [name, value] of response.headers) {
      if (name === 'set-cookie') continue;
      expect(leaks(value), `${where}: header ${name}`).toBe(false);
    }
    for (const cookie of response.headers.getSetCookie()) {
      if (!cookie.startsWith('market_session=')) expect(leaks(cookie), `${where}: another cookie`).toBe(false);
      else expect(cookie, `${where}: the session cookie is HttpOnly`).toMatch(/; HttpOnly/i);
    }
    expect(leaks(await response.text()), `${where}: body`).toBe(false);
  }

  it('sign-in answers with the account, and the token only inside the signed HttpOnly cookie', async () => {
    const response = await send(request('/auth/sign-in', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ account: 'Alpha', password: 'pass' }) }));
    expect(response.status).toBe(200);
    const cookie = response.headers.getSetCookie().find(c => c.startsWith('market_session='))!;
    // the cookie holds the token, signed: the raw token isn't the cookie's value
    expect(cookie.split(';')[0]).not.toBe(`market_session=${token}`);
    const body = await response.clone().json();
    expect(body).toEqual({ accountId: 1, accountName: 'Alpha' });
    await expectNoToken(response, 'sign-in');
  });

  it('no proxied answer, error or page carries the token', async () => {
    const cookie = await signedIn();
    api.on('GET', '/api/vault', request => ({ json: { items: [], echo: request.headers.authorization } }));
    api.on('GET', '/api/history', { status: 500, json: { error: 'server' } });

    for (const path of ['/api/me', '/api/history', '/', '/listing/1', '/vault', '/history', '/signin', '/no-such-page']) {
      await expectNoToken(await send(request(path, { headers: { Cookie: cookie } })), path);
    }
    await expectNoToken(await send(request('/auth/sign-out', { method: 'POST', headers: { Cookie: cookie, 'Content-Type': 'application/json' }, body: '{}' })), 'sign-out');
  });

  it('a header that echoes what the API was sent is not passed back', async () => {
    const cookie = await signedIn();
    api.on('GET', '/api/vault', request => ({ json: { items: [] }, headers: { 'X-Echo-Authorization': String(request.headers.authorization) } }));
    const response = await send(request('/api/vault', { headers: { Cookie: cookie } }));
    expect(response.headers.get('X-Echo-Authorization')).toBeNull();
  });

  it('hop-by-hop, auth and server headers are stripped; content and cache headers pass', async () => {
    api.on('GET', '/api/icons/0x06003237.png', {
      body: 'png-bytes',
      headers: {
        'Content-Type': 'image/png', 'Cache-Control': 'public, max-age=86400', ETag: '"icon-1"', 'Last-Modified': 'Wed, 01 Oct 2026 12:00:00 GMT',
        Connection: 'keep-alive', 'Keep-Alive': 'timeout=5', 'Proxy-Authenticate': 'Basic', Trailer: 'X-Checksum', Upgrade: 'h2c',
        'Set-Cookie': 'api=1; Path=/', 'WWW-Authenticate': 'Bearer', Server: 'Kestrel', 'X-Market-Service-Key': 'echo', Authorization: 'Bearer echo',
      },
    });
    const response = await send(request('/api/icons/0x06003237.png'));
    expect(response.status).toBe(200);
    expect(await response.text()).toBe('png-bytes');
    expect(response.headers.get('Content-Type')).toBe('image/png');
    expect(response.headers.get('Cache-Control')).toBe('public, max-age=86400');
    expect(response.headers.get('ETag')).toBe('"icon-1"');
    expect(response.headers.get('Last-Modified')).toBe('Wed, 01 Oct 2026 12:00:00 GMT');
    for (const name of ['Connection', 'Keep-Alive', 'Proxy-Authenticate', 'Trailer', 'Upgrade', 'Transfer-Encoding', 'Set-Cookie', 'WWW-Authenticate', 'Server', 'X-Market-Service-Key', 'Authorization'])
      expect(response.headers.get(name), name).toBeNull();
  });

  it('a revalidated icon keeps its cache answer (304, no body)', async () => {
    api.on('GET', '/api/icons/0x06003237.png', request => request.headers['if-none-match'] === '"icon-1"' ? { status: 304, headers: { ETag: '"icon-1"', 'Cache-Control': 'public, max-age=86400' } } : { body: 'png' });
    const response = await send(request('/api/icons/0x06003237.png', { headers: { 'If-None-Match': '"icon-1"' } }));
    expect(response.status).toBe(304);
    expect(response.headers.get('ETag')).toBe('"icon-1"');
    expect(await response.text()).toBe('');
  });

  it('the API\'s status and error body pass through unchanged', async () => {
    api.on('POST', '/api/listings/1/purchase', { status: 409, json: { error: 'price_changed', price: 150 } });
    const response = await send(request('/api/listings/1/purchase', { method: 'POST', headers: { 'X-Market-Request': '1', 'Content-Type': 'application/json' }, body: '{}' }));
    expect(response.status).toBe(409);
    expect(await response.json()).toEqual({ error: 'price_changed', price: 150 });

    api.on('GET', '/api/listings/1', { status: 200, body: '{"status":', headers: { 'Content-Type': 'application/json' } });
    expect(await (await send(request('/api/listings/1'))).text()).toBe('{"status":');
  });
});

// ---- 5. the session cookie

describe('5. the session cookie', () => {
  it('sign-in sets it signed, HttpOnly, Secure, SameSite=Lax, for the whole site, until the session\'s absolute expiry', async () => {
    const response = await send(request('/auth/sign-in', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ account: 'Alpha', password: 'pass' }) }));
    const [cookie] = response.headers.getSetCookie();
    const attributes = cookie.split(';').map(part => part.trim().toLowerCase());
    expect(cookie).toMatch(/^market_session=[^;]+/);
    expect(attributes).toContain('httponly');
    expect(attributes).toContain('secure');
    expect(attributes).toContain('samesite=lax');
    expect(attributes).toContain('path=/');
    expect(cookie).toContain(`Expires=${new Date(absoluteExpires).toUTCString()}`);
    expect(attributes.some(a => a.startsWith('domain='))).toBe(false);
  });

  it('a cookie that isn\'t signed with the secret signs in nowhere', async () => {
    const cookie = await signedIn();
    const [name, value] = cookie.split('=');
    const forged = `${name}=${Buffer.from(JSON.stringify({ token: 'ws.forged' })).toString('base64')}.${value.split('.').pop()}`;
    await send(request('/api/vault', { headers: { Cookie: forged } }));
    expect(forwarded('/api/vault')[0].headers.authorization).toBeUndefined();

    const otherSecret = context({ cookieSecret: 'another-secret-0123456789abcdef0123456789abcdef' });
    await send(request('/api/me', { headers: { Cookie: cookie } }), otherSecret);
    expect(forwarded('/api/me')[0].headers.authorization).toBeUndefined();
  });

  it('sign-out ends the session at the API, then clears the cookie', async () => {
    const cookie = await signedIn();
    const response = await send(request('/auth/sign-out', { method: 'POST', headers: { Cookie: cookie, 'Content-Type': 'application/json' }, body: '{}' }));
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ ok: true });
    const [sent] = forwarded('/api/auth/session');
    expect(sent.method).toBe('DELETE');
    expect(sent.headers.authorization).toBe(`Bearer ${token}`);
    expectCleared(response);
  });

  it('sign-out of a session the API already ended clears the cookie; one the API failed to end keeps it, so it can be tried again', async () => {
    const cookie = await signedIn();
    api.on('DELETE', '/api/auth/session', { status: 401, json: { error: 'unauthorized' } });
    expectCleared(await send(request('/auth/sign-out', { method: 'POST', headers: { Cookie: cookie, 'Content-Type': 'application/json' }, body: '{}' })));

    api.on('DELETE', '/api/auth/session', { status: 503, json: { error: 'server' } });
    const failed = await send(request('/auth/sign-out', { method: 'POST', headers: { Cookie: cookie, 'Content-Type': 'application/json' }, body: '{}' }));
    expect(failed.status).toBe(503);
    expect(failed.headers.getSetCookie()).toEqual([]);
  });

  it('any API 401 clears it: through the proxy, and on a server-rendered page', async () => {
    const cookie = await signedIn();
    api.on('GET', '/api/vault', { status: 401, json: { error: 'unauthorized' } });
    const proxied = await send(request('/api/vault', { headers: { Cookie: cookie } }));
    expect(proxied.status).toBe(401);
    expect(await proxied.json()).toEqual({ error: 'unauthorized' });
    expectCleared(proxied);

    api.on('GET', '/api/me', { status: 401, json: { error: 'unauthorized' } });
    const page = await send(request('/', { headers: { Cookie: cookie } }));
    expect(page.status).toBe(200);
    expectCleared(page);
    const html = visible(await page.text());
    expect(html).toContain('>Sign in</a>');
    expect(html).not.toContain('Alpha Main');

    // a page's .data request (client-side navigation) clears it the same way
    api.on('GET', '/api/listings/1', { status: 401, json: { error: 'unauthorized' } });
    expectCleared(await send(request('/listing/1.data', { headers: { Cookie: cookie } })));
  });

  it('a 401 to a signed-out request (a wrong password) sets no cookie at all', async () => {
    api.on('POST', '/api/auth/session', { status: 401, json: { error: 'invalid_credentials' } });
    const response = await send(request('/auth/sign-in', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ account: 'Alpha', password: 'wrong' }) }));
    expect(response.status).toBe(401);
    expect(await response.json()).toEqual({ error: 'invalid_credentials' });
    expect(response.headers.getSetCookie()).toEqual([]);
  });

  it.each([['account_locked', 429], ['ip_blocked', 429], ['banned', 403], ['bad_request', 400]] as const)('sign-in passes the API\'s %s refusal through', async (code, status) => {
    api.on('POST', '/api/auth/session', { status, json: { error: code } });
    const response = await send(request('/auth/sign-in', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ account: 'Alpha', password: 'x' }) }));
    expect(response.status).toBe(status);
    expect(await response.json()).toEqual({ error: code });
    expect(response.headers.getSetCookie()).toEqual([]);
  });

  it('a sign-in form sent before the script ran signs in and goes home, or comes back with the refusal', async () => {
    const form = (password: string) => request('/auth/sign-in', { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams({ account: 'Alpha', password }).toString() });
    const ok = await send(form('pass'));
    expect(ok.status).toBe(302);
    expect(ok.headers.get('Location')).toBe('/');
    cookieFrom(ok);
    expect(JSON.parse(forwarded('/api/auth/session')[0].body)).toEqual({ account: 'Alpha', password: 'pass' });

    api.on('POST', '/api/auth/session', { status: 401, json: { error: 'invalid_credentials' } });
    const refused = await send(form('wrong'));
    expect(refused.headers.get('Location')).toBe('/signin?error=invalid_credentials');
    expect(refused.headers.getSetCookie()).toEqual([]);
  });

  function expectCleared(response: Response) {
    const cookie = response.headers.getSetCookie().find(c => c.startsWith('market_session='));
    expect(cookie, 'the session cookie is cleared').toBeDefined();
    expect(cookie!).toMatch(/^market_session=;/);
    expect(cookie!).toMatch(/Max-Age=0|Expires=Thu, 01 Jan 1970/);
  }
});

// ---- 6. Browse and Listing render on the server

describe('6. server-rendered pages', () => {
  it('Browse arrives with the listings and facets for the address, and the cursor stays the page\'s', async () => {
    const response = await send(request('/?q=Bone&sort=price&dir=asc&cursor=stale'));
    expect(response.status).toBe(200);
    expect(response.headers.get('Content-Type')).toContain('text/html');
    const html = visible(await response.text());
    expect(html).toContain('Bone Slicer');
    expect(html).toContain('href="/listing/1"');
    expect(html).toContain('34-52 (Slashing)');
    expect(html).toContain('Melee Weapon (1)');
    expect(html).toContain('Load more');
    expect(html).toContain('value="Bone"');
    const [sent] = forwarded('/api/listings');
    expect(new URLSearchParams(sent.search)).toEqual(new URLSearchParams({ q: 'Bone', sort: 'price', dir: 'asc' }));
  });

  it('Browse with nothing to show says so', async () => {
    api.on('GET', '/api/listings', { json: { listings: [], nextCursor: null } });
    const html = visible(await (await send(request('/?q=Nothing'))).text());
    expect(html).toContain('No listings match');
  });

  it('Browse shows the API\'s refusal of a bad filter', async () => {
    api.on('GET', '/api/listings', { status: 400, json: { error: 'bad_price' } });
    const html = visible(await (await send(request('/?minPrice=1.5'))).text());
    expect(html).toContain('Enter whole MMD prices.');
  });

  it('Listing arrives with the item, price and appraisal', async () => {
    const response = await send(request('/listing/1'));
    expect(response.status).toBe(200);
    const html = visible(await response.text());
    expect(html).toContain('<h1>Bone Slicer</h1>');
    expect(html).toContain('120 <small>MMD</small>');
    expect(html).toContain('Damage: 34 - 52');
    expect(html).toContain('Blood Drinker');
    expect(html).toContain('Bravo Main');
  });

  it.each([[404, 'not_found'], [410, 'gone']] as const)('a listing the API answers %s for renders "No longer available"', async (status, code) => {
    api.on('GET', '/api/listings/5', { status, json: { error: code } });
    const response = await send(request('/listing/5'));
    expect(response.status).toBe(status);
    const html = visible(await response.text());
    expect(html).toContain('No longer available');
    expect(html).toContain('This listing was sold, removed or expired.');
  });

  it('an address that can\'t name a listing is no longer available, without asking the API', async () => {
    const response = await send(request('/listing/abc'));
    expect(response.status).toBe(404);
    expect(await response.text()).toContain('No longer available');
    expect(forwarded('/api/listings/abc')).toEqual([]);
  });

  it('the BFF answers its own health check without the API', async () => {
    const response = await send(request('/health'));
    expect(response.status).toBe(200);
    expect(await response.text()).toBe('ok');
    expect(api.requests).toEqual([]);
  });
});

// ---- 7. the acting character and the header on the first render

describe('7. the header on the first render', () => {
  const selected = (html: string) => /<option value="(\d+)" selected="">/.exec(html)?.[1];

  it('signed in, the header shows the account, the balance and the chosen character', async () => {
    const cookie = await signedIn();
    const html = visible(await (await send(request('/', { headers: { Cookie: `${cookie}; market-character-1=12` } }))).text());
    expect(html).toContain('<span>Alpha</span>');
    expect(html).toContain('500 MMD');
    expect(html).toContain('Sign out');
    expect(selected(html)).toBe('12');
  });

  it('a character cookie naming a character not on the account falls back to the first', async () => {
    const cookie = await signedIn();
    for (const value of ['99', '12abc', '']) {
      const html = visible(await (await send(request('/listing/1', { headers: { Cookie: `${cookie}; market-character-1=${value}` } }))).text());
      expect(selected(html), value).toBe('11');
    }
    // another account's choice doesn't count
    const html = visible(await (await send(request('/', { headers: { Cookie: `${cookie}; market-character-2=12` } }))).text());
    expect(selected(html)).toBe('11');
  });

  it('the paused banner is there on the first render', async () => {
    api.on('GET', '/api/me', { json: { ...me, paused: true } });
    const cookie = await signedIn();
    const html = visible(await (await send(request('/', { headers: { Cookie: cookie } }))).text());
    expect(html).toContain('The market is paused. Purchases and MMD withdrawals are temporarily unavailable.');
  });

  const ended = 'Your session has ended. Please sign in again.';

  it.each(['/', '/listing/1', '/vault'])('a session the API has ended (401) shows "%s" with the session-ended message on the first render, and clears the cookie', async path => {
    const cookie = await signedIn();
    api.on('GET', '/api/me', { status: 401, json: { error: 'unauthorized' } });
    const response = await send(request(path, { headers: { Cookie: cookie } }));
    const cleared = response.headers.getSetCookie().find(c => c.startsWith('market_session='));
    expect(cleared).toMatch(/^market_session=;/);
    const html = visible(await response.text());
    expect(html).toContain(`<p role="status" class="notice">${ended}</p>`);
    expect(html).toContain('>Sign in</a>');
  });

  it('a 401 from a page\'s own loader (Browse, Listing) during a signed-in render shows the message too', async () => {
    const cookie = await signedIn();
    api.on('GET', '/api/listings/1', { status: 401, json: { error: 'unauthorized' } });
    const response = await send(request('/listing/1', { headers: { Cookie: cookie } }));
    expect(response.headers.getSetCookie().some(c => c.startsWith('market_session=;'))).toBe(true);
    const html = visible(await response.text());
    expect(html).toContain(`<p role="status" class="notice">${ended}</p>`);
    // the whole page agrees the session is over: the header is signed out, though the account's own call still answered
    expect(html).toContain('>Sign in</a>');
    expect(html).not.toContain('<span>Alpha</span>');
  });

  it('a visitor who had no session cookie, or a live session, never sees the session-ended message', async () => {
    // the API would refuse this visitor's account: but a visitor with no session never asks for one
    api.on('GET', '/api/me', { status: 401, json: { error: 'unauthorized' } });
    for (const path of ['/', '/listing/1', '/signin']) {
      const response = await send(request(path));
      expect(visible(await response.text()), path).not.toContain(ended);
      expect(response.headers.getSetCookie(), path).toEqual([]);
    }
    api.on('GET', '/api/me', { json: me });
    const cookie = await signedIn();
    expect(visible(await (await send(request('/', { headers: { Cookie: cookie } }))).text())).not.toContain(ended);
    // a cookie that isn't ours (wrong signature) is no session: nothing ended
    expect(visible(await (await send(request('/', { headers: { Cookie: 'market_session=forged.value' } }))).text())).not.toContain(ended);
  });

  it('signed out, the header offers sign-in and asks the API for no account', async () => {
    const html = visible(await (await send(request('/'))).text());
    expect(html).toContain('>Sign in</a>');
    expect(forwarded('/api/me')).toEqual([]);
  });
});
