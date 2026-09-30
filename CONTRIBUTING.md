# Contributing

## Commit messages and releases

Use [Conventional Commits](https://www.conventionalcommits.org/) for changes
that should appear in release notes. A `feat:` commit proposes a minor release,
and a `fix:` commit proposes a patch release. Use `BREAKING CHANGE:` in the
commit footer for a breaking change; before version 1.0 this proposes a minor
release.

Release Please opens or updates a version and changelog pull request after
changes reach `master`. Merging that pull request creates a draft GitHub
Release. The workflow builds the tagged source, attaches a Linux x64 server
archive and SHA-256 checksum, publishes the release, and then deploys the same
published archive to production.

Do not edit `VERSION` or `LEGACEy_CHANGELOG.md` by hand or create release tags
manually; Release Please manages them. The upstream `changelog.md` is kept
separate and unchanged.
