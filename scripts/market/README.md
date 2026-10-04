# Local market stack

Runs the whole market on one machine, as separate processes:

- MySQL in Docker (`ace-db`, `127.0.0.1:3310`, from `docker/docker-compose.local.yml`; never the `ace-db` on 3306);
- the game server, natively, on the market stack's own databases;
- the Market API in its own Docker container (`market-api`, under `/api`), with **no host port**;
- the BFF (`market-web`): the website's own server, with live reload, on `127.0.0.1:5173`, the one local origin.

The browser only ever talks to the BFF. The BFF renders the pages, signs players in, keeps their session in its own signed cookie, checks
cross-site requests, and forwards an allowlist of API routes through `/api/*`. The Market API is private: only the BFF reaches it, on the
Docker network they share (`market`), and it answers only requests that carry its service key in `X-Market-Service-Key`, except
`GET /health`, which answers a bare `ok` for health checks.

Everything listens on loopback only. You need Docker, the .NET 10 SDK, `jq`, `curl`, the client DATs in
`~/ace_dats/retail` (or `ACE_HOST_DAT_DIRECTORY`), and an untracked `docker.env` in the repository root
(copy `docker.env.example`). The database login comes from `docker.env`; nothing secret is in git.

Set the two market secrets once in `docker.env`:

```bash
printf '\nMARKET_SERVICE_KEY=%s\n' "$(openssl rand -hex 32)" >> docker.env
printf '\nMARKET_COOKIE_SECRET=%s\n' "$(openssl rand -hex 32)" >> docker.env
```

Compose gives each container only its own: `market-api` gets the key (as `Market__ServiceKey`); `market-web` gets the key and the cookie
secret. `api.sh`, `web.sh` and `smoke.sh` stop with a message when one is missing or shorter than 32 characters, and the API and the BFF
refuse to start without them. Changing the cookie secret signs everyone out.

From the repository root, in order:

```bash
./scripts/market/bootstrap.sh   # start MySQL; create ace_market_auth and ace_market_shard with the market schema and the development marker
./scripts/market/game.sh        # build and run the game server on them (stays in the foreground; Ctrl+C stops it)
./scripts/market/api.sh         # in another terminal: build and start the market-api container, wait until Docker reports it healthy
./scripts/market/web.sh         # build and start the BFF, wait until it is healthy
./scripts/market/seed.sh        # accounts, characters, Vault items, balances and listings
./scripts/market/smoke.sh       # proves BFF -> API -> database -> game -> database -> API -> BFF
```

`game.sh` must have run once before `seed.sh`: it writes the configuration the seed tool reads
(`~/.local/state/legacey/market-host/Config.js`). Seeding while the game runs is safe.

## The smoke check

It goes through the BFF, as a browser would. It checks that the API container is healthy and publishes no port, that the BFF's
`/health` answers `ok`, that a route off the BFF's allowlist (`/api/tokens`) gets 404, and that the BFF reaches the API with the service
key. Then it signs in as `seedalpha` at the BFF's `/auth/sign-in`, asks for an MMD withdrawal to the offline character `Seed Alpha`
through `/api/*`, expects the game server to fail it `offline` within 10 seconds, and signs out. It prints `SMOKE PASS`, or
`SMOKE FAIL (<piece>)` naming the missing piece: `config`, `database`, `schema`, `API`, `BFF`, `seed` or `game`.

## Ticket status walkthrough

With MySQL, Market API and website running and the game server stopped, run:

```bash
./scripts/market/dev.sh fixture --character "Seed Alpha"
```

The fixture first adds a temporary unknown-kind probe ticket and watches it for five seconds. If the game claims it, the fixture deletes the probe and refuses to continue. Otherwise it creates one ticket in each state (WAITING, CLAIMED, awaiting confirmation, channelling, DONE) and one for every failure reason the game answers, with the game's own messages naming the account's first Vault item and the character. These rows demonstrate presentation only: the fixture writes `market_ticket` rows and nothing else, so a DONE withdrawal moved no Vault item, MMD or trade notes. Sign in as `seedalpha` at `http://localhost:5173`; the status panel shows every example while any is unfinished, and rebuilds them after a reload.

To watch one ticket change, move it forward (the same probe runs first):

```bash
./scripts/market/dev.sh fixture --ticket 63 --to channelling --seconds 20    # or claimed, awaiting_confirmation
./scripts/market/dev.sh fixture --ticket 63 --to failed --code interrupted   # or --to done
```

A WAITING ticket is claimed first, as the game would. Tickets only move forward: a finished ticket can't move, and `--code` must be a reason the game answers (an unknown one lists them). The countdown defaults to `vault_channel_seconds` for channelling and 30 seconds for a confirmation.

The separate `./scripts/market/smoke.sh` uses the real game process. It requests an MMD withdrawal for offline `Seed Alpha` and waits for the game to report `offline`.

## After an API code change

```bash
./scripts/market/api.sh         # rebuilds the image and restarts market-api alone
```

Signed-in sessions live in the database (`market_web_session`), so a rebuild of the API or the BFF signs nobody out. Cached icons are kept
in the named volume `market-api-icons`. Automated API tests still run in-process (`dotnet test Source/ACE.MarketApi.Tests`).

To call the API by hand, go through its container (it has no host port), e.g.
`docker compose --env-file docker.env -f docker/docker-compose.local.yml exec market-api curl -s http://127.0.0.1:8080/health`.

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
- named exactly as allowed for their role (`ace_market_auth`, `ace_market_shard`, `ace_market_e2e_auth`, `ace_market_e2e_shard`; `MARKET_DEV_ALLOWED_AUTH_DATABASES`, `MARKET_DEV_ALLOWED_SHARD_DATABASES`);
- marked as development databases (a `legacey_dev_marker` row).

If any check fails it writes nothing and names the failed check. `bootstrap.sh` marks the databases it creates.
To mark existing ones, run `./scripts/market/dev.sh mark` at a terminal and type `MARK`. Never mark a server's
database: the guard is a safeguard against mistakes, and a tunnel to a marked database would pass it.
`./scripts/market/dev.sh check` runs the checks without writing.

## Production end-to-end stack

The separate production-image stack runs at `http://127.0.0.1:5174`. It shares only the already-running `docker-ace-db-1` MySQL container and
uses `ace_market_e2e_auth` and `ace_market_e2e_shard`; it doesn't recreate or remove the development stack.

```bash
./scripts/market/e2e.sh up       # fresh seeded data, then start the production API and BFF images and run the smoke check
./scripts/market/e2e.sh fresh    # drop/recreate only the fixed e2e pair, apply schemas, mark and seed (a running stack is stopped, then restarted)
./scripts/market/e2e.sh smoke    # sign in as website-desktop-alpha and browse listings through the production BFF
./scripts/market/e2e.sh audit    # run the market ledger audit against the e2e shard
./scripts/market/e2e.sh down     # remove only the market-e2e Compose project
```

`fresh` refuses any endpoint other than local MySQL at `127.0.0.1:3310` and any database names other than the fixed e2e pair before it
starts the reset script. The guard also refuses a configuration that mixes the development and e2e databases (one of each), so `seed` and
`fixture` can't write e2e accounts into a development database. The seed creates an independent Alpha/Bravo pair for each test file and
viewport project; the current `website` suite has `website-desktop-alpha` / `website-desktop-bravo` and `website-phone-alpha` /
`website-phone-bravo`. Add a file's projects to `E2EPairs` in `Source/ACE.MarketDev/Seeder.cs` when the suite gains one. Set
`MARKET_SEED_PASSWORD` for a different local test password.

To run the ticket walkthrough on the e2e data, use its seeded character name:

```bash
MARKET_DEV_CONFIG="$HOME/.local/state/legacey/market-e2e/Config.js" ./scripts/market/dev.sh fixture --character "website desktop Alpha"
```

The production E2E API uses a separate `market_e2e_api` MySQL login. Its randomly generated password is stored in a mode-600
`api-db.env` file under the E2E run directory (or `MARKET_E2E_API_CREDENTIALS_FILE`); the parent directory is mode 700. The E2E
runner recreates the login with write access only to `ace_market_e2e_auth` and `ace_market_e2e_shard`, and `SELECT` access to
`ace_world`. The local development API continues to use its existing `MYSQL_USER`/`MYSQL_PASSWORD` login. The generated `Config.js`
holds the application login's password and is created mode 600.

The BFF production image builds the framework output in a Node build stage and runs it with production dependencies as the unprivileged
`node` user. API address and both BFF secrets are passed at runtime through the environment; no secrets enter either image.

## Pull-request checks and the local gate

Every pull request into `master`, `marketplace` or a `marketplace-*` branch runs `.github/workflows/pull-request.yml`: the .NET
solution build, the website's `npm ci`, type check and production build, `npm run test:bff` (the BFF's request tests, the unit tests and
the component tests) and `scripts/market/openapi-drift.sh`, which rebuilds the OpenAPI document and the generated client and fails when
either differs from the checkout. None of it needs DATs, MySQL or game data.

Before opening the pull request, run the local gate from the repository root:

```bash
./scripts/market/gate.sh                # everything the workflow runs, the .NET test suites, the fake-API browser checks, the e2e suite
./scripts/market/gate.sh --play-test    # also the automated play-test (needs the game server running)
```

It stops at the first failure and ends with a Markdown summary to paste into the pull request (also saved as `summary.md` beside the
per-test results, under `~/.local/state/legacey/market-gate/<UTC time>`, or `--results-dir`). The end-to-end stack is taken down even when
a step fails. The fake-API browser checks run on `MARKET_WEB_TEST_PORT` (default 5187, beside the dev BFF on 5173). Each test suite's
per-test results are reconciled with its exit status and the exact list in `gate-known-failures.txt`: a failure not on the list stops the
gate and names the test, and so does a listed failure that starts passing (remove it from the list). The play-test step is off unless
asked for (`--play-test` or `MARKET_GATE_PLAY_TEST=1`); `play-test.sh` is a placeholder that reports "not implemented" and fails until
the automated play-test lands.

## Starting over

```bash
docker compose -f docker/docker-compose.local.yml exec ace-db sh -c 'MYSQL_PWD="$MYSQL_PASSWORD" mysql -u"$MYSQL_USER" -e "DROP DATABASE ace_market_auth; DROP DATABASE ace_market_shard;"'
./scripts/market/bootstrap.sh
```

Stop the game server first, and restart it afterwards. The game server records applied update scripts in its build
output (`DatabaseSetupScripts/Updates/Shard/applied_updates.txt`), not in the database, so it won't reapply ACE's
older shard updates to a recreated shard. `bootstrap.sh` applies the market schema scripts itself (the schema, ticket progress and web sessions), so the market doesn't depend on that file.

Settings: `MARKET_AUTH_DATABASE`, `MARKET_SHARD_DATABASE`, `MARKET_WEB_PORT`, `DB_HOST_PORT`, `MARKET_GAME_RUN_DIR`.

### Website (the BFF)

After `scripts/market/api.sh`, run `scripts/market/web.sh`. Open `http://localhost:5173` (override with `MARKET_WEB_PORT`). This is the
one local origin: the BFF serves the pages and its own `/auth/sign-in`, `/auth/sign-out`, `/health` and `/api/*` routes. Only loopback is
published. The source is mounted with polling for live reload; the lockfile installs dependencies into the named `market-web-dependencies`
volume at startup. The container runs as the image's `node` user, so files the dev server writes into the checkout (React Router's
`.react-router/` route types) are yours. No host Node installation is needed.

The BFF's settings are environment variables only (`market-web/src/bff/settings.server.ts`): `MARKET_API_URL`, `MARKET_SERVICE_KEY`,
`MARKET_COOKIE_SECRET`, and optionally `MARKET_TRUSTED_PROXY` (a reverse proxy's address, whose right-most `X-Forwarded-For` entry is then
the player's), `MARKET_SITE_ORIGIN` (the public origin, behind such a proxy) and `MARKET_GATEWAY_IS_LOOPBACK`. Locally there is no proxy;
the port is published on the host's `127.0.0.1`, and Docker delivers those connections from the network's gateway, so compose sets
`MARKET_GATEWAY_IS_LOOPBACK=true` and the API counts local players as `127.0.0.1`, not the gateway.

Type checking, the production build and the BFF's request tests use the same image:

```sh
docker compose --env-file docker.env -f docker/docker-compose.local.yml exec -T market-web npm run typecheck
docker compose --env-file docker.env -f docker/docker-compose.local.yml exec -T market-web npm run build
docker compose --env-file docker.env -f docker/docker-compose.local.yml exec -T market-web npm run test:bff
```

The build writes `market-web/build` (`build/server` for the BFF's server, `build/client` for the browser's files); `npm start` runs it.

Browser regression checks run in the browser image (also no host Node). They start the BFF themselves, against a fake Market API that
each check scripts, so they need no database:

```sh
docker run --rm --ipc=host -v "$PWD/market-web:/app" -v market-browser-dependencies:/app/node_modules -w /app mcr.microsoft.com/playwright:v1.55.1-noble sh -c 'npm ci && npm run test:browser'
```

When developing from another worktree, run the stack scripts from the main
checkout. Set `MARKET_WEB_SOURCE` to that worktree's absolute `market-web`
path for the source bind mount. Rebuild the API with the worktree's build
context and `--no-deps`; never start or recreate `ace-db` from that worktree.
