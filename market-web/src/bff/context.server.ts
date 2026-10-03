import type { BffSettings } from './settings.server';
import { settingsFromEnv } from './settings.server';

declare module 'react-router' {
  /** What every loader, action and the proxy get besides the request: the settings, and the address the connection came from */
  interface AppLoadContext {
    bff: BffSettings;
    /** The TCP peer's address, as the server saw it; null when it isn't known */
    connectionAddress: string | null;
  }
}

let settings: BffSettings | undefined;

/** The settings from the environment, read once. Throws, naming what's wrong, when they are incomplete; server.js calls it at startup. */
export function bffSettings(): BffSettings {
  settings ??= settingsFromEnv();
  return settings;
}

/** The load context for one request (server.js) */
export function loadContext(_request: Request, client: { address: string }) {
  return { bff: bffSettings(), connectionAddress: client.address || null };
}
