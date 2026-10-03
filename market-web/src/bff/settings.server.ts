import { readFileSync } from 'node:fs';
import { normalizeAddress } from './client-ip.server';

/**
 * The BFF's settings. They come from the environment only (settingsFromEnv); the request tests build them by hand.
 */
export interface BffSettings {
  /** The private Market API's base URL, e.g. http://market-api:8080. Never public. */
  apiUrl: string;
  /** Sent as X-Market-Service-Key on every API request */
  serviceKey: string;
  /** Signs the session cookie */
  cookieSecret: string;
  /** The one reverse proxy whose X-Forwarded-For is believed, as a plain address; null when there is none (locally) */
  trustedProxy: string | null;
  /** The site's own origin for the cross-site check (https://market.example); null: the origin the request was addressed to */
  siteOrigin: string | null;
  /**
   * Connection addresses that are the host's own loopback. Locally the BFF runs in Docker with its port published on the host's 127.0.0.1
   * only, so every connection reaches it from the container's default gateway: that address counts as 127.0.0.1.
   */
  loopbackPeers: string[];
}

/** The shortest service key the API starts with (ServiceGate.MinimumKeyLength in Source/ACE.MarketApi/ServiceGate.cs), and the shortest cookie secret */
export const minimumSecretLength = 32;

/**
 * The settings from the environment, or an Error naming every problem:
 * - MARKET_API_URL: the private API's base URL;
 * - MARKET_SERVICE_KEY: the API's service key, at least 32 characters;
 * - MARKET_COOKIE_SECRET: signs the session cookie, at least 32 characters;
 * - MARKET_TRUSTED_PROXY (optional): the reverse proxy's address, whose right-most X-Forwarded-For entry is the player's;
 * - MARKET_SITE_ORIGIN (optional): the public origin, when a proxy in front changes the scheme or host;
 * - MARKET_GATEWAY_IS_LOOPBACK (optional, local Docker only): "true" counts connections from the container's default gateway as 127.0.0.1 (logged at startup; a warning when no gateway can be read).
 */
export function settingsFromEnv(
  env: Record<string, string | undefined> = process.env,
  defaultGateway: () => string | null = readDefaultGateway,
  log: Pick<Console, 'info' | 'warn'> = console,
): BffSettings {
  const problems: string[] = [];
  const text = (name: string) => (env[name] ?? '').trim();

  const apiUrl = text('MARKET_API_URL');
  if (!/^https?:\/\/[^/]+$/.test(apiUrl.replace(/\/$/, ''))) problems.push('MARKET_API_URL must be the Market API\'s base URL, such as http://market-api:8080.');

  const serviceKey = text('MARKET_SERVICE_KEY');
  if (serviceKey.length < minimumSecretLength) problems.push(`MARKET_SERVICE_KEY is missing or shorter than ${minimumSecretLength} characters (set it in docker.env).`);

  const cookieSecret = text('MARKET_COOKIE_SECRET');
  if (cookieSecret.length < minimumSecretLength) problems.push(`MARKET_COOKIE_SECRET is missing or shorter than ${minimumSecretLength} characters (set it in docker.env: openssl rand -hex 32).`);

  let trustedProxy: string | null = null;
  if (text('MARKET_TRUSTED_PROXY')) {
    trustedProxy = normalizeAddress(text('MARKET_TRUSTED_PROXY'));
    if (!trustedProxy) problems.push('MARKET_TRUSTED_PROXY must be one IP address.');
  }

  let siteOrigin: string | null = null;
  if (text('MARKET_SITE_ORIGIN')) {
    try { siteOrigin = new URL(text('MARKET_SITE_ORIGIN')).origin; }
    catch { problems.push('MARKET_SITE_ORIGIN must be an origin, such as https://market.example.'); }
  }

  // said once, at startup: which address will count as loopback, or that none will
  const loopbackPeers: string[] = [];
  if (text('MARKET_GATEWAY_IS_LOOPBACK') === 'true') {
    const gateway = normalizeAddress(defaultGateway());
    if (gateway) {
      loopbackPeers.push(gateway);
      log.info(`MARKET_GATEWAY_IS_LOOPBACK: connections from the default gateway ${gateway} count as 127.0.0.1.`);
    } else {
      log.warn('MARKET_GATEWAY_IS_LOOPBACK is true, but no default gateway could be read (/proc/net/route): every connection counts as its own address.');
    }
  }

  if (problems.length > 0) throw new Error('The BFF is not configured:\n  ' + problems.join('\n  '));

  return { apiUrl: apiUrl.replace(/\/$/, ''), serviceKey, cookieSecret, trustedProxy, siteOrigin, loopbackPeers };
}

/** The IPv4 default gateway from /proc/net/route (Linux), or null */
export function readDefaultGateway(): string | null {
  let table: string;
  try { table = readFileSync('/proc/net/route', 'utf8'); } catch { return null; }

  for (const line of table.split('\n').slice(1)) {
    const [, destination, gateway] = line.trim().split(/\s+/);
    if (destination !== '00000000' || !gateway || !/^[0-9A-Fa-f]{8}$/.test(gateway)) continue;
    // little-endian hex: 010011AC is 172.17.0.1
    const bytes = gateway.match(/../g)!.map(pair => parseInt(pair, 16)).reverse();
    return bytes.join('.');
  }
  return null;
}
