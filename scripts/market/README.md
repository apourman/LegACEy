# Local market stack

Runs the whole market on one machine, as separate processes:

- MySQL in Docker (`ace-db`, `127.0.0.1:3310`, from `docker/docker-compose.local.yml`; never the `ace-db` on 3306);
- the game server, natively, on the market stack's own databases;
- the Market API in its own Docker container (`market-api`, `127.0.0.1:5080`, under `/api`).

Everything listens on loopback only. You need Docker, the .NET 10 SDK, `jq`, `curl`, the client DATs in
`~/ace_dats/retail` (or `ACE_HOST_DAT_DIRECTORY`), and an untracked `docker.env` in the repository root
(copy `docker.env.example`). The database login comes from `docker.env`; nothing secret is in git.

From the repository root, in order:

```bash
./scripts/market/bootstrap.sh   # start MySQL; create ace_market_auth and ace_market_shard with the market schema and the development marker
./scripts/market/game.sh        # build and run the game server on them (stays in the foreground; Ctrl+C stops it)
./scripts/market/api.sh         # in another terminal: build and start the market-api container, wait until healthy
./scripts/market/seed.sh        # accounts, characters, Vault items, balances and listings
./scripts/market/smoke.sh       # proves API -> database -> game -> database -> API
```

`game.sh` must have run once before `seed.sh`: it writes the configuration the seed tool reads
(`~/.local/state/legacey/market-host/Config.js`). Seeding while the game runs is safe.

## The smoke check

It signs in as `seedalpha`, asks for an MMD withdrawal to the offline character `Seed Alpha`, and expects the game
server to fail it `offline` within 10 seconds. It prints `SMOKE PASS`, or `SMOKE FAIL (<piece>)` naming the missing
piece: `database`, `schema`, `API`, `seed` or `game`.

## Ticket status walkthrough

With MySQL, Market API and website running and the game server stopped, run:

```bash
./scripts/market/dev.sh fixture --character "Seed Alpha"
```

The fixture first adds a temporary unknown-kind probe ticket and watches it for five seconds. If the game claims it, the fixture deletes the probe and refuses to continue. Otherwise it creates examples for WAITING, CLAIMED/Working, CLAIMED/Channelling, DONE, and FAILED (`offline` and `paused`). These rows demonstrate presentation only: the fixture does not touch Vault rows, balances, ledger entries or item rows. Sign in as `seedalpha` at `http://localhost:5173`, then reload any page to see the status panel rebuild the examples from the API. The channel sample has a 60-second display countdown; it does not run a game channel.

The separate `./scripts/market/smoke.sh` uses the real game process. It requests an MMD withdrawal for offline `Seed Alpha` and waits for the game to report `offline`.

## After an API code change

```bash
./scripts/market/api.sh         # rebuilds the image and restarts market-api alone
```

Signed-in sessions (the cookie keys) and cached icons are kept in the named volumes `market-api-keys` and
`market-api-icons`, so they survive the rebuild. Automated API tests still run in-process (`dotnet test Source/ACE.MarketApi.Tests`).

## Seed data

`seed.sh` creates two accounts with the password `marketdev` (or `MARKET_SEED_PASSWORD`, which the smoke check uses too):

- `seedalpha`: characters `Seed Alpha` and `Seed Alpha Second`; five Vault items, four listed (one by the second character);
- `seedbravo`: characters `Seed Bravo` and `Seed Bravo Second`; three Vault items, two listed.

Each account gets 1000 MMD through an `admin_adjust` transfer, and the ledger audit runs at the end. The items are
real items made from world weenies and escrowed the way a deposit leaves them. The characters are bare human
characters for the website; they aren't meant for playing with a real client. Running `seed.sh` again writes nothing.

## The development guard

The seed tool (`Source/ACE.MarketDev`) writes the database directly, so before any write it checks that the auth and
shard databases it would write are:

- at an allowed endpoint (`127.0.0.1:3310` by default; `MARKET_DEV_ALLOWED_ENDPOINTS`);
- named exactly as allowed for their role (`ace_market_auth`, `ace_market_shard`; `MARKET_DEV_ALLOWED_AUTH_DATABASES`, `MARKET_DEV_ALLOWED_SHARD_DATABASES`);
- marked as development databases (a `legacey_dev_marker` row).

If any check fails it writes nothing and names the failed check. `bootstrap.sh` marks the databases it creates.
To mark existing ones, run `./scripts/market/dev.sh mark` at a terminal and type `MARK`. Never mark a server's
database: the guard is a safeguard against mistakes, and a tunnel to a marked database would pass it.
`./scripts/market/dev.sh check` runs the checks without writing.

## Starting over

```bash
docker compose -f docker/docker-compose.local.yml exec ace-db sh -c 'MYSQL_PWD="$MYSQL_PASSWORD" mysql -u"$MYSQL_USER" -e "DROP DATABASE ace_market_auth; DROP DATABASE ace_market_shard;"'
./scripts/market/bootstrap.sh
```

Stop the game server first, and restart it afterwards. The game server records applied update scripts in its build
output (`DatabaseSetupScripts/Updates/Shard/applied_updates.txt`), not in the database, so it won't reapply ACE's
older shard updates to a recreated shard. `bootstrap.sh` applies both market schema scripts itself, so the market doesn't depend on that file.

Settings: `MARKET_AUTH_DATABASE`, `MARKET_SHARD_DATABASE`, `MARKET_API_PORT`, `DB_HOST_PORT`, `MARKET_GAME_RUN_DIR`.

### Website

After `scripts/market/api.sh`, run `scripts/market/web.sh`. Open
`http://localhost:5173` (override with `MARKET_WEB_PORT`). This is the website
and API's single origin; the website proxies `/api` to `market-api:8080`.
Only loopback is published. The source is mounted with polling for live reload;
the lockfile installs dependencies into the named `market-web-dependencies`
volume at startup. No host Node installation is needed.

Type checking and the static production build use the same image:

```sh
docker compose --env-file docker.env -f docker/docker-compose.local.yml exec -T market-web npm run typecheck
docker compose --env-file docker.env -f docker/docker-compose.local.yml exec -T market-web npm run build
```

The build writes `market-web/dist` (HTML, CSS and JavaScript only). Production
hosting must send SPA routes such as `/listing/1` to `index.html` and proxy
`/api` to the API. Deployment is outside this local spec.

Browser regression checks run in the browser image (also no host Node):

```sh
docker run --rm --ipc=host -v "$PWD/market-web:/app" -v market-browser-dependencies:/app/node_modules -w /app mcr.microsoft.com/playwright:v1.55.1-noble sh -c 'npm ci && npm run test:browser'
```

When developing from another worktree, run the stack scripts from the main
checkout. Set `MARKET_WEB_SOURCE` to that worktree's absolute `market-web`
path for the source bind mount. Rebuild the API with the worktree's build
context and `--no-deps`; never start or recreate `ace-db` from that worktree.
