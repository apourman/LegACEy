# ACE server scripts

The server runs directly on the host. MySQL runs in Docker and is initialized from the
repository's `Database` SQL files.

From the repository root:

```bash
./scripts/db-bootstrap/bootstrap.sh       # start and initialize Docker MySQL
./scripts/ace-server/rebuild.sh           # start DB and build the local server
./scripts/ace-server/run-host.sh          # start DB, build, and run the local server
./scripts/ace-server/restart.sh           # rebuild config and run an existing build
```

The host runner expects compatible client DAT files in `~/ace_dats/retail`; override that with
`ACE_DAT_FILES_DIRECTORY`. Runtime configuration and logs are kept under
`~/.local/state/legacey` and mods under `~/.local/share/legacey`. Override those locations with
`ACE_HOST_RUN_DIR`, `ACE_HOST_LOG_DIR`, and `ACE_MODS_DIRECTORY`.

The first database bootstrap creates `ace_auth`, `ace_shard`, and `ace_world`, imports the base
schemas, seeds the world version row, and creates the configured application user. Existing data
is preserved in the named `db-data` Docker volume.
