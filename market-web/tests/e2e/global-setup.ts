import { execFileSync } from 'node:child_process';

export default function globalSetup() {
  const root = process.env.MARKET_DEV_ROOT ?? process.cwd().replace(/\/market-web$/, '');
  const config = process.env.MARKET_DEV_CONFIG ?? '/home/jacob/.local/state/legacey/market-e2e/Config.js';
  for (const character of ['journeys desktop Alpha', 'journeys phone Alpha']) {
    execFileSync(`${root}/scripts/market/dev.sh`, ['fixture', '--character', character], {
      cwd: root,
      env: { ...process.env, MARKET_DEV_ROOT: root, MARKET_DEV_CONFIG: config },
      stdio: 'inherit',
    });
  }
}
