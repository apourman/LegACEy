import { describe, expect, it } from 'vitest';
import { settingsFromEnv } from '../src/bff/settings.server';

// The BFF's settings from the environment: what the local Docker gateway setting logs at startup, and what it refuses.

const env = {
  MARKET_API_URL: 'http://market-api:8080',
  MARKET_SERVICE_KEY: 'settings-test-service-key-0123456789abcdef0123',
  MARKET_COOKIE_SECRET: 'settings-test-cookie-secret-0123456789abcdef01',
};

function recorder() {
  const lines: { level: string; text: string }[] = [];
  return { lines, log: { info: (text: string) => lines.push({ level: 'info', text }), warn: (text: string) => lines.push({ level: 'warn', text }) } };
}

describe('MARKET_GATEWAY_IS_LOOPBACK', () => {
  it('names the gateway it will count as 127.0.0.1, once, and nothing secret', () => {
    const { lines, log } = recorder();
    const settings = settingsFromEnv({ ...env, MARKET_GATEWAY_IS_LOOPBACK: 'true' }, () => '172.18.0.1', log);
    expect(settings.loopbackPeers).toEqual(['172.18.0.1']);
    expect(lines).toEqual([{ level: 'info', text: expect.stringContaining('172.18.0.1') }]);
    for (const secret of [env.MARKET_SERVICE_KEY, env.MARKET_COOKIE_SECRET]) expect(lines[0].text).not.toContain(secret);
  });

  it('warns, and counts no address as loopback, when no gateway can be read', () => {
    const { lines, log } = recorder();
    const settings = settingsFromEnv({ ...env, MARKET_GATEWAY_IS_LOOPBACK: 'true' }, () => null, log);
    expect(settings.loopbackPeers).toEqual([]);
    expect(lines).toEqual([{ level: 'warn', text: expect.stringContaining('no default gateway') }]);
  });

  it('is silent when off', () => {
    const { lines, log } = recorder();
    expect(settingsFromEnv(env, () => '172.18.0.1', log).loopbackPeers).toEqual([]);
    expect(lines).toEqual([]);
  });
});

describe('required settings', () => {
  it('refuses to start without the API address and both secrets, naming each and never their values', () => {
    expect(() => settingsFromEnv({ MARKET_SERVICE_KEY: 'short' }, () => null, recorder().log)).toThrow(/MARKET_API_URL[\s\S]*MARKET_SERVICE_KEY[\s\S]*MARKET_COOKIE_SECRET/);
    expect(() => settingsFromEnv({ ...env, MARKET_TRUSTED_PROXY: 'not-an-ip' }, () => null, recorder().log)).toThrow(/MARKET_TRUSTED_PROXY/);
  });
});
