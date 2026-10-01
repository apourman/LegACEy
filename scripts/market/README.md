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

## After an API code change

```bash
./scripts/market/api.sh         # rebuilds the image and restarts market-api alone
```

Signed-in sessions (the cookie keys) and cached icons are kept in the named volumes `market-api-keys` and
`market-api-icons`, so they survive the rebuild. Automated API tests still run in-process (`dotnet test Source/ACE.MarketApi.Tests`).

## Seed data

`seed.sh` creates two accounts with the password `marketdev` (or `--password`, or `MARKET_SEED_PASSWORD`):

- `seedalpha`: characters `Seed Alpha` and `Seed Alpha Second`; five Vault items, four listed (one by the second character);
- `seedbravo`: characters `Seed Bravo` and `Seed Bravo Second`; three Vault items, two listed.

Each account gets 1000 MMD through an `admin_adjust` transfer, and the ledger audit runs at the end. The items are
real items made from world weenies and escrowed the way a deposit leaves them. The characters are bare human
characters for the website; they aren't meant for playing with a real client. Running `seed.sh` again writes nothing.

## The development guard

The seed tool (`Source/ACE.MarketDev`) writes the database directly, so before any write it checks that the auth and
shard databases it would write are:

- at an allowed endpoint (`127.0.0.1:3310` by default; `MARKET_DEV_ALLOWED_ENDPOINTS`);
- named exactly as allowed (`ace_market_auth`, `ace_market_shard`; `MARKET_DEV_ALLOWED_DATABASES`);
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
older shard updates to a recreated shard. `bootstrap.sh` applies the market script itself, so the market doesn't depend on that file.

Settings: `MARKET_AUTH_DATABASE`, `MARKET_SHARD_DATABASE`, `MARKET_API_PORT`, `DB_HOST_PORT`, `MARKET_GAME_RUN_DIR`.
