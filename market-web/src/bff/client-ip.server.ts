import { isIP } from 'node:net';
import type { AppLoadContext } from 'react-router';

/**
 * One plain IP address as the Market API accepts it in X-Market-Client-Ip, or null: dotted IPv4 (an IPv4-mapped IPv6 address becomes its IPv4
 * address), or IPv6 in its short form without a zone.
 */
export function normalizeAddress(text: string | null | undefined): string | null {
  if (!text) return null;
  let address = text.trim();

  const zone = address.indexOf('%');
  if (zone >= 0) address = address.slice(0, zone);

  const kind = isIP(address);
  if (kind === 4) return address;
  if (kind !== 6) return null;

  const mapped = /^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/i.exec(address);
  if (mapped && isIP(mapped[1]) === 4) return mapped[1];

  // the URL parser writes IPv6 in its short, lower-case form
  return new URL(`http://[${address}]/`).hostname.slice(1, -1);
}

/**
 * The player's address for the API's sign-in limits:
 * - with a configured trusted proxy and a connection from it, the right-most X-Forwarded-For entry, which that proxy appended;
 * - otherwise the connection's address, where the host's own loopback (locally, the Docker gateway) counts as 127.0.0.1.
 * An X-Forwarded-For from anyone else is ignored. Null when there is no usable address: the caller refuses the request rather than guess,
 * since guessing would put players together under one address.
 */
export function clientIp(request: Request, context: AppLoadContext): string | null {
  const connection = normalizeAddress(context.connectionAddress);
  if (!connection) return null;

  const settings = context.bff;
  if (settings.trustedProxy !== null && connection === settings.trustedProxy) {
    const forwarded = request.headers.get('X-Forwarded-For');
    if (forwarded === null) return connection;
    const entries = forwarded.split(',').map(entry => entry.trim()).filter(entry => entry.length > 0);
    return normalizeAddress(entries[entries.length - 1]);
  }

  return settings.loopbackPeers.includes(connection) ? '127.0.0.1' : connection;
}
